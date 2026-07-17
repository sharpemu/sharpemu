// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// IT_EVENT_WRITE carries a 6-bit hardware EVENT_TYPE, but sceAgcDriverAddEqEvent registers the
// listener with a guest-defined eventId. Those two values are not the same numbering scheme, so
// exact ident matching never wakes anything (issue #173). TriggerRegisteredEventsByFilter wakes
// every graphics registration instead.
public sealed class AgcEventQueueTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x2000;

    [Fact]
    public void TriggerRegisteredEventsByFilter_DifferentIdentThanEventType_WakesGraphicsWaiter()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong handleOutAddress = BaseAddress + 0x100;
        const ulong eventsAddress = BaseAddress + 0x200;
        const ulong outCountAddress = BaseAddress + 0x300;
        const ulong timeoutAddress = BaseAddress + 0x400;

        // Create an event queue.
        ctx[CpuRegister.Rdi] = handleOutAddress;
        var createResult = KernelEventQueueCompatExports.KernelCreateEqueue(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, createResult);

        var handle = ReadUInt64(memory, handleOutAddress);

        // Register a graphics event with eventId 0x20 (as Poppy Playtime does).
        const ulong registeredEventId = 0x20;
        const ulong userData = 0xDEAD_BEEF;
        var registered = KernelEventQueueCompatExports.RegisterEvent(
            handle,
            registeredEventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData);
        Assert.True(registered);

        // The command buffer fires EVENT_WRITE with eventType 0x07. This does not match 0x20.
        const ulong eventType = 0x07;
        var triggered = KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            eventType);
        Assert.Equal(1, triggered);

        // Wait with timeout=0. The event is already pending, so this returns immediately.
        WriteUInt64(memory, timeoutAddress, 0);
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = eventsAddress;
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = outCountAddress;
        ctx[CpuRegister.R8] = timeoutAddress;
        var waitResult = KernelEventQueueCompatExports.KernelWaitEqueue(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, waitResult);
        Assert.Equal(1u, ReadUInt32(memory, outCountAddress));

        // Verify the queued event carries the registered ident and the event type as data.
        Assert.Equal(registeredEventId, ReadUInt64(memory, eventsAddress + 0x00));
        Assert.Equal(KernelEventQueueCompatExports.KernelEventFilterGraphics, ReadInt16(memory, eventsAddress + 0x08));
        Assert.Equal(0u, ReadUInt16(memory, eventsAddress + 0x0A));
        Assert.Equal(1u, ReadUInt32(memory, eventsAddress + 0x0C));
        Assert.Equal(eventType, ReadUInt64(memory, eventsAddress + 0x10));
        Assert.Equal(userData, ReadUInt64(memory, eventsAddress + 0x18));
    }

    [Fact]
    public void TriggerRegisteredEventsByFilter_NoGraphicsRegistrations_ReturnsZero()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong handleOutAddress = BaseAddress + 0x100;
        ctx[CpuRegister.Rdi] = handleOutAddress;
        var createResult = KernelEventQueueCompatExports.KernelCreateEqueue(ctx);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, createResult);

        var handle = ReadUInt64(memory, handleOutAddress);

        // Register a user event, not a graphics event.
        KernelEventQueueCompatExports.RegisterEvent(
            handle,
            0x1,
            KernelEventQueueCompatExports.KernelEventFilterUser,
            0);

        var triggered = KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            0x07);

        Assert.Equal(0, triggered);
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

    private static ushort ReadUInt16(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[2];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
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
}
