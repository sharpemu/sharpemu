// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial handles AGC event signaling, including flips and queued interrupts.

    private static readonly bool _compatibilitySubmitCompletionEvent = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_AGC_SUBMIT_COMPLETION_EVENT"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceAgcEqAccessors = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_AGC_EQ_ACCESSORS"),
        "1",
        StringComparison.Ordinal);
    private static long _agcEqAccessorTraceCount;

    private sealed class SubmittedCompletionState
    {
        public bool RaisedQueuedInterrupt { get; set; }
    }

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

    private static void NotifySubmittedDcbCompleted(
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong submissionId)
    {
        if (state.CompletionEventNotifiedSubmissionId == submissionId)
        {
            return;
        }

        state.CompletionEventNotifiedSubmissionId = submissionId;
        var completionState = state.ActiveCompletionState;
        var completionEventId = state.CompletionEventId;
        var isGraphics = ReferenceEquals(state, gpuState.Graphics);
        var queueName = state.QueueName;
        void TriggerCompletionEvents()
        {
            // A queued interrupt already reports the ordered completion. Do not
            // add a second event for the same submission.
            if (completionState?.RaisedQueuedInterrupt == true)
            {
                TraceAgc(
                    $"agc.completion_event_suppressed queue={queueName} " +
                    $"submission={submissionId} reason=queued_interrupt");
                return;
            }

            var triggered = KernelEventQueueCompatExports.TriggerRegisteredEvents(
                completionEventId,
                KernelEventQueueCompatExports.KernelEventFilterGraphics,
                completionEventId);
            // The broad fan-out wakes graphics registrations whose ident never
            // matches anything the driver publishes. That is a compatibility
            // guess rather than hardware behavior, so it stays opt-in and stays
            // on the graphics queue where it was measured.
            if (isGraphics && _compatibilitySubmitCompletionEvent)
            {
                triggered += KernelEventQueueCompatExports.TriggerRegisteredEventsDistinct(
                    KernelEventQueueCompatExports.KernelEventFilterGraphics);
            }
            TraceAgc(
                $"agc.completion_event queue={queueName} submission={submissionId} " +
                $"event=0x{completionEventId:X} queues={triggered}");
        }

        // A submission is complete only after its translated Vulkan work and
        // ordered guest-memory writes have finished. Put the notification on that
        // same logical queue instead of approximating completion with a timer or a
        // ThreadPool hop, either of which can only make the interrupt late and
        // reorder it against registration changes (and can wake Unity while its
        // upload data is still stale).
        if (GuestGpu.Current.SubmitOrderedGuestAction(
                TriggerCompletionEvents,
                $"agc submit completion {submissionId}") == 0)
        {
            TriggerCompletionEvents();
        }
    }

    internal readonly record struct QueuedInterruptDecision(
        bool WritesData,
        bool RaisesInterrupt);

    internal static QueuedInterruptDecision EvaluateQueuedInterrupt(
        uint interrupt,
        bool isAsyncCompute,
        uint dataSelection,
        bool conditionReadable,
        ulong conditionValue,
        ulong data)
    {
        var writesData = dataSelection != 0 && interrupt switch
        {
            0 or 2 or 3 => true,
            1 => isAsyncCompute,
            _ => false,
        };
        var raisesInterrupt = interrupt switch
        {
            1 or 2 or 4 => true,
            5 => conditionReadable &&
                 unchecked((uint)conditionValue) <= unchecked((uint)data),
            6 => conditionReadable && conditionValue <= data,
            _ => false,
        };
        return new QueuedInterruptDecision(writesData, raisesInterrupt);
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

    private static readonly Dictionary<(ulong Source, ulong Destination), ulong> _softwarePresenterFingerprints = new();

    private static bool TrySoftwarePresent(
        CpuContext ctx,
        TextureDescriptor source,
        int videoOutHandle,
        int displayBufferIndex)
    {
        if (source.Format != Gen5TextureFormatR8G8B8A8Unorm ||
            source.TileMode != 0 ||
            source.Type != Gen5TextureType2D ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            !VideoOutExports.TryGetDisplayBufferInfo(videoOutHandle, displayBufferIndex, out var destination) ||
            destination.Address == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            destination.Width > 8192 ||
            destination.Height > 8192 ||
            destination.TilingMode != 0 ||
            destination.PixelFormat is not (
                VideoOutPixelFormatA8R8G8B8Srgb or
                VideoOutPixelFormatA8B8G8R8Srgb or
                VideoOutPixelFormat2R8G8B8A8Srgb or
                VideoOutPixelFormat2B8G8R8A8Srgb or
                VideoOutPixelFormat2R10G10B10A2 or
                VideoOutPixelFormat2B10G10R10A2 or
                VideoOutPixelFormat2R10G10B10A2Srgb or
                VideoOutPixelFormat2B10G10R10A2Srgb or
                VideoOutPixelFormat2R10G10B10A2Bt2100Pq or
                VideoOutPixelFormat2B10G10R10A2Bt2100Pq))
        {
            return false;
        }

        var sourceByteCount = checked((ulong)source.Width * source.Height * 4);
        if (sourceByteCount > 256UL * 1024UL * 1024UL)
        {
            return false;
        }

        var sourceBytes = new byte[(int)sourceByteCount];
        if (!ctx.Memory.TryRead(source.Address, sourceBytes))
        {
            return false;
        }

        var fingerprint = ComputeFingerprint(sourceBytes);
        var fingerprintKey = (source.Address, destination.Address);
        lock (_softwarePresenterGate)
        {
            if (_softwarePresenterFingerprints.TryGetValue(fingerprintKey, out var previousFingerprint) &&
                previousFingerprint == fingerprint)
            {
                return true;
            }
        }

        var destinationPitch = destination.PitchInPixel == 0
            ? destination.Width
            : destination.PitchInPixel;
        if (destinationPitch < destination.Width)
        {
            return false;
        }

        var destinationRow = new byte[checked((int)destinationPitch * 4)];
        var rgbaDestination = destination.PixelFormat is
            VideoOutPixelFormatA8B8G8R8Srgb or
            VideoOutPixelFormat2R8G8B8A8Srgb;
        var packed10Destination =
            VideoOutExports.IsPacked10BitPixelFormat(destination.PixelFormat);
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * 4));
                var destinationOffset = checked((int)x * 4);
                if (packed10Destination)
                {
                    if (!VideoOutExports.TryPackRgba8Pixel(
                            destination.PixelFormat,
                            sourceBytes[sourceOffset + 0],
                            sourceBytes[sourceOffset + 1],
                            sourceBytes[sourceOffset + 2],
                            sourceBytes[sourceOffset + 3],
                            out var packed))
                    {
                        return false;
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(
                        destinationRow.AsSpan(destinationOffset, sizeof(uint)),
                        packed);
                }
                else if (rgbaDestination)
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 0];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 2];
                }
                else
                {
                    destinationRow[destinationOffset + 0] = sourceBytes[sourceOffset + 2];
                    destinationRow[destinationOffset + 1] = sourceBytes[sourceOffset + 1];
                    destinationRow[destinationOffset + 2] = sourceBytes[sourceOffset + 0];
                }

                if (!packed10Destination)
                {
                    destinationRow[destinationOffset + 3] = sourceBytes[sourceOffset + 3];
                }
            }

            var destinationAddress = destination.Address + ((ulong)y * destinationPitch * 4);
            if (!ctx.Memory.TryWrite(destinationAddress, destinationRow))
            {
                return false;
            }
        }

        lock (_softwarePresenterGate)
        {
            _softwarePresenterFingerprints[fingerprintKey] = fingerprint;
        }

        VideoOutExports.SubmitHostRgbaFrame(sourceBytes, source.Width, source.Height);
        TraceAgc(
            $"agc.software_presenter src=0x{source.Address:X16} {source.Width}x{source.Height} fmt={source.Format}/num{source.NumberType} " +
            $"dst=0x{destination.Address:X16} {destination.Width}x{destination.Height} fingerprint=0x{fingerprint:X16}");
        return true;
    }
}
