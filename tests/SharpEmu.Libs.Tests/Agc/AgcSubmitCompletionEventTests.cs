// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// sceAgcDriverSubmitAcb takes the compute queue's owner handle in rdi. That handle is the
// eventId the guest registers with sceAgcDriverAddEqEvent, so an ACB reaching its queue fence
// must deliver a graphics-filter completion event under that exact ident. UE 4.27's dynamic
// resolution heuristic parks the game thread on that interrupt; without it the render side
// never publishes GPU timings and the title deadlocks.
[Collection(GraphicsEventQueueStateCollection.Name)]
public sealed class AgcSubmitCompletionEventTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x2000;

    private const ulong HandleOutAddress = BaseAddress + 0x100;
    private const ulong EventsAddress = BaseAddress + 0x200;
    private const ulong OutCountAddress = BaseAddress + 0x300;
    private const ulong TimeoutAddress = BaseAddress + 0x400;
    private const ulong PacketAddress = BaseAddress + 0x500;
    private const ulong CommandAddress = BaseAddress + 0x800;

    [Fact]
    public void DriverSubmitAcb_DeliversCompletionEventUnderOwnerHandleIdent()
    {
        // Deliberately unusual so a graphics registration from a sibling test cannot alias it:
        // the kernel event registry is process-wide static state.
        const ulong ownerHandle = 0x5EA1;
        const ulong userData = 0xC0FF_EE00_1234_5678;

        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            Assert.True(KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                ownerHandle,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData));

            SubmitEmptyAcb(ctx, memory, ownerHandle);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
            Assert.Equal(1u, ReadUInt32(memory, OutCountAddress));
            Assert.Equal(ownerHandle, ReadUInt64(memory, EventsAddress + 0x00));
            Assert.Equal(
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                ReadInt16(memory, EventsAddress + 0x08));
            Assert.Equal(ownerHandle, ReadUInt64(memory, EventsAddress + 0x10));
            Assert.Equal(userData, ReadUInt64(memory, EventsAddress + 0x18));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    // Delivery is registration-gated: a title that never registered the ACB owner handle as a
    // graphics eventId must not observe a spurious completion interrupt.
    [Fact]
    public void DriverSubmitAcb_WithoutMatchingRegistration_DeliversNothing()
    {
        const ulong ownerHandle = 0x5EA2;
        const ulong unrelatedEventId = 0x5EA3;

        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            Assert.True(KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                unrelatedEventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData: 0));

            SubmitEmptyAcb(ctx, memory, ownerHandle);

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                WaitEqueue(ctx, memory, equeue));
            Assert.Equal(0u, ReadUInt32(memory, OutCountAddress));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    // The graphics queue keeps its own completion ident (0); an ACB owner handle must not be
    // able to wake a graphics-queue completion registration or vice versa.
    [Fact]
    public void DriverSubmitDcb_DeliversCompletionEventUnderGraphicsIdent()
    {
        const ulong graphicsCompletionIdent = 0;
        const ulong userData = 0xABCD_0000_0000_1111;

        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            Assert.True(KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                graphicsCompletionIdent,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData));

            WriteEmptySubmitPacket(memory);
            ctx[CpuRegister.Rdi] = PacketAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                AgcExports.DriverSubmitDcb(ctx));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
            Assert.Equal(1u, ReadUInt32(memory, OutCountAddress));
            Assert.Equal(graphicsCompletionIdent, ReadUInt64(memory, EventsAddress + 0x00));
            Assert.Equal(userData, ReadUInt64(memory, EventsAddress + 0x18));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    [Fact]
    public void DriverSubmitDcb_QueuedInterruptDoesNotAddSyntheticCompletionEvent()
    {
        const ulong graphicsCompletionIdent = 0;
        const uint interruptContextId = 7;

        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        try
        {
            Assert.True(KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                graphicsCompletionIdent,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData: 0));

            WriteUInt32(memory, CommandAddress + 0x00, 0xC006_1060);
            WriteUInt32(memory, CommandAddress + 0x04, 0);
            WriteUInt32(memory, CommandAddress + 0x08, 2u << 24);
            WriteUInt32(memory, CommandAddress + 0x0C, 0);
            WriteUInt32(memory, CommandAddress + 0x10, 0);
            WriteUInt32(memory, CommandAddress + 0x14, 0);
            WriteUInt32(memory, CommandAddress + 0x18, 0);
            WriteUInt32(memory, CommandAddress + 0x1C, interruptContextId);
            WriteUInt64(memory, PacketAddress, CommandAddress);
            WriteUInt32(memory, PacketAddress + 8, 8);

            ctx[CpuRegister.Rdi] = PacketAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                AgcExports.DriverSubmitDcb(ctx));

            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                WaitEqueue(ctx, memory, equeue));
            Assert.Equal(1u, ReadUInt32(memory, OutCountAddress));
            Assert.Equal(interruptContextId, ReadUInt64(memory, EventsAddress + 0x10));
        }
        finally
        {
            DeleteEqueue(ctx, equeue);
        }
    }

    private static void SubmitEmptyAcb(CpuContext ctx, FakeCpuMemory memory, ulong ownerHandle)
    {
        WriteEmptySubmitPacket(memory);
        ctx[CpuRegister.Rdi] = ownerHandle;
        ctx[CpuRegister.Rsi] = PacketAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DriverSubmitAcb(ctx));
    }

    // A zero-dword submission parses as immediately complete, so the queue reaches its fence
    // without needing a live GPU backend.
    private static void WriteEmptySubmitPacket(FakeCpuMemory memory)
    {
        WriteUInt64(memory, PacketAddress, 0);
        WriteUInt32(memory, PacketAddress + 8, 0);
    }

    private static ulong CreateEqueue(CpuContext ctx, FakeCpuMemory memory)
    {
        ctx[CpuRegister.Rdi] = HandleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        return ReadUInt64(memory, HandleOutAddress);
    }

    // The kernel event registry is process-wide static state and deleting the queue drops its
    // registrations, so sibling suites that assert over every graphics registration stay clean.
    private static void DeleteEqueue(CpuContext ctx, ulong equeue)
    {
        ctx[CpuRegister.Rdi] = equeue;
        _ = KernelEventQueueCompatExports.KernelDeleteEqueue(ctx);
    }

    private static int WaitEqueue(CpuContext ctx, FakeCpuMemory memory, ulong equeue)
    {
        WriteUInt64(memory, TimeoutAddress, 0);
        ctx[CpuRegister.Rdi] = equeue;
        ctx[CpuRegister.Rsi] = EventsAddress;
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = OutCountAddress;
        ctx[CpuRegister.R8] = TimeoutAddress;
        return KernelEventQueueCompatExports.KernelWaitEqueue(ctx);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static short ReadInt16(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[2];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadInt16LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
