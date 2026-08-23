// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// The kernel event-queue registry is process-wide static state, and these tests assert over
// every graphics registration in it, so they cannot run beside another suite that registers
// graphics events.
[CollectionDefinition(GraphicsEventQueueStateCollection.Name, DisableParallelization = true)]
public sealed class GraphicsEventQueueStateCollection
{
    public const string Name = "GraphicsEventQueueState";
}

// IT_EVENT_WRITE carries a 6-bit hardware EVENT_TYPE, but sceAgcDriverAddEqEvent registers the
// listener with a guest-defined eventId. Those two values are not the same numbering scheme, so
// exact ident matching never wakes anything (issue #173). TriggerRegisteredEventsByFilter wakes
// every graphics registration instead.
[Collection(GraphicsEventQueueStateCollection.Name)]
public sealed class AgcEventQueueTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x2000;

    [Fact]
    public void DriverGetEqEventType_ReturnsPackedRegisteredEventType()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong eventAddress = BaseAddress + 0x100;
        const uint eventType = 0x0706_0001;
        WriteUInt32(memory, eventAddress, eventType);
        ctx[CpuRegister.Rdi] = eventAddress;

        var result = AgcExports.DriverGetEqEventType(ctx);

        Assert.Equal(unchecked((int)eventType), result);
        Assert.Equal(eventType, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void DriverGetEqContextId_ReturnsLowTwentySevenEventDataBits()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong eventAddress = BaseAddress + 0x100;
        const ulong eventData = 0xFFFF_FFFF_FEDC_BA98;
        WriteUInt64(memory, eventAddress + 0x10, eventData);
        ctx[CpuRegister.Rdi] = eventAddress;

        var result = AgcExports.DriverGetEqContextId(ctx);

        Assert.Equal(0x06DC_BA98, result);
        Assert.Equal(0x06DC_BA98UL, ctx[CpuRegister.Rax]);
    }

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
        Assert.Equal(
            KernelEventQueueCompatExports.KernelEventFlagClear,
            ReadUInt16(memory, eventsAddress + 0x0A));
        Assert.Equal(1u, ReadUInt32(memory, eventsAddress + 0x0C));
        Assert.Equal(eventType, ReadUInt64(memory, eventsAddress + 0x10));
        Assert.Equal(userData, ReadUInt64(memory, eventsAddress + 0x18));

        // Registrations live in process-wide static state, and a sibling test asserts that
        // no graphics registration exists at all. Drop this one instead of relying on
        // execution order.
        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
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

    [Fact]
    public void CapturedCompletion_DoesNotWakeDeleteAndReAddGeneration()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var handle = CreateEqueue(ctx, memory, BaseAddress + 0x100);
        const ulong eventId = 0x20;

        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            handle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0x1111));
        var staleSnapshot =
            KernelEventQueueCompatExports.CaptureRegisteredEvents(
                memory,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics);
        Assert.Single(staleSnapshot.Targets);

        Assert.True(KernelEventQueueCompatExports.DeleteRegisteredEvent(
            handle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics));
        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            handle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0x2222));

        var staleDelivery =
            KernelEventQueueCompatExports.TriggerCapturedEvents(
                staleSnapshot,
                eventId);
        Assert.Equal(0, staleDelivery.TriggeredCount);
        Assert.Equal(1, staleDelivery.StaleCount);
        Assert.False(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                handle,
                out _));

        var liveSnapshot =
            KernelEventQueueCompatExports.CaptureRegisteredEvents(
                memory,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics);
        var liveDelivery =
            KernelEventQueueCompatExports.TriggerCapturedEvents(
                liveSnapshot,
                eventId);
        Assert.Equal(1, liveDelivery.TriggeredCount);
        Assert.Equal(0, liveDelivery.StaleCount);
        Assert.True(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                handle,
                out var delivered));
        Assert.Equal(0x2222UL, delivered.UserData);

        DeleteEqueue(ctx, handle);
    }

    [Fact]
    public void CapturedCompletion_PreservesEachQueuesRegistrationGeneration()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var firstHandle = CreateEqueue(ctx, memory, BaseAddress + 0x100);
        var secondHandle = CreateEqueue(ctx, memory, BaseAddress + 0x108);
        const ulong eventId = 0x20;

        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            firstHandle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0xAAAA));
        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            secondHandle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0xBBBB));
        var snapshot = KernelEventQueueCompatExports.CaptureRegisteredEvents(
            memory,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics);
        Assert.Equal(2, snapshot.Targets.Length);

        Assert.True(KernelEventQueueCompatExports.DeleteRegisteredEvent(
            secondHandle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics));
        var delivery = KernelEventQueueCompatExports.TriggerCapturedEvents(
            snapshot,
            eventId);

        Assert.Equal(1, delivery.TriggeredCount);
        Assert.Equal(1, delivery.StaleCount);
        Assert.True(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                firstHandle,
                out var delivered));
        Assert.Equal(0xAAAAUL, delivered.UserData);
        Assert.False(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                secondHandle,
                out _));

        DeleteEqueue(ctx, firstHandle);
        DeleteEqueue(ctx, secondHandle);
    }

    [Fact]
    public void CapturedCompletion_DoesNotWakeDeletedEqueue()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var handle = CreateEqueue(ctx, memory, BaseAddress + 0x100);

        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            handle,
            ident: 0,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0));
        var snapshot = KernelEventQueueCompatExports.CaptureRegisteredEvents(
            memory,
            ident: 0,
            KernelEventQueueCompatExports.KernelEventFilterGraphics);
        DeleteEqueue(ctx, handle);

        var delivery = KernelEventQueueCompatExports.TriggerCapturedEvents(
            snapshot,
            data: 0);

        Assert.Equal(0, delivery.TriggeredCount);
        Assert.Equal(1, delivery.StaleCount);
    }

    [Fact]
    public void CapturedCompletion_IsScopedToCreatingRuntime()
    {
        var firstMemory = new FakeCpuMemory(BaseAddress, MemorySize);
        var secondMemory = new FakeCpuMemory(BaseAddress, MemorySize);
        var firstContext = new CpuContext(firstMemory, Generation.Gen5);
        var secondContext = new CpuContext(secondMemory, Generation.Gen5);
        var firstHandle = CreateEqueue(
            firstContext,
            firstMemory,
            BaseAddress + 0x100);
        var secondHandle = CreateEqueue(
            secondContext,
            secondMemory,
            BaseAddress + 0x100);
        const ulong eventId = 0x20;

        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            firstHandle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0x1111));
        Assert.True(KernelEventQueueCompatExports.RegisterEvent(
            secondHandle,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics,
            userData: 0x2222));

        var snapshot = KernelEventQueueCompatExports.CaptureRegisteredEvents(
            firstMemory,
            eventId,
            KernelEventQueueCompatExports.KernelEventFilterGraphics);
        Assert.Single(snapshot.Targets);
        var delivery = KernelEventQueueCompatExports.TriggerCapturedEvents(
            snapshot,
            eventId);

        Assert.Equal(1, delivery.TriggeredCount);
        Assert.Equal(0, delivery.StaleCount);
        Assert.True(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                firstHandle,
                out var delivered));
        Assert.Equal(0x1111UL, delivered.UserData);
        Assert.False(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                secondHandle,
                out _));

        DeleteEqueue(firstContext, firstHandle);
        DeleteEqueue(secondContext, secondHandle);
    }

    [Fact]
    public void OnePendingEventCanBeReservedByOnlyOneWaiter()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong handleOutAddress = BaseAddress + 0x100;
        ctx[CpuRegister.Rdi] = handleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        var handle = ReadUInt64(memory, handleOutAddress);

        Assert.True(KernelEventQueueCompatExports.EnqueueEvent(
            handle,
            new KernelEventQueueCompatExports.KernelQueuedEvent(
                7,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                KernelEventQueueCompatExports.KernelEventFlagClear,
                1,
                0,
                0)));

        Assert.Equal(
            1,
            KernelEventQueueCompatExports.ReservePendingEventCountForTest(
                handle,
                eventCapacity: 1));
        Assert.Equal(
            0,
            KernelEventQueueCompatExports.ReservePendingEventCountForTest(
                handle,
                eventCapacity: 1));
    }

    [Fact]
    public void ZeroTimeoutWithNoEventReturnsWithoutHostWait()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const ulong handleOutAddress = BaseAddress + 0x100;
        const ulong eventsAddress = BaseAddress + 0x200;
        const ulong outCountAddress = BaseAddress + 0x300;
        const ulong timeoutAddress = BaseAddress + 0x400;
        ctx[CpuRegister.Rdi] = handleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        var handle = ReadUInt64(memory, handleOutAddress);
        WriteUInt64(memory, timeoutAddress, 0);

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = eventsAddress;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rcx] = outCountAddress;
        ctx[CpuRegister.R8] = timeoutAddress;

        var result = KernelEventQueueCompatExports.KernelWaitEqueue(ctx);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            result);
        Assert.Equal(0u, ReadUInt32(memory, outCountAddress));
        Assert.True(
            KernelEventQueueCompatExports.IsSynchronousPoll(
                timeoutAddress,
                timeoutUsec: 0));
    }

    [Fact]
    public void LevelUserEventPersistsButEdgeEventClears()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong handleOutAddress = BaseAddress + 0x100;
        ctx[CpuRegister.Rdi] = handleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        var handle = ReadUInt64(memory, handleOutAddress);

        const ulong levelIdent = 0xA1;
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = levelIdent;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelAddUserEvent(ctx));
        ctx[CpuRegister.Rdx] = 0x1111;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelTriggerUserEvent(ctx));

        Assert.True(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                handle,
                out var firstLevel));
        Assert.True(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                handle,
                out var secondLevel));
        Assert.Equal((ushort)0, firstLevel.Flags);
        Assert.Equal(0x1111UL, firstLevel.UserData);
        Assert.Equal(firstLevel, secondLevel);

        ctx[CpuRegister.Rsi] = levelIdent;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteUserEvent(ctx));

        const ulong edgeIdent = 0xA2;
        ctx[CpuRegister.Rsi] = edgeIdent;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelAddUserEventEdge(ctx));
        ctx[CpuRegister.Rdx] = 0x2222;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelTriggerUserEvent(ctx));

        Assert.True(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                handle,
                out var edge));
        Assert.Equal(
            KernelEventQueueCompatExports.KernelEventFlagClear,
            edge.Flags);
        Assert.Equal(0x2222UL, edge.UserData);
        Assert.False(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                handle,
                out _));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
    }

    [Fact]
    public void DeletingLevelRegistrationClearsItsReadyState()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        const ulong handleOutAddress = BaseAddress + 0x100;
        ctx[CpuRegister.Rdi] = handleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        var handle = ReadUInt64(memory, handleOutAddress);

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0xB1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelAddUserEvent(ctx));
        ctx[CpuRegister.Rdx] = 0x3333;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelTriggerUserEvent(ctx));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteUserEvent(ctx));

        Assert.False(
            KernelEventQueueCompatExports.TryReservePendingEventForTest(
                handle,
                out _));

        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
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

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static ulong CreateEqueue(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong handleOutAddress)
    {
        ctx[CpuRegister.Rdi] = handleOutAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        return ReadUInt64(memory, handleOutAddress);
    }

    private static void DeleteEqueue(CpuContext ctx, ulong handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
    }
}
