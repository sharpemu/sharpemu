// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelEventQueueWaiterLifetimeTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong HandleAddress = BaseAddress + 0x100;
    private const ulong EventsAddress = BaseAddress + 0x200;
    private const ulong OutCountAddress = BaseAddress + 0x300;

    [Fact]
    public void DeleteEqueue_CompletesStagedWaiterAsDeleted()
    {
        var (memory, ctx, handle) = CreateEqueue();
        var waiter = StageGuestWait(ctx, handle, threadHandle: 0x701);

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));

        Assert.True(waiter.TryWake());
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
            waiter.Resume());
        Assert.Equal(0u, ReadUInt32(memory, OutCountAddress));
        Assert.False(KernelEventQueueCompatExports.IsValidEqueue(handle));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
    }

    [Fact]
    public void DeletedGenerationWaiter_CannotConsumeNewQueueEvent()
    {
        var (memory, ctx, oldHandle) = CreateEqueue();
        var oldWaiter = StageGuestWait(ctx, oldHandle, threadHandle: 0x702);

        ctx[CpuRegister.Rdi] = oldHandle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));

        var newHandle = CreateEqueue(ctx, memory);
        Assert.NotEqual(oldHandle, newHandle);
        var expected = new KernelEventQueueCompatExports.KernelQueuedEvent(
            Ident: 0x77,
            Filter: KernelEventQueueCompatExports.KernelEventFilterUser,
            Flags: KernelEventQueueCompatExports.KernelEventFlagClear,
            Fflags: 1,
            Data: 0x1234,
            UserData: 0x5678);
        Assert.True(KernelEventQueueCompatExports.EnqueueEvent(
            newHandle,
            expected));

        Assert.True(oldWaiter.TryWake());
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
            oldWaiter.Resume());
        Assert.True(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                newHandle,
                out var delivered));
        Assert.Equal(expected, delivered);

        ctx[CpuRegister.Rdi] = newHandle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
    }

    private static (FakeCpuMemory Memory, CpuContext Context, ulong Handle)
        CreateEqueue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        return (memory, ctx, CreateEqueue(ctx, memory));
    }

    private static ulong CreateEqueue(CpuContext ctx, FakeCpuMemory memory)
    {
        ctx[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        return ReadUInt64(memory, HandleAddress);
    }

    private static IGuestThreadBlockWaiter StageGuestWait(
        CpuContext ctx,
        ulong handle,
        ulong threadHandle)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000 + threadHandle,
            resumeRsp: 0x2_0000 + threadHandle,
            returnSlotAddress: 0x3_0000 + threadHandle);
        try
        {
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = EventsAddress;
            ctx[CpuRegister.Rdx] = 1;
            ctx[CpuRegister.Rcx] = OutCountAddress;
            ctx[CpuRegister.R8] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelEventQueueCompatExports.KernelWaitEqueue(ctx));

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out var hasContinuation,
                out _,
                out var waiter,
                out _));
            Assert.Equal("sceKernelWaitEqueue", reason);
            Assert.True(hasContinuation);
            return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
