// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// Runs one fault test in a child process so the installed host fault handler is the real one.
internal static class GuestFaultWorker
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    public const string Completed = "completed";
    public const string Skipped = "skipped";
    private const string ReportVariable = "SHARPEMU_FAULT_WORKER_REPORT";

    public static bool CanRun => RuntimeInformation.ProcessArchitecture == Architecture.X64 && OperatingSystem.IsWindows();

    public static bool IsWorker(string variable) =>
        string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal);

    // The child writes its outcome to a report file; exit code zero alone does not prove the sequence ran.
    public static async Task RunIsolatedAsync(string variable, Type test, string method)
    {
        var report = Path.GetTempFileName();
        try
        {
            await RunIsolatedAsync(variable, test, method, report);
        }
        finally
        {
            File.Delete(report);
        }
    }

    private static async Task RunIsolatedAsync(string variable, Type test, string method, string report)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(test.Assembly.Location);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add($"FullyQualifiedName={test.FullName}.{method}");
        startInfo.Environment[variable] = "1";
        startInfo.Environment[ReportVariable] = report;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the fault worker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var completed = true;
        try
        {
            await process.WaitForExitAsync().WaitAsync(Timeout);
        }
        catch (TimeoutException)
        {
            completed = false;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var output = await stdout + await stderr;
        Assert.True(completed, $"fault worker did not exit within {Timeout.TotalSeconds:F0} seconds\n{output}");
        Assert.True(process.ExitCode == 0, $"fault worker exited with code {process.ExitCode}\n{output}");
        var outcome = File.ReadAllText(report);
        Assert.True(outcome.StartsWith(Completed, StringComparison.Ordinal) || outcome.StartsWith(Skipped, StringComparison.Ordinal), $"fault worker left no outcome report\n{output}");
        Assert.False(outcome.StartsWith(Skipped, StringComparison.Ordinal) && ReferenceShaders.Required, $"the required gate cannot run without the fault worker: {outcome}");
    }

    // Called by the child at the end of its sequence, or when it has no device to run on.
    public static void Report(string outcome, string detail)
    {
        var line = $"{outcome} {detail}";
        var report = Environment.GetEnvironmentVariable(ReportVariable);
        if (report is null)
        {
            Console.Error.WriteLine(line);
            return;
        }

        File.WriteAllText(report, line);
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
    public static byte[] BuildSyntheticElf()
    {
        const int elfHeaderSize = 0x40;
        const int programHeaderSize = 0x38;
        const int fileOffset = 0x1000;
        const ulong entryPoint = 0x1000;
        ReadOnlySpan<byte> payload =
        [
            0x31, 0xC0, 0xC3, 0x48, 0x8B, 0x07, 0xC3, 0x48, 0x89, 0x37, 0xC3,
            // Signal readiness, wait for release, then read or write the target.
            0xC7, 0x02, 1, 0, 0, 0, 0x83, 0x39, 0, 0x74, 0xFB, 0x48, 0x8B, 0x07, 0xC3,
            0xC7, 0x02, 1, 0, 0, 0, 0x83, 0x39, 0, 0x74, 0xFB, 0x48, 0x89, 0x37, 0xC3,
        ];
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

// The loaded synthetic program: one read routine and one write routine that run as guest code.
internal sealed class SyntheticGuest : IDisposable
{
    private readonly CacheHarness _harness;
    private readonly DirectExecutionBackend _backend;
    private readonly CpuDispatcher _dispatcher;
    private readonly CpuContext _context;
    private readonly ulong _readRoutine;
    private readonly ulong _writeRoutine;
    private readonly ulong _delayedReadRoutine;
    private readonly ulong _delayedWriteRoutine;

    public SyntheticGuest(CacheHarness harness, string name)
    {
        _harness = harness;
        var image = new SelfLoader().Load(GuestFaultWorker.BuildSyntheticElf(), harness.Memory);
        var moduleManager = new ModuleManager();
        moduleManager.Freeze();
        _backend = new DirectExecutionBackend(moduleManager);
        _dispatcher = new CpuDispatcher(harness.Memory, moduleManager, _backend);
        var result = _dispatcher.DispatchEntry(
            image.EntryPoint,
            Generation.Gen5,
            image.ImportStubs,
            image.RuntimeSymbols,
            name,
            new CpuExecutionOptions
            {
                CpuEngine = CpuExecutionEngine.NativeOnly,
                EnableDisasmDiagnostics = false,
                StrictDynlibResolution = true,
                ImportTraceLimit = 0,
                DebugHook = null,
            });
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        _context = new CpuContext(new TrackedCpuMemory(harness.Memory), Generation.Gen5);
        _readRoutine = image.EntryPoint + 3;
        _writeRoutine = image.EntryPoint + 7;
        _delayedReadRoutine = image.EntryPoint + 11;
        _delayedWriteRoutine = image.EntryPoint + 26;
    }

    public ulong Read(ulong address)
    {
        Assert.True(_backend.TryCallGuestFunction(_context, _readRoutine, address, 0, 0, 0, 0, "guest-read", out var value, out var error), error);
        return value;
    }

    public void Write(ulong address, ulong value) =>
        Assert.True(_backend.TryCallGuestFunction(_context, _writeRoutine, address, value, 0, 0, 0, "guest-write", out _, out var error), error);

    public ulong AccessAfterSignal(bool write, ulong address, ulong value, ulong readyAddress, ulong releaseAddress)
    {
        var routine = write ? _delayedWriteRoutine : _delayedReadRoutine;
        Assert.True(_backend.TryCallGuestFunction(_context, routine, address, value, readyAddress, releaseAddress, 0, 0,
            "guest-delayed-access", out var result, out var error), error);
        return result;
    }

    // Runs one guest access per thread on private guest stacks, all released by one barrier.
    public (bool Ok, ulong Value, string? Error)[] Race(int threads, bool write, ulong address, Func<int, ulong> valueOf)
    {
        using var start = new Barrier(threads);
        var outcomes = new (bool Ok, ulong Value, string? Error)[threads];
        var racers = new Thread[threads];
        for (var index = 0; index < threads; index++)
        {
            var slot = index;
            Assert.True(_harness.Memory.TryAllocateAtOrAbove(0x3_0000_0000, 0x10000, executable: false, 0x4000, out var stack));
            racers[slot] = new Thread(() =>
            {
                var racer = new CpuContext(new TrackedCpuMemory(_harness.Memory), Generation.Gen5);
                start.SignalAndWait();
                var routine = write ? _writeRoutine : _readRoutine;
                var ok = _backend.TryCallGuestFunction(racer, routine, address, write ? valueOf(slot) : 0, 0, 0, stack, 0x10000, $"guest-race-{slot}", out var value, out var error);
                outcomes[slot] = (ok, value, error);
            });
            racers[slot].Start();
        }

        foreach (var racer in racers)
        {
            Assert.True(racer.Join(GuestFaultWorker.Timeout), "a racing guest access did not return");
        }

        return outcomes;
    }

    public void Dispose() => _dispatcher.Dispose();
}
