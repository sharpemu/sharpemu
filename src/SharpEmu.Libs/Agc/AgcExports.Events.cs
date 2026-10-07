// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial handles AGC event signaling, including flips and queued interrupts.

    private static readonly bool _traceAgcEqAccessors = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_AGC_EQ_ACCESSORS"),
        "1",
        StringComparison.Ordinal);
    private static long _agcEqAccessorTraceCount;

    [SysAbiExport(
        Nid = "cFazmnXpJOE",
        ExportName = "sceAgcAcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType >= 0x40)
        {
            return ReturnPointer(ctx, 0);
        }

        var hasAddress = (eventType & ~1u) == 0x38;
        var packetDwords = hasAddress ? 4u : 2u;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, hasAddress ? eventType | 0x100u : eventType & 0x3Fu))
        {
            return ReturnPointer(ctx, 0);
        }

        if (hasAddress &&
            (!TryWriteUInt32(ctx, commandAddress + 8, (uint)eventAddress & ~7u) ||
             !TryWriteUInt32(ctx, commandAddress + 12, (uint)(eventAddress >> 32))))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "aJf+j5yntiU",
        ExportName = "sceAgcDcbEventWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbEventWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var eventType = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var eventAddress = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || eventType > 0x3F || eventAddress != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItEventWrite, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, eventType))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_event_write buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} type={eventType}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "MWiElSNE8j8",
        ExportName = "sceAgcDcbWaitUntilSafeForRendering",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitUntilSafeForRendering(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RWaitFlipDone)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, displayBufferIndex) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_wait_safe buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "YUeqkyT7mEQ",
        ExportName = "sceAgcDcbSetFlip",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetFlip(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var videoOutHandle = (uint)ctx[CpuRegister.Rsi];
        var displayBufferIndex = (int)ctx[CpuRegister.Rdx];
        var flipMode = (uint)ctx[CpuRegister.Rcx];
        var flipArg = unchecked((ulong)ctx[CpuRegister.R8]);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(6, ItNop, RFlip)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, videoOutHandle) ||
            !TryWriteUInt32(ctx, commandAddress + 8, unchecked((uint)displayBufferIndex)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, flipMode) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(flipArg & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)(flipArg >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_flip buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} handle={videoOutHandle} index={displayBufferIndex} mode={flipMode} arg=0x{flipArg:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "w2rJhmD+dsE",
        ExportName = "sceAgcDriverAddEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverAddEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        var userData = ctx[CpuRegister.Rdx];
        if (!KernelEventQueueCompatExports.RegisterEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                userData))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_add_eq_event eq=0x{equeue:X16} id=0x{eventId:X16} udata=0x{userData:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "DL2RXaXOy88",
        ExportName = "sceAgcDriverDeleteEqEvent",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverDeleteEqEvent(CpuContext ctx)
    {
        var equeue = ctx[CpuRegister.Rdi];
        var eventId = ctx[CpuRegister.Rsi];
        if (!KernelEventQueueCompatExports.DeleteRegisteredEvent(
                equeue,
                eventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        TraceAgc($"agc.driver_delete_eq_event eq=0x{equeue:X16} id=0x{eventId:X16}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "5CdQTZIQPxM",
        ExportName = "sceAgcDriverGetEqEventType",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverGetEqEventType(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (!TryReadUInt32(ctx, eventAddress, out var eventType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ctx[CpuRegister.Rax] = eventType;
        TraceAgcEqAccessor(ctx, "event_type", eventAddress, eventType);
        return unchecked((int)eventType);
    }

    [SysAbiExport(
        Nid = "Zw7uUVPulbw",
        ExportName = "sceAgcDriverGetEqContextId",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverGetEqContextId(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (!TryReadUInt64(ctx, eventAddress + 0x10, out var eventData))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var contextId = (uint)eventData & 0x07FF_FFFFu;
        ctx[CpuRegister.Rax] = contextId;
        TraceAgcEqAccessor(ctx, "context_id", eventAddress, contextId);
        return unchecked((int)contextId);
    }

    private static void TraceAgcEqAccessor(
        CpuContext ctx,
        string accessor,
        ulong eventAddress,
        uint result)
    {
        if (!_traceAgcEqAccessors)
        {
            return;
        }

        var count = Interlocked.Increment(ref _agcEqAccessorTraceCount);
        if (count > 64 && (count & (count - 1)) != 0)
        {
            return;
        }

        Span<byte> eventBytes = stackalloc byte[0x20];
        if (!ctx.Memory.TryRead(eventAddress, eventBytes))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.eq_accessor count={count} " +
                $"accessor={accessor} event=0x{eventAddress:X16} " +
                $"result=0x{result:X8} snapshot=unreadable");
            return;
        }

        var ident = BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[0x00..]);
        var filter = BinaryPrimitives.ReadInt16LittleEndian(eventBytes[0x08..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(eventBytes[0x0A..]);
        var filterFlags = BinaryPrimitives.ReadUInt32LittleEndian(eventBytes[0x0C..]);
        var data = BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[0x10..]);
        var userData = BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[0x18..]);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.eq_accessor count={count} accessor={accessor} " +
            $"event=0x{eventAddress:X16} result=0x{result:X8} " +
            $"ident=0x{ident:X16} filter={filter} flags=0x{flags:X4} " +
            $"fflags=0x{filterFlags:X8} data=0x{data:X16} " +
            $"udata=0x{userData:X16} thread='{Thread.CurrentThread.Name}' " +
            $"managed={Environment.CurrentManagedThreadId}");
    }
    private static bool TryReadQueuedInterruptCondition(
        CpuContext ctx,
        uint interrupt,
        ulong address,
        out ulong value)
    {
        value = 0;
        if (interrupt == 5)
        {
            if (!TryReadLiveUInt32(ctx, address, out var value32))
            {
                return false;
            }

            value = value32;
            return true;
        }

        return interrupt == 6 && TryReadLiveUInt64(ctx, address, out value);
    }

}
