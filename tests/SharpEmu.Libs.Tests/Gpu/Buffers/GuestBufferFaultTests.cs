// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// Guest code on a guest stack is the only context whose faults reach the store; a child
// process runs it so the host fault handler is the real one.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferFaultTests
{
    private const string WorkerEnvironmentVariable = "SHARPEMU_BUFFER_FAULT_WORKER";
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task GuestFaultsRecoverThroughTheBufferStore()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !OperatingSystem.IsWindows())
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(WorkerEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            RunGuest();
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(typeof(GuestBufferFaultTests).Assembly.Location);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add($"FullyQualifiedName={typeof(GuestBufferFaultTests).FullName}.{nameof(GuestFaultsRecoverThroughTheBufferStore)}");
        startInfo.Environment[WorkerEnvironmentVariable] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the fault worker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var completed = true;
        try
        {
            await process.WaitForExitAsync().WaitAsync(WorkerTimeout);
        }
        catch (TimeoutException)
        {
            completed = false;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var output = await stdout + await stderr;
        Assert.True(completed, $"fault worker did not exit within {WorkerTimeout.TotalSeconds:F0} seconds\n{output}");
        Assert.True(process.ExitCode == 0, $"fault worker exited with code {process.ExitCode}\n{output}");
    }

    private static void RunGuest()
    {
        using var vulkan = HeadlessVulkan.TryCreate();
        if (vulkan is null)
        {
            return;
        }

        using var harness = new CacheHarness(vulkan);
        using var fatal = new FatalScope();
        GuestGpuMemoryHook.Attach(harness.Gpu);
        try
        {
            var image = new SelfLoader().Load(BuildSyntheticElf(), harness.Memory);
            var moduleManager = new ModuleManager();
            moduleManager.Freeze();
            var backend = new DirectExecutionBackend(moduleManager);
            using var dispatcher = new CpuDispatcher(harness.Memory, moduleManager, backend);
            var result = dispatcher.DispatchEntry(
                image.EntryPoint,
                Generation.Gen5,
                image.ImportStubs,
                image.RuntimeSymbols,
                "buffer-fault-worker",
                new CpuExecutionOptions
                {
                    CpuEngine = CpuExecutionEngine.NativeOnly,
                    EnableDisasmDiagnostics = false,
                    StrictDynlibResolution = true,
                    ImportTraceLimit = 0,
                    DebugHook = null,
                });
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);

            var context = new CpuContext(new TrackedCpuMemory(harness.Memory), Generation.Gen5);
            var readRoutine = image.EntryPoint + 3;
            var writeRoutine = image.EntryPoint + 7;

            ulong GuestRead(ulong address)
            {
                Assert.True(backend.TryCallGuestFunction(context, readRoutine, address, 0, 0, 0, 0, "guest-read", out var value, out var error), error);
                return value;
            }

            void GuestWrite(ulong address, ulong value) =>
                Assert.True(backend.TryCallGuestFunction(context, writeRoutine, address, value, 0, 0, 0, "guest-write", out _, out var error), error);

            // Check write recovery before the first GPU download.
            var probe = harness.MapBacked(0x10000, ReadWrite);
            harness.Worker.Run(() => harness.Cache.ObtainBuffer(probe, 0x4100, isWritten: false));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(probe));
            GuestWrite(probe + 0x30, 0x1234);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(probe));
            Assert.Equal(0x1234UL, GuestRead(probe + 0x30));
            Assert.Contains("faults_resolved=1 ", GuestGpuMemoryHook.GetSummary());

            // Read fault: the GPU result is not readable until the fault downloads it.
            var backed = harness.MapBacked(0x10000, ReadWrite);
            harness.Worker.Run(() =>
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(backed, 0x100, isWritten: true);
                buffer.Fill(offset, 0x100, 0x11223344);
            });
            Assert.Equal(HostPageProtection.NoAccess, harness.Protection(backed));
            Assert.Equal(0x1122334411223344UL, GuestRead(backed + 8));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(backed));
            Assert.Contains("faults_resolved=2 ", GuestGpuMemoryHook.GetSummary());

            // Write fault: the page turns CPU-dirty, the write lands, and the next obtain re-uploads it.
            GuestWrite(backed + 0x10, 0xDEADBEEF);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(backed));
            Assert.Equal(0xDEADBEEFUL, GuestRead(backed + 0x10));
            Assert.Contains("faults_resolved=3 ", GuestGpuMemoryHook.GetSummary());
            var (reuploaded, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(backed, 0x4100, isWritten: false));
            Assert.Equal(0xDEADBEEFUL, BitConverter.ToUInt64(harness.ReadBack(reuploaded, 0x10, 8)));
            Assert.Equal(0x11223344u, BitConverter.ToUInt32(harness.ReadBack(reuploaded, 0x20, 4)));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(backed));

            // Private memory uploads on use and faults on writes; a GPU write into it is refused.
            var privateRange = harness.MapPrivate(0x10000);
            GuestWrite(privateRange + 0x20, 0x5555AAAA);
            var (privateBuffer, _) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(privateRange, 0x4100, isWritten: false));
            Assert.Equal(0x5555AAAAUL, BitConverter.ToUInt64(harness.ReadBack(privateBuffer, 0x20, 8)));
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(privateRange));
            GuestWrite(privateRange + 0x28, 1);
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(privateRange));
            Assert.Contains("faults_resolved=4 ", GuestGpuMemoryHook.GetSummary());
            harness.Worker.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Cache.ObtainBuffer(privateRange, 0x100, isWritten: true)));

            // Unrelated faults are declined at the hook the fault handler calls.
            var noAccess = harness.MapBacked(0x10000, GuestPageProtection.None);
            Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Read, noAccess + 8));
            Assert.False(GuestGpuMemoryHook.TryResolveFault(FaultKind.Write, 0x3_0000_0000));
            Assert.Contains("faults_declined=2", GuestGpuMemoryHook.GetSummary());

            // Lost race: two guest threads faulting the same GPU-dirty page through the live
            // handler both resume; the first hops to the worker while the second spins natively.
            var raced = harness.MapBacked(0x10000, ReadWrite);
            harness.Worker.Run(() =>
            {
                var (buffer, offset) = harness.Cache.ObtainBuffer(raced, 0x100, isWritten: true);
                buffer.Fill(offset, 0x100, 0x77777777);
            });
            using var start = new Barrier(2);
            var outcomes = new (bool Ok, ulong Value, string? Error)[2];
            var racers = new Thread[2];
            for (var index = 0; index < racers.Length; index++)
            {
                var slot = index;
                Assert.True(harness.Memory.TryAllocateAtOrAbove(0x3_0000_0000, 0x10000, executable: false, 0x4000, out var stack));
                racers[slot] = new Thread(() =>
                {
                    var racer = new CpuContext(new TrackedCpuMemory(harness.Memory), Generation.Gen5);
                    start.SignalAndWait();
                    var ok = backend.TryCallGuestFunction(racer, readRoutine, raced + 8, 0, 0, 0, stack, 0x10000, $"guest-read-race-{slot}", out var value, out var error);
                    outcomes[slot] = (ok, value, error);
                });
                racers[slot].Start();
            }

            foreach (var racer in racers)
            {
                Assert.True(racer.Join(WorkerTimeout), "a racing guest read did not return");
            }

            Assert.All(outcomes, outcome => Assert.True(outcome.Ok, outcome.Error));
            Assert.All(outcomes, outcome => Assert.Equal(0x7777777777777777UL, outcome.Value));
            Assert.Contains("faults_resolved=6 ", GuestGpuMemoryHook.GetSummary());
            Assert.Equal(HostPageProtection.ReadOnly, harness.Protection(raced));

            harness.Shutdown();
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(backed));
            Assert.Equal(HostPageProtection.ReadWrite, harness.Protection(privateRange));
            Assert.Equal(HostPageProtection.NoAccess, harness.Protection(noAccess));
            Console.Error.WriteLine(GuestGpuMemoryHook.GetSummary());
        }
        finally
        {
            GuestGpuMemoryHook.Attach(null);
        }
    }

    private static string ResolveDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            return configuredHost;
        }

        var processPath = Environment.ProcessPath;
        return processPath is not null && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? processPath
            : "dotnet";
    }

    // Entry: xor eax, eax; ret. +3: mov rax, [rdi]; ret. +7: mov [rdi], rsi; ret.
    private static byte[] BuildSyntheticElf()
    {
        const int elfHeaderSize = 0x40;
        const int programHeaderSize = 0x38;
        const int fileOffset = 0x1000;
        const ulong entryPoint = 0x1000;
        ReadOnlySpan<byte> payload = [0x31, 0xC0, 0xC3, 0x48, 0x8B, 0x07, 0xC3, 0x48, 0x89, 0x37, 0xC3];
        var image = new byte[fileOffset + payload.Length];

        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        image[7] = 9;
        image[8] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x20), elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), programHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), 1);

        var programHeader = image.AsSpan(elfHeaderSize, programHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[0x04..], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x08..], fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x10..], entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x18..], entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x20..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x28..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x30..], 0x1000);
        payload.CopyTo(image.AsSpan(fileOffset));
        return image;
    }
}
