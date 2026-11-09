// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.GnmMode.Libraries.Gnm;

public static partial class GnmDriverExports
{
    private const ulong GraphicsEndOfPipeEvent = 0x40;
    private const int EventBadDescriptor = unchecked((int)0x80020009);
    private const int EventInvalidArgument = unchecked((int)0x80020016);
    private const int EventMemoryFault = unchecked((int)0x8002000E);

    public static int AddEqEvent(CpuContext context)
    {
        var handle = context[CpuRegister.Rdi];
        var id = context[CpuRegister.Rsi];
        if (!KernelEventQueueCompatExports.IsValidEqueue(handle))
            return context.SetReturn(EventBadDescriptor);
        if (!TryGetHostEvents(id, out var first, out var count))
            return context.SetReturn(EventInvalidArgument);
        for (var index = 0u; index < count; index++)
        {
            if (KernelEventQueueCompatExports.RegisterEvent(handle, first + index,
                KernelEventQueueCompatExports.KernelEventFilterGraphics, context[CpuRegister.Rdx], flags: 1))
                continue;
            for (var previous = 0u; previous < index; previous++)
                KernelEventQueueCompatExports.DeleteRegisteredEvent(handle, first + previous,
                    KernelEventQueueCompatExports.KernelEventFilterGraphics);
            return context.SetReturn(EventBadDescriptor);
        }
        return context.SetReturn(0);
    }

    public static int DeleteEqEvent(CpuContext context)
    {
        var handle = context[CpuRegister.Rdi];
        if (!KernelEventQueueCompatExports.IsValidEqueue(handle))
            return context.SetReturn(EventBadDescriptor);
        if (!TryGetHostEvents(context[CpuRegister.Rsi], out var first, out var count))
            return context.SetReturn(EventInvalidArgument);
        for (var index = 0u; index < count; index++)
            KernelEventQueueCompatExports.DeleteRegisteredEvent(handle, first + index,
                KernelEventQueueCompatExports.KernelEventFilterGraphics);
        return context.SetReturn(0);
    }

    public static int GetEqEventType(CpuContext context)
    {
        return context.SetReturn(context.TryReadUInt32(context[CpuRegister.Rdi] + 0x10, out var type)
            ? unchecked((int)type) : EventMemoryFault);
    }

    public static int WaitEqueue(CpuContext context)
    {
        var events = context[CpuRegister.Rsi];
        var count = context[CpuRegister.Rcx];
        var capacity = (uint)Math.Min(context[CpuRegister.Rdx], int.MaxValue);
        if (count == 0)
            return context.SetReturn(EventMemoryFault);
        var result = KernelEventQueueCompatExports.KernelWaitEqueue(context);
        if (result != 0)
            return context.SetReturn(result);
        if (GuestThreadExecution.TryConsumeCurrentThreadBlock(out var reason, out _, out _,
            out var wakeKey, out var waiter, out var deadline))
        {
            GuestThreadExecution.RequestCurrentThreadBlock(context, reason, wakeKey,
                waiter is null ? null : new GnmEventWaiter(context, events, count, capacity, waiter), deadline);
            return context.SetReturn(0);
        }
        return context.SetReturn(TranslateEvents(context, events, count, capacity, result));
    }

    private sealed class GnmEventWaiter(CpuContext context, ulong events, ulong count, uint capacity,
        IGuestThreadBlockWaiter inner) : IGuestThreadBlockWaiter
    {
        public bool TryWake() => inner.TryWake();

        public int Resume() => TranslateEvents(context, events, count, capacity, inner.Resume());
    }

    private static int TranslateEvents(CpuContext context, ulong events, ulong countAddress, uint capacity, int result)
    {
        if (result != 0)
            return result;
        if (!context.TryReadUInt32(countAddress, out var count))
            return EventMemoryFault;
        Span<byte> value = stackalloc byte[0x20];
        for (var index = 0u; index < Math.Min(count, capacity); index++)
        {
            var address = events + index * 0x20UL;
            if (!context.Memory.TryRead(address, value))
                return EventMemoryFault;
            if (BinaryPrimitives.ReadInt16LittleEndian(value[8..]) != KernelEventQueueCompatExports.KernelEventFilterGraphics)
                continue;
            var hostId = BinaryPrimitives.ReadUInt64LittleEndian(value);
            ulong guestId;
            if (hostId == 0)
                guestId = GraphicsEndOfPipeEvent;
            else if (hostId >= GpuCommandInterpreter.ComputeQueueBase &&
                hostId - GpuCommandInterpreter.ComputeQueueBase < ComputePipeCount * QueuesPerPipe)
                guestId = (hostId - GpuCommandInterpreter.ComputeQueueBase) / QueuesPerPipe;
            else
                continue;
            BinaryPrimitives.WriteUInt64LittleEndian(value, guestId);
            BinaryPrimitives.WriteUInt64LittleEndian(value[0x10..], guestId);
            if (!context.Memory.TryWrite(address, value))
                return EventMemoryFault;
        }
        return 0;
    }

    private static bool TryGetHostEvents(ulong id, out ulong first, out uint count)
    {
        first = 0;
        count = 1;
        if (id == GraphicsEndOfPipeEvent)
            return true;
        if (id >= ComputePipeCount)
            return false;
        first = GpuCommandInterpreter.ComputeQueueBase + id * QueuesPerPipe;
        count = QueuesPerPipe;
        return true;
    }
}
