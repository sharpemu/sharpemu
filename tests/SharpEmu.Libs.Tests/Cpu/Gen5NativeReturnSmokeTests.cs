// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
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

        var result = await RunIsolatedWorker(nameof(SyntheticGen5Entry_ReturnsToHost));

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

    [Fact]
    public async Task ExtractAndBlendPreserveLivePointerThroughNativeExecution()
    {
        if (!IsSupportedHost || !Avx2.IsSupported)
            return;

        if (Environment.GetEnvironmentVariable(WorkerEnvironmentVariable) != "1")
        {
            var result = await RunIsolatedWorker(nameof(ExtractAndBlendPreserveLivePointerThroughNativeExecution));
            Assert.True(result.Completed, result.Output);
            Assert.True(result.ExitCode == 0, result.Output);
            return;
        }

        // Keep a data pointer in RAX while the vector instructions process a color value.
        // Read through that pointer after the extract and blend complete.
        ExecuteSyntheticGuest([
            0x48, 0x89, 0xF8,                         // mov rax,rdi
            0x89, 0xD1, 0xC1, 0xE9, 0x08,             // mov ecx,edx; shr ecx,8
            0xC5, 0xF9, 0x6E, 0xC2,                   // vmovd xmm0,edx
            0xC4, 0xE3, 0x79, 0x22, 0xC9, 0x01,       // vpinsrd xmm1,xmm0,ecx,1
            0xC5, 0xF9, 0x72, 0xD0, 0x10,             // vpsrld xmm0,xmm0,16
            0x66, 0x0F, 0x78, 0xC1, 0x28, 0x00,       // extrq xmm1,40,0
            0xC4, 0xE3, 0x79, 0x02, 0xC1, 0x02,       // vpblendd xmm0,xmm0,xmm1,2
            0xC5, 0xFB, 0x10, 0x48, 0x10,             // vmovsd xmm1,[rax+16]
            0x66, 0x48, 0x0F, 0x7E, 0xC8,             // movq rax,xmm1
            0xC3
        ]);
    }

    [Fact]
    public async Task SyntheticGen5Continuation_PreservesFullReturnValue()
    {
        if (!IsSupportedHost)
            return;

        if (Environment.GetEnvironmentVariable(WorkerEnvironmentVariable) != "1")
        {
            var result = await RunIsolatedWorker(nameof(SyntheticGen5Continuation_PreservesFullReturnValue));
            Assert.True(result.Completed, result.Output);
            Assert.True(result.ExitCode == 0, result.Output);
            return;
        }

        ExecuteSyntheticGuest(continuation: true);
    }

    private static void ExecuteSyntheticGuest(byte[]? callbackInstructions = null, bool continuation = false)
    {
        using var memory = new PhysicalVirtualMemory();
        var image = new SelfLoader().Load(BuildSyntheticElf(callbackInstructions), memory);
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
        if (continuation)
        {
            Assert.True(memory.TryAllocateAtOrAbove(0x1_0000_0000, 0x4000, false, 0x4000, out var stack));
            callerContext[CpuRegister.Rsp] = stack + 0x4000 - sizeof(ulong);
            // Resume directly into mov rax, imm64; ret through the shared guest return stub.
            var execute = typeof(DirectExecutionBackend).GetMethod(
                "ExecuteGuestContinuationEntry", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object?[] arguments = [callerContext, image.EntryPoint + 3, callerContext[CpuRegister.Rsp],
                "synthetic-native-continuation-return", null];
            var exitReason = execute.Invoke(backend, arguments);
            Assert.True(exitReason?.ToString() == "Returned", arguments[4]?.ToString());
            Assert.Equal(CallbackReturnValue, callerContext[CpuRegister.Rax]);
            return;
        }

        ulong dataAddress = 0;
        var recoveryCounter = typeof(DirectExecutionBackend).GetField(
            "_sse4aInstructionsEmulated", BindingFlags.Static | BindingFlags.NonPublic)!;
        var recoveriesBefore = (long)recoveryCounter.GetValue(null)!;
        if (callbackInstructions is not null)
        {
            var loadedInstructions = new byte[callbackInstructions.Length];
            Assert.True(memory.TryRead(image.EntryPoint + 3, loadedInstructions));
            Assert.Equal(callbackInstructions, loadedInstructions);
            Assert.True(memory.TryAllocateAtOrAbove(0x1_0000_0000, 0x4000, false, 0x4000, out dataAddress));
            Assert.True(memory.TryWriteUInt64(dataAddress + 16, CallbackReturnValue));
        }

        Assert.True(
            backend.TryCallGuestFunction(
                callerContext,
                image.EntryPoint + 3,
                dataAddress,
                0,
                callbackInstructions is not null ? 0x00FF9300UL : 0,
                0,
                0,
                "synthetic-native-callback-return",
                out var callbackReturn,
                out var callbackError),
            callbackError);
        Assert.Equal(CallbackReturnValue, callbackReturn);
        if (callbackInstructions is not null)
        {
            var recoveriesAfter = (long)recoveryCounter.GetValue(null)!;
            var supportsSse4a = (X86Base.CpuId(unchecked((int)0x80000001), 0).Ecx & (1 << 6)) != 0;
            Assert.Equal(supportsSse4a ? 0L : 1L, recoveriesAfter - recoveriesBefore);
        }
    }

    private static async Task<WorkerResult> RunIsolatedWorker(string testMethod)
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
            $"FullyQualifiedName={typeof(Gen5NativeReturnSmokeTests).FullName}.{testMethod}");
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

    private static byte[] BuildSyntheticElf(byte[]? callbackInstructions)
    {
        const int elfHeaderSize = 0x40;
        const int programHeaderSize = 0x38;
        const int fileOffset = 0x1000;
        const ulong entryPoint = 0x1000;
        Span<byte> payload = new byte[3 + (callbackInstructions?.Length ?? 11)];
        payload[0] = 0x31; // xor eax, eax
        payload[1] = 0xC0;
        payload[2] = 0xC3; // ret
        if (callbackInstructions is not null)
        {
            callbackInstructions.CopyTo(payload[3..]);
        }
        else
        {
            payload[3] = 0x48; // mov rax, imm64
            payload[4] = 0xB8;
            BinaryPrimitives.WriteUInt64LittleEndian(payload[5..], CallbackReturnValue);
            payload[13] = 0xC3; // ret
        }
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
