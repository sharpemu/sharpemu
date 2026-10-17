// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// The constant-result thunks replace a managed export with three bytes of
/// machine code, so the guest never crosses into managed code for them at all.
/// That is only correct while the export really is constant, which is what
/// these tests check - by running the managed export and by running the thunk,
/// not by trusting either.
/// </summary>
public sealed unsafe class NativeImportThunkTests
{
    // Every NID the backend thunks. Kept here deliberately: the list has to be
    // restated by a test that proves each entry, or an export gaining a real
    // implementation would silently keep answering 0 forever.
    public static TheoryData<string> ConstantZeroNids() =>
    [
        "3pcAvmwKCvM", // sceSystemGestureInitializePrimitiveTouchRecognizer
        "FWF8zkhr854", // sceSystemGestureCreateTouchRecognizer
        "qpo-mEOwje0", // sceSystemGestureOpen
        "GgFMb22sbbI", // sceSystemGestureUpdatePrimitiveTouchRecognizer
        "j4h82CQWENo", // sceSystemGestureUpdateTouchRecognizer
        "h8uongcBNVs", // sceSystemGestureGetTouchEventsCount
        "1MMK0W-kMgA", // sceSystemGestureAppendTouchRecognizer
        "wPJGwI2RM2I", // sceSystemGestureUpdateAllTouchRecognizer
        "-4GCfYdNF1s", // sceImeUpdate
        "x8qnXqh-tiM", // sceMouseRead
    ];

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen4 | Generation.Gen5));
        return manager;
    }

    // The equivalence proof. If the managed export ever answers with anything
    // but RAX = 0 - for any argument, in any order - the thunk standing in for
    // it is wrong, and this is where that shows up.
    [Theory]
    [MemberData(nameof(ConstantZeroNids))]
    public void ThunkedExportAnswersZeroForEveryArgument(string nid)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export), nid);

        ulong[] probes =
        [
            0,
            1,
            ulong.MaxValue,
            0x0000_7FFF_FFFF_F000UL,
            0x8000_0000_0000_0000UL,
            0x0000_0000_DEAD_BEEFUL,
        ];

        foreach (var probe in probes)
        {
            var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = probe;
            context[CpuRegister.Rsi] = ~probe;
            context[CpuRegister.Rdx] = probe ^ 0x5555_5555_5555_5555UL;
            context[CpuRegister.Rcx] = probe;
            context[CpuRegister.R8] = ~probe;
            context[CpuRegister.R9] = probe;

            var result = export.Function(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(0UL, context[CpuRegister.Rax]);
        }
    }

    // An export that writes through a guest pointer is not constant, however
    // constant its return value is. None of the thunked ones may touch memory.
    [Theory]
    [MemberData(nameof(ConstantZeroNids))]
    public void ThunkedExportWritesNothingToGuestMemory(string nid)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export));

        const ulong memoryBase = 0x1_0000_0000;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        Span<byte> pattern = stackalloc byte[256];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = unchecked((byte)(i * 7 + 1));
        }

        Assert.True(memory.TryWrite(memoryBase, pattern));

        // Point every argument register at the pattern: an export that writes
        // its answer through one of them would disturb it.
        context[CpuRegister.Rdi] = memoryBase;
        context[CpuRegister.Rsi] = memoryBase + 8;
        context[CpuRegister.Rdx] = memoryBase + 16;
        context[CpuRegister.Rcx] = memoryBase + 24;
        context[CpuRegister.R8] = memoryBase + 32;
        context[CpuRegister.R9] = memoryBase + 40;

        _ = export.Function(context);

        Span<byte> after = stackalloc byte[256];
        Assert.True(memory.TryRead(memoryBase, after));
        Assert.True(after.SequenceEqual(pattern), $"{nid} wrote to guest memory");
    }

    [Theory]
    [MemberData(nameof(ConstantZeroNids))]
    public void BackendClassifiesTheNidAsAConstantThunk(string nid)
    {
        Assert.Equal(
            DirectExecutionBackend.GuestFastPathStub.ConstantZero,
            DirectExecutionBackend.ClassifyGuestFastPathStub(nid));
    }

    // Nothing with real behaviour may drift into the constant list.
    [Theory]
    [InlineData("9UK1vLZQft4")] // scePthreadMutexLock
    [InlineData("tn3VlD0hG60")] // scePthreadMutexUnlock
    [InlineData("Zxa0VhQVTsk")] // sceKernelWaitSema
    [InlineData("n88vx3C5nW8")] // gettimeofday
    [InlineData("YndgXqQVV7c")] // scePadReadState
    [InlineData("UbQoYawOsfY")] // sceAvPlayerIsActive
    [InlineData("Q3VBxCXhUHs")] // memcpy
    public void ImportsWithRealBehaviourAreNotConstantThunks(string nid)
    {
        Assert.NotEqual(
            DirectExecutionBackend.GuestFastPathStub.ConstantZero,
            DirectExecutionBackend.ClassifyGuestFastPathStub(nid));
    }

    [Fact]
    public void ConstantZeroThunkIsXorEaxEaxRet()
    {
        var code = DirectExecutionBackend.EmitGuestFastPathStub(
            DirectExecutionBackend.GuestFastPathStub.ConstantZero,
            blockTlsIndex: 0,
            fallbackTrampoline: 0x1234);

        // No TLS read and no fallback: the answer cannot depend on either.
        Assert.Equal(new byte[] { 0x31, 0xC0, 0xC3 }, code);
    }

    // Executes the emitted machine code. A thunk that returned the incoming RAX
    // (the SysV variadic vector count the guest passes in) instead of zeroing it
    // would pass a byte comparison and fail here.
    [Fact]
    public void ConstantZeroThunkReturnsZeroWhenExecuted()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        const ulong raxSentinel = 0x1122334455667788UL;
        var code = DirectExecutionBackend.EmitGuestFastPathStub(
            DirectExecutionBackend.GuestFastPathStub.ConstantZero,
            blockTlsIndex: 0,
            fallbackTrampoline: 0);

        var page = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            var offset = 0;
            // mov rax, sentinel
            page[offset++] = 0x48;
            page[offset++] = 0xB8;
            for (var i = 0; i < 8; i++)
            {
                page[offset++] = (byte)(raxSentinel >> (i * 8));
            }

            // The thunk is inlined straight after, so its `ret` returns to the
            // managed caller with whatever RAX it left.
            for (var i = 0; i < code.Count; i++)
            {
                page[offset++] = code[i];
            }

            Assert.True(offset < 4096);
            Assert.Equal(0, MProtect((nint)page, 4096, ProtReadExec));
            var entry = (delegate* unmanaged<ulong>)page;

            Assert.Equal(0UL, entry());
        }
        finally
        {
            _ = MProtect((nint)page, 4096, ProtReadWrite);
            NativeMemory.AlignedFree(page);
        }
    }

    private const int ProtReadWrite = 0x1 | 0x2;
    private const int ProtReadExec = 0x1 | 0x4;

    [DllImport("libc", EntryPoint = "mprotect", SetLastError = true)]
    private static extern int MProtect(nint address, nuint length, int protection);
}
