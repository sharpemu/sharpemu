// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelEventQueueCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;

    [Fact]
    public void CreateEqueue_WritesNonZeroHandleAndSucceeds()
    {
        var (context, outAddress) = NewContextWithOutSlot();
        context[CpuRegister.Rdi] = outAddress;

        var result = KernelEventQueueCompatExports.KernelCreateEqueue(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(context.TryReadUInt64(outAddress, out var handle));
        Assert.NotEqual(0UL, handle);
        Assert.True(KernelEventQueueCompatExports.IsValidEqueue(handle));
    }

    [Fact]
    public void CreateEqueue_NullOutAddressReturnsInvalidArgument()
    {
        var (context, _) = NewContextWithOutSlot();
        context[CpuRegister.Rdi] = 0;

        var result = KernelEventQueueCompatExports.KernelCreateEqueue(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void DeleteEqueue_RemovesQueueFromRegistry()
    {
        var (context, outAddress) = NewContextWithOutSlot();
        context[CpuRegister.Rdi] = outAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventQueueCompatExports.KernelCreateEqueue(context));
        Assert.True(context.TryReadUInt64(outAddress, out var handle));

        context[CpuRegister.Rdi] = handle;
        var result = KernelEventQueueCompatExports.KernelDeleteEqueue(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.False(KernelEventQueueCompatExports.IsValidEqueue(handle));
    }

    [Fact]
    public void AddUserEvent_OnUnknownQueueReturnsNotFound()
    {
        var (context, _) = NewContextWithOutSlot();
        const ulong unknownHandle = 0xDEAD_BEEF;
        context[CpuRegister.Rdi] = unknownHandle;
        context[CpuRegister.Rsi] = 42;

        var result = KernelEventQueueCompatExports.KernelAddUserEvent(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void AddUserEvent_OnValidQueueSucceeds()
    {
        var handle = CreateEqueue();
        var (context, _) = NewContextWithOutSlot();
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 0x1234;

        var result = KernelEventQueueCompatExports.KernelAddUserEvent(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
    }

    [Fact]
    public void TriggerUserEvent_OnUnknownQueueReturnsNotFound()
    {
        var (context, _) = NewContextWithOutSlot();
        context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0;

        var result = KernelEventQueueCompatExports.KernelTriggerUserEvent(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void TriggerUserEvent_OnUnregisteredEventReturnsNotFound()
    {
        var handle = CreateEqueue();
        var (context, _) = NewContextWithOutSlot();
        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = 0xABCD; // never registered
        context[CpuRegister.Rdx] = 0;

        var result = KernelEventQueueCompatExports.KernelTriggerUserEvent(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    // Full lifecycle: create -> register user event -> trigger -> wait delivers
    // the queued event with the registered ident/filter and the trigger data.
    // DequeueEvents runs before the blocking path, so a pre-triggered queue
    // returns immediately without touching the guest thread scheduler.
    [Fact]
    public void CreateAddTriggerWait_DeliversTriggeredUserEvent()
    {
        const ulong eventIdent = 0x4242;
        const ulong triggerData = 0x55AA_55AA;
        var handle = CreateEqueue();

        // Register the user event on the queue.
        var (addCtx, _) = NewContextWithOutSlot();
        addCtx[CpuRegister.Rdi] = handle;
        addCtx[CpuRegister.Rsi] = eventIdent;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelAddUserEvent(addCtx));

        // Trigger it with a distinct data payload.
        var (triggerCtx, _) = NewContextWithOutSlot();
        triggerCtx[CpuRegister.Rdi] = handle;
        triggerCtx[CpuRegister.Rsi] = eventIdent;
        triggerCtx[CpuRegister.Rdx] = triggerData;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelTriggerUserEvent(triggerCtx));

        // Wait should deliver the single pending event without blocking.
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var waitContext = new CpuContext(memory, Generation.Gen5);
        const ulong eventsAddress = MemoryBase + 0x100;
        const ulong outCountAddress = MemoryBase + 0x300;
        waitContext[CpuRegister.Rdi] = handle;
        waitContext[CpuRegister.Rsi] = eventsAddress;
        waitContext[CpuRegister.Rdx] = 1; // capacity
        waitContext[CpuRegister.Rcx] = outCountAddress;
        waitContext[CpuRegister.R8] = 0; // no timeout -> would block, but event is pending

        var result = KernelEventQueueCompatExports.KernelWaitEqueue(waitContext);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(waitContext.TryReadUInt32(outCountAddress, out var delivered));
        Assert.Equal(1u, delivered);

        // KernelEvent layout (0x20): ident(0x00) filter(0x08) flags(0x0A)
        // fflags(0x0C) data(0x10) userdata(0x18).
        Span<byte> evt = stackalloc byte[0x20];
        Assert.True(memory.TryRead(eventsAddress, evt));
        Assert.Equal(eventIdent, BinaryPrimitives.ReadUInt64LittleEndian(evt[0x00..]));
        Assert.Equal(KernelEventQueueCompatExports.KernelEventFilterUser,
            BinaryPrimitives.ReadInt16LittleEndian(evt[0x08..]));
        Assert.Equal(triggerData, BinaryPrimitives.ReadUInt64LittleEndian(evt[0x10..]));
    }

    private static ulong CreateEqueue()
    {
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong outAddress = MemoryBase + 0x10;
        context[CpuRegister.Rdi] = outAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(context));
        Assert.True(context.TryReadUInt64(outAddress, out var handle));
        return handle;
    }

    private static (CpuContext Context, ulong OutAddress) NewContextWithOutSlot()
    {
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var context = new CpuContext(memory, Generation.Gen5);
        return (context, MemoryBase + 0x10);
    }
}
