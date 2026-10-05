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
}
