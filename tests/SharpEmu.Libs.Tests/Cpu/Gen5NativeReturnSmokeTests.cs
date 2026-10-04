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

    [Fact]
    public Task RedZoneSentinelWithoutExceptionIsPreserved() =>
        CheckRedZoneSentinel(nameof(RedZoneSentinelWithoutExceptionIsPreserved), false, false);

    [Fact]
    public Task RedZoneSentinelWithProtectedExtractIsPreserved() =>
        CheckRedZoneSentinel(nameof(RedZoneSentinelWithProtectedExtractIsPreserved), true, true);

    [Fact]
    public Task RedZoneSentinelWithUnprotectedExtractIsPreserved() =>
        CheckRedZoneSentinel(nameof(RedZoneSentinelWithUnprotectedExtractIsPreserved), true, false);

    [Fact]
    public Task RedZoneSentinelWithRegisterExtractIsPreserved() =>
        CheckRedZoneSentinel(nameof(RedZoneSentinelWithRegisterExtractIsPreserved), true, false, true);

    [Fact]
    public async Task RegisterExtractRecoversAllFieldControlsWithNonzeroUpperLanes()
    {
        if (!OperatingSystem.IsWindows() || !IsSupportedHost)
            return;

        // This probe checks software recovery, not hardware results for undefined fields.
        if ((X86Base.CpuId(unchecked((int)0x80000001), 0).Ecx & (1 << 6)) != 0)
            return;

        if (Environment.GetEnvironmentVariable(WorkerEnvironmentVariable) != "1")
        {
            var result = await RunIsolatedWorker(nameof(RegisterExtractRecoversAllFieldControlsWithNonzeroUpperLanes));
            Assert.True(result.Completed, result.Output);
            Assert.True(result.ExitCode == 0, result.Output);
            return;
        }

        var instructions = new List<byte>
        {
            0xF3, 0x0F, 0x6F, 0x07,                   // movdqu xmm0,[rdi]
            0xF3, 0x0F, 0x6F, 0x4F, 0x10,             // movdqu xmm1,[rdi+16]
            0x66, 0x0F, 0x6F, 0xD0,                   // movdqa xmm2,xmm0
            0x48, 0xB8                                // mov rax,imm64
        };
        var returnValue = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(returnValue, CallbackReturnValue);
        instructions.AddRange(returnValue);
        instructions.AddRange([
            0x48, 0x39, 0xC0,                         // cmp rax,rax
            0x9C, 0x5E,                               // pushfq; pop rsi
            0x48, 0x89, 0x77, 0x50,                   // mov [rdi+80],rsi
            0x66, 0x0F, 0x79, 0xC1,                   // extrq xmm0,xmm1
            0x9C, 0x5E,
            0x48, 0x89, 0x77, 0x58,                   // mov [rdi+88],rsi
            0xF3, 0x0F, 0x7F, 0x47, 0x20,             // movdqu [rdi+32],xmm0
            0xF3, 0x0F, 0x7F, 0x4F, 0x30,             // movdqu [rdi+48],xmm1
            0xF3, 0x0F, 0x7F, 0x57, 0x40,             // movdqu [rdi+64],xmm2
            0xC3
        ]);
        ExecuteSyntheticGuest(instructions.ToArray(), checkExtractControls: true);
    }

    private static void CheckExtractControls(PhysicalVirtualMemory memory, DirectExecutionBackend backend,
        CpuContext context, ulong entryPoint, ulong dataAddress)
    {
        ulong[] values = [0, ulong.MaxValue, 0x8000_0000_0000_0000,
            0xAAAA_AAAA_AAAA_AAAA, 0x5555_5555_5555_5555, CallbackReturnValue];
        var input = new byte[96];
        var output = new byte[96];
        foreach (var value in values)
        for (var length = 0; length < 64; length++)
        for (var index = 0; index < 64; index++)
        {
            var upper = value ^ 0x1357_9BDF_2468_ACE0UL;
            var control = 0xFEDC_BA98_7654_C0C0UL | (uint)length | ((ulong)index << 8);
            BinaryPrimitives.WriteUInt64LittleEndian(input, value);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(8), upper);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16), control);
            BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24), ~upper);
            Assert.True(memory.TryWrite(dataAddress, input));
            Assert.True(backend.TryCallGuestFunction(context, entryPoint, dataAddress, 0, 0, 0, 0,
                "synthetic-extract-controls", out var returned, out var error), error);
            Assert.True(memory.TryRead(dataAddress, output));

            // Build the oracle from the low quadword only. Bits past bit 63 are zero.
            ulong expected = 0;
            var bitCount = length == 0 ? 64 : length;
            for (var bit = 0; bit < bitCount; bit++)
            {
                var position = index + bit;
                var source = position < 64 ? value >> position : 0UL;
                expected |= (source & 1UL) << bit;
            }
            var actual = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(32));
            var label = $"value=0x{value:X16}, length={length}, index={index}";
            Assert.True(expected == actual,
                $"{label}: expected 0x{expected:X16}, actual 0x{actual:X16}");
            Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(40)) == 0,
                $"{label}: destination upper half is not zero");
            Assert.True(input.AsSpan(16, 16).SequenceEqual(output.AsSpan(48, 16)),
                $"{label}: source XMM1 changed");
            Assert.True(input.AsSpan(0, 16).SequenceEqual(output.AsSpan(64, 16)),
                $"{label}: unrelated XMM2 changed");
            Assert.True(output.AsSpan(80, 8).SequenceEqual(output.AsSpan(88, 8)),
                $"{label}: RFLAGS changed");
            Assert.True(returned == CallbackReturnValue, $"{label}: RAX changed");
        }
    }

    private static async Task CheckRedZoneSentinel(string method, bool extract, bool protect,
        bool registerExtract = false)
    {
        if (!OperatingSystem.IsWindows() || !IsSupportedHost)
            return;

        if (Environment.GetEnvironmentVariable(WorkerEnvironmentVariable) != "1")
        {
            var result = await RunIsolatedWorker(method);
            Assert.True(result.Completed, result.Output);
            Assert.True(result.ExitCode == 0, result.Output);
            return;
        }

        // Keep the sentinel live below the guest stack pointer across the exception.
        // The protected control moves the exception frame below that storage.
        var instructions = new List<byte> { 0x48, 0xB8 };
        var sentinel = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(sentinel, CallbackReturnValue);
        instructions.AddRange(sentinel);
        for (var offset = 8; offset <= 128; offset += 8)
            instructions.AddRange([0x48, 0x89, 0x44, 0x24, unchecked((byte)-offset)]);
        instructions.AddRange([0x66, 0x0F, 0xEF, 0xC0]); // pxor xmm0,xmm0
        if (registerExtract)
        {
            instructions.AddRange([0xB9, 0x08, 0x00, 0x00, 0x00]); // mov ecx,8
            instructions.AddRange([0x66, 0x48, 0x0F, 0x6E, 0xC9]); // movq xmm1,rcx
        }
        if (protect)
            instructions.AddRange([0x48, 0x8D, 0x64, 0x24, 0x80]); // lea rsp,[rsp-128]
        if (extract)
            instructions.AddRange(registerExtract
                ? [0x66, 0x0F, 0x79, 0xC1]
                : [0x66, 0x0F, 0x78, 0xC0, 0x08, 0x00]);
        if (protect)
            instructions.AddRange([0x48, 0x8D, 0xA4, 0x24, 0x80, 0x00, 0x00, 0x00]);
        for (var offset = 8; offset <= 128; offset += 8)
        {
            instructions.AddRange([0x48, 0x8B, 0x4C, 0x24, unchecked((byte)-offset)]);
            instructions.AddRange([0x48, 0x89, 0x4F, (byte)(offset - 8)]);
        }
        instructions.Add(0xC3);
        ExecuteSyntheticGuest(instructions.ToArray(), expectedExtracts: extract ? 1 : 0,
            checkRedZone: true);
    }

    private static void ExecuteSyntheticGuest(byte[]? callbackInstructions = null, bool continuation = false,
        int expectedExtracts = 1, bool checkRedZone = false, bool checkExtractControls = false)
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

        if (checkExtractControls)
        {
            CheckExtractControls(memory, backend, callerContext, image.EntryPoint + 3, dataAddress);
            Assert.Equal(6L * 64 * 64, (long)recoveryCounter.GetValue(null)! - recoveriesBefore);
            return;
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
        if (callbackInstructions is not null)
        {
            var recoveriesAfter = (long)recoveryCounter.GetValue(null)!;
            var supportsSse4a = (X86Base.CpuId(unchecked((int)0x80000001), 0).Ecx & (1 << 6)) != 0;
            Assert.Equal(supportsSse4a ? 0L : expectedExtracts, recoveriesAfter - recoveriesBefore);
        }
        Assert.True(callbackReturn == CallbackReturnValue,
            $"Guest return value changed: expected 0x{CallbackReturnValue:X16}, actual 0x{callbackReturn:X16}.");
        if (checkRedZone)
        {
            var snapshot = new byte[128];
            Assert.True(memory.TryRead(dataAddress, snapshot));
            var changes = new List<string>();
            for (var index = 0; index < 16; index++)
            {
                var actual = BinaryPrimitives.ReadUInt64LittleEndian(snapshot.AsSpan(index * 8));
                if (actual != CallbackReturnValue)
                    changes.Add($"RSP-{(index + 1) * 8:X}: 0x{actual:X16}");
            }
            Assert.True(changes.Count == 0, $"Red-zone sentinel changed: {string.Join(", ", changes)}");
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
