// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSyncOnAddressCompatExportsTests
{
    private const ulong BaseAddress = 0x1_2000_0000;
    private const ulong ValueAddress = BaseAddress + 0x100;
    private const ulong TimeoutAddress = BaseAddress + 0x200;

    [Fact]
    public void Wait32ReturnsImmediatelyWhenValueDoesNotMatch()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 7);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 9;
        ctx[CpuRegister.Rdx] = 0;

        RunAsGuest(0x801, () => Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSyncOnAddressCompatExports.SyncOnAddressWait32(ctx)));

        Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
    }

    [Fact]
    public void Wait32ReturnsTimedOutForZeroTimeout()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 7);
        WriteUInt32(memory, TimeoutAddress, 0);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 7;
        ctx[CpuRegister.Rdx] = TimeoutAddress;

        RunAsGuest(0x802, () => Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            KernelSyncOnAddressCompatExports.SyncOnAddressWait32(ctx)));

        Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
    }

    [Fact]
    public void Wait64UsesExpectedValueAndEightByteAlignment()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 0x1122_3344_5566_7788);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 0x8877_6655_4433_2211;
        ctx[CpuRegister.Rdx] = 0;

        RunAsGuest(0x803, () => Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSyncOnAddressCompatExports.SyncOnAddressWait64(ctx)));

        ctx[CpuRegister.Rdi] = ValueAddress + 4;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSyncOnAddressCompatExports.SyncOnAddressWait64(ctx));
    }

    [Fact]
    public void WakeOneSelectsOnlyOneWaiter()
    {
        var (memory, firstContext) = CreateContext();
        var secondContext = new CpuContext(memory, Generation.Gen5);
        WriteUInt32(memory, ValueAddress, 5);
        var first = StageWait32(firstContext, threadHandle: 0x804, expected: 5);
        var second = StageWait32(secondContext, threadHandle: 0x805, expected: 5);

        firstContext[CpuRegister.Rdi] = ValueAddress;
        firstContext[CpuRegister.Rsi] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(firstContext));

        Assert.True(first.TryWake());
        Assert.False(second.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, first.Resume());

        firstContext[CpuRegister.Rsi] = int.MaxValue;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(firstContext));
        Assert.True(second.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, second.Resume());
    }

    [Fact]
    public void WakeZeroDoesNotSelectAWaiterAndNegativeCountIsInvalid()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 3);
        var waiter = StageWait32(ctx, threadHandle: 0x806, expected: 3);

        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(ctx));
        Assert.False(waiter.TryWake());

        ctx[CpuRegister.Rsi] = unchecked((ulong)-1L);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(ctx));
        Assert.False(waiter.TryWake());

        ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSyncOnAddressCompatExports.SyncOnAddressWake(ctx));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Resume());
    }

    [Fact]
    public void ExpiredWaitReturnsOkIfTheValueChanged()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 11);
        WriteUInt32(memory, TimeoutAddress, 1000);
        var waiter = StageWait32(ctx, threadHandle: 0x807, expected: 11, TimeoutAddress);

        WriteUInt32(memory, ValueAddress, 12);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waiter.Resume());
    }

    [Fact]
    public void ExpiredWaitReturnsTimedOutIfTheValueDidNotChange()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt32(memory, ValueAddress, 13);
        WriteUInt32(memory, TimeoutAddress, 1000);
        var waiter = StageWait32(ctx, threadHandle: 0x808, expected: 13, TimeoutAddress);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            waiter.Resume());
    }

    [Fact]
    public void UmtxWaitReturnsImmediatelyWhenValueDoesNotMatch()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 7);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 2; // UMTX_OP_WAIT
        ctx[CpuRegister.Rdx] = 9;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;

        RunAsGuest(0x809, () => Assert.Equal(
            0,
            KernelSyncOnAddressCompatExports.UmtxOp(ctx)));

        Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void UmtxWaitAndWakeShareTheAddressWaitRegistry()
    {
        var (memory, waitContext) = CreateContext();
        var wakeContext = new CpuContext(memory, Generation.Gen5);
        WriteUInt64(memory, ValueAddress, 0);

        IGuestThreadBlockWaiter? stagedWaiter = null;
        waitContext[CpuRegister.Rdi] = ValueAddress;
        waitContext[CpuRegister.Rsi] = 2; // UMTX_OP_WAIT
        waitContext[CpuRegister.Rdx] = 0;
        waitContext[CpuRegister.Rcx] = 0;
        waitContext[CpuRegister.R8] = 0;
        RunAsGuest(0x80A, () =>
        {
            Assert.Equal(0, KernelSyncOnAddressCompatExports.UmtxOp(waitContext));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out _,
                out var waiter,
                out var deadline));
            Assert.Equal("_umtx_op.wait", reason);
            Assert.True(hasContinuation);
            Assert.Equal(0, deadline);
            stagedWaiter = Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
        });

        wakeContext[CpuRegister.Rdi] = ValueAddress;
        wakeContext[CpuRegister.Rsi] = 3; // UMTX_OP_WAKE
        wakeContext[CpuRegister.Rdx] = 1;
        wakeContext[CpuRegister.Rcx] = 0;
        wakeContext[CpuRegister.R8] = 0;
        Assert.Equal(0, KernelSyncOnAddressCompatExports.UmtxOp(wakeContext));

        var resumed = Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(stagedWaiter);
        Assert.True(resumed.TryWake());
        Assert.Equal(0, resumed.Resume());
    }

    [Fact]
    public void UmtxZeroTimespecReturnsMinusOneAndTimedOutErrno()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 12);
        WriteUInt64(memory, TimeoutAddress, 0);
        WriteUInt64(memory, TimeoutAddress + sizeof(long), 0);
        SetErrnoStorage(ctx);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 2; // UMTX_OP_WAIT
        ctx[CpuRegister.Rdx] = 12;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = TimeoutAddress;

        Assert.Equal(-1, KernelSyncOnAddressCompatExports.UmtxOp(ctx));
        Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
        Assert.Equal(60, ReadInt32(memory, ctx.FsBase + 0x40)); // ETIMEDOUT
        Assert.False(GuestThreadExecution.TryConsumeCurrentThreadBlock(out _));
    }

    [Fact]
    public void UmtxRejectsInvalidTimespecWithEinval()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 12);
        WriteUInt64(memory, TimeoutAddress, 0);
        WriteUInt64(memory, TimeoutAddress + sizeof(long), 1_000_000_000);
        SetErrnoStorage(ctx);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 2; // UMTX_OP_WAIT
        ctx[CpuRegister.Rdx] = 12;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = TimeoutAddress;

        Assert.Equal(-1, KernelSyncOnAddressCompatExports.UmtxOp(ctx));
        Assert.Equal(22, ReadInt32(memory, ctx.FsBase + 0x40)); // EINVAL
    }

    [Fact]
    public void UmtxLargeTimespecSaturatesWithoutOverflow()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 1);
        WriteUInt64(memory, TimeoutAddress, unchecked((ulong)long.MaxValue));
        WriteUInt64(memory, TimeoutAddress + sizeof(long), 999_999_999);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 2; // UMTX_OP_WAIT
        ctx[CpuRegister.Rdx] = 2; // Already changed, so no host wait.
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = TimeoutAddress;

        Assert.Equal(0, KernelSyncOnAddressCompatExports.UmtxOp(ctx));
    }

    [Fact]
    public void UmtxTimedContinuationReturnsPosixTimeout()
    {
        var (memory, ctx) = CreateContext();
        WriteUInt64(memory, ValueAddress, 21);
        WriteUInt64(memory, TimeoutAddress, 0);
        WriteUInt64(memory, TimeoutAddress + sizeof(long), 1_000_000); // 1 ms
        SetErrnoStorage(ctx);
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = 2; // UMTX_OP_WAIT
        ctx[CpuRegister.Rdx] = 21;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = TimeoutAddress;

        IGuestThreadBlockWaiter? stagedWaiter = null;
        RunAsGuest(0x80B, () =>
        {
            Assert.Equal(0, KernelSyncOnAddressCompatExports.UmtxOp(ctx));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out var waiter,
                out var deadline));
            Assert.True(deadline > 0);
            stagedWaiter = Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
        });

        Thread.Sleep(10);
        var resumed = Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(stagedWaiter);
        Assert.Equal(-1, resumed.Resume());
        Assert.Equal(60, ReadInt32(memory, ctx.FsBase + 0x40)); // ETIMEDOUT
    }

    [Fact]
    public void UmtxExportIsRegisteredForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("04AjkP0jO9U", out var export));
        Assert.Equal("_umtx_op", export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    private static (FakeCpuMemory Memory, CpuContext Context) CreateContext()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        return (memory, new CpuContext(memory, Generation.Gen5));
    }

    private static IGuestThreadBlockWaiter StageWait32(
        CpuContext ctx,
        ulong threadHandle,
        uint expected,
        ulong timeoutAddress = 0)
    {
        IGuestThreadBlockWaiter? stagedWaiter = null;
        ctx[CpuRegister.Rdi] = ValueAddress;
        ctx[CpuRegister.Rsi] = expected;
        ctx[CpuRegister.Rdx] = timeoutAddress;
        RunAsGuest(threadHandle, () =>
        {
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelSyncOnAddressCompatExports.SyncOnAddressWait32(ctx));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out _,
                out var waiter,
                out var deadline));
            Assert.Equal("sceKernelSyncOnAddressWait32", reason);
            Assert.True(hasContinuation);
            Assert.Equal(timeoutAddress == 0, deadline == 0);
            stagedWaiter = Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
        });

        return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(stagedWaiter);
    }

    private static void RunAsGuest(ulong threadHandle, Action action)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000 + threadHandle,
            resumeRsp: 0x2_0000 + threadHandle,
            returnSlotAddress: 0x3_0000 + threadHandle);
        try
        {
            action();
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void SetErrnoStorage(CpuContext ctx)
    {
        ctx.FsBase = BaseAddress + 0x700;
    }

    private static int ReadInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
}
