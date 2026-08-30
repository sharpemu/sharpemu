// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class Gen5NativeReturnSmokeTests
{
    private const ulong CallbackReturnValue = 0xFEDC_BA98_7654_3210UL;
    private const string WorkerEnvironmentVariable = "SHARPEMU_NATIVE_RETURN_SMOKE_WORKER";
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task SyntheticGen5Entry_ReturnsToHost()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable(WorkerEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            ExecuteSyntheticGuest();
            return;
        }

        var result = await RunIsolatedWorker();

        Assert.True(
            result.Completed,
            $"native return worker did not exit within {WorkerTimeout.TotalSeconds:F0} seconds\n{result.Output}");
        Assert.True(
            result.ExitCode == 0,
            $"native return worker exited with code {result.ExitCode}\n{result.Output}");
    }

    private static bool IsSupportedHost =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    private static void ExecuteSyntheticGuest()
    {
        using var memory = new PhysicalVirtualMemory();
        var image = new SelfLoader().Load(BuildSyntheticElf(), memory);
        Assert.Equal((byte)2, image.ElfHeader.AbiVersion);
        Assert.Equal(0x0000_0008_0000_1000UL, image.EntryPoint);

        var moduleManager = new ModuleManager();
        moduleManager.Freeze();

        var backend = new DirectExecutionBackend(moduleManager);
        using var dispatcher = new CpuDispatcher(memory, moduleManager, backend);
        var result = dispatcher.DispatchEntry(
            image.EntryPoint,
            Generation.Gen5,
            image.ImportStubs,
            image.RuntimeSymbols,
            "synthetic-native-return",
            new CpuExecutionOptions
            {
                CpuEngine = CpuExecutionEngine.NativeOnly,
                EnableDisasmDiagnostics = false,
                StrictDynlibResolution = true,
                ImportTraceLimit = 0,
                DebugHook = null
            });

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, dispatcher.LastSessionSummary.Result);
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
        Assert.Equal(0, dispatcher.LastSessionSummary.ImportsHit);
        Assert.Equal(0, dispatcher.LastSessionSummary.UniqueNidsHit);
        Assert.Null(dispatcher.LastTrapInfo);
        Assert.Null(dispatcher.LastMemoryFaultInfo);
        Assert.Null(dispatcher.LastNotImplementedInfo);

        var callerContext = new CpuContext(new TrackedCpuMemory(memory), Generation.Gen5);
        Assert.True(
            backend.TryCallGuestFunction(
                callerContext,
                image.EntryPoint + 3,
                0,
                0,
                0,
                0,
                0,
                "synthetic-native-callback-return",
                out var callbackReturn,
                out var callbackError),
            callbackError);
        Assert.Equal(CallbackReturnValue, callbackReturn);
    }

    private static async Task<WorkerResult> RunIsolatedWorker()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(typeof(Gen5NativeReturnSmokeTests).Assembly.Location);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add(
            $"FullyQualifiedName={typeof(Gen5NativeReturnSmokeTests).FullName}.{nameof(SyntheticGen5Entry_ReturnsToHost)}");
        startInfo.Environment[WorkerEnvironmentVariable] = "1";
        startInfo.Environment["SHARPEMU_SENTINEL_PROBE"] = null;

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the isolated native return worker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(WorkerTimeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return new WorkerResult(false, process.ExitCode, await ReadOutput(stdout, stderr));
        }

        return new WorkerResult(true, process.ExitCode, await ReadOutput(stdout, stderr));
    }

    private static string ResolveDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            return configuredHost;
        }

        var processPath = Environment.ProcessPath;
        if (processPath is not null &&
            string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }

    private static async Task<string> ReadOutput(Task<string> stdout, Task<string> stderr) =>
        await stdout + await stderr;

    private static byte[] BuildSyntheticElf()
    {
        const int elfHeaderSize = 0x40;
        const int programHeaderSize = 0x38;
        const int fileOffset = 0x1000;
        const ulong entryPoint = 0x1000;
        Span<byte> payload = stackalloc byte[14];
        payload[0] = 0x31; // xor eax, eax
        payload[1] = 0xC0;
        payload[2] = 0xC3; // ret
        payload[3] = 0x48; // mov rax, imm64
        payload[4] = 0xB8;
        BinaryPrimitives.WriteUInt64LittleEndian(payload[5..], CallbackReturnValue);
        payload[13] = 0xC3; // ret
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

    private sealed record WorkerResult(bool Completed, int ExitCode, string Output);
}
