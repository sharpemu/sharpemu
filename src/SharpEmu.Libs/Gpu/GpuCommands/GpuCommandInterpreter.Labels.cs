// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed partial class GpuCommandInterpreter
{
    private const uint GcrGl2MetadataInvalidate = 1u << 1;
    private const uint GcrGl0VectorInvalidate = 1u << 2;
    private const uint GcrGl1Invalidate = 1u << 3;
    private const uint GcrGl2Unshared = 1u << 4;
    private const uint GcrGl2Invalidate = 1u << 8;
    private const uint GcrGl2Writeback = 1u << 9;
    private const uint GcrOrder012 = 1u << 10;
    private const uint GcrOrder210 = 2u << 10;
    private const uint GcrKnownMask = GcrGl2MetadataInvalidate | GcrGl0VectorInvalidate | GcrGl1Invalidate |
        GcrGl2Unshared | GcrGl2Invalidate | GcrGl2Writeback | GcrOrder012 | GcrOrder210;

    private static int _unknownGcrWarnings;

    // The label is written to guest memory now; only the interrupt waits for the GPU.
    internal void WriteEndOfPipe(
        bool is64Bit,
        uint cachePolicy,
        uint eventWriteDestination,
        uint eopEventType,
        uint cacheAction,
        uint eventIndex,
        uint eventWriteSource,
        ulong destination,
        ulong value,
        uint interruptSelector,
        uint interruptContextId)
    {
        if (cachePolicy != 0)
        {
            throw _host.Fatal($"The end-of-pipe cache policy is not supported: policy={cachePolicy} destination=0x{destination:X16}.");
        }

        if (eventWriteDestination != 0)
        {
            throw _host.Fatal($"The end-of-pipe write destination is not supported: destination={eventWriteDestination} address=0x{destination:X16}.");
        }

        var withInterrupt = false;
        switch (interruptSelector)
        {
            case 0:
            case 3:
                break;
            case 1:
                if (!IsComputeQueue)
                {
                    QueueInterrupt(interruptContextId);
                    return;
                }

                withInterrupt = true;
                break;
            case 2:
                withInterrupt = true;
                break;
            default:
                throw _host.Fatal($"The end-of-pipe interrupt selector is unknown: selector={interruptSelector} destination=0x{destination:X16}.");
        }

        var eventId = InterruptEventId;
        switch (eventWriteSource)
        {
            case 1:
                if (!is64Bit)
                {
                    if (eopEventType == 0x2F && cacheAction == 0x00 && eventIndex == 0x06)
                    {
                        WriteGdsWords(destination, value, withInterrupt, interruptContextId);
                        return;
                    }
                }
                else if (eopEventType == 0x04 && cacheAction == 0x00 && eventIndex == 0x05)
                {
                    Write32(destination, (uint)value, withWriteBack: false, withInterrupt, eventId, interruptContextId);
                    return;
                }

                break;
            case 2:
                if (!is64Bit)
                {
                    if (eopEventType == 0x2F && eventIndex == 0x06)
                    {
                        switch (cacheAction)
                        {
                            case 0x00:
                                Write32(destination, (uint)value, withWriteBack: false, withInterrupt, eventId, interruptContextId);
                                return;
                            case 0x38:
                                Write32(destination, (uint)value, withWriteBack: true, withInterrupt, eventId, interruptContextId);
                                return;
                        }
                    }
                }
                else
                {
                    switch (cacheAction)
                    {
                        case 0x00:
                            switch (eopEventType)
                            {
                                case 0x04:
                                    if (eventIndex == 0x05)
                                    {
                                        Write64(destination, value, withWriteBack: false, withInterrupt, eventId, interruptContextId);
                                        return;
                                    }

                                    break;
                                case 0x14:
                                case 0x28:
                                    if (eventIndex == 0x00)
                                    {
                                        Write64(destination, value, withWriteBack: false, withInterrupt, eventId, interruptContextId);
                                        return;
                                    }

                                    break;
                                case 0x2B:
                                case 0x2D:
                                case 0x2F:
                                case 0x30:
                                    if (eventIndex == 0x00 && !withInterrupt)
                                    {
                                        Write64(destination, value, withWriteBack: false, withInterrupt, eventId, interruptContextId);
                                        return;
                                    }

                                    break;
                            }

                            break;
                        case 0x38:
                            switch (eopEventType)
                            {
                                case 0x04:
                                case 0x14:
                                case 0x28:
                                    if (((eopEventType is 0x04 or 0x28) && eventIndex == 0x05 && !withInterrupt) || eventIndex == 0x00)
                                    {
                                        Write64(destination, value, withWriteBack: true, withInterrupt, eventId, interruptContextId);
                                        return;
                                    }

                                    break;
                                case 0x2B:
                                case 0x2D:
                                    if (eventIndex == 0x00 && !withInterrupt)
                                    {
                                        Write64(destination, value, withWriteBack: true, withInterrupt, eventId, interruptContextId);
                                        return;
                                    }

                                    break;
                                case 0x2F:
                                    if (eventIndex == 0x06 && !withInterrupt)
                                    {
                                        Write64(destination, value, withWriteBack: true, withInterrupt, eventId, interruptContextId);
                                        return;
                                    }

                                    break;
                            }

                            break;
                        case 0x3B:
                            if (eopEventType == 0x04 && eventIndex == 0x05 && withInterrupt)
                            {
                                Write64(destination, value, withWriteBack: true, withInterrupt, eventId, interruptContextId);
                                return;
                            }

                            break;
                    }
                }

                break;
            case 4:
                if (is64Bit)
                {
                    var clock = EndOfPipe.ReadReferenceClock();
                    WriteQword(destination, clock);
                    switch (cacheAction)
                    {
                        case 0x00:
                            if ((eopEventType == 0x04 && eventIndex == 0x05) || (eopEventType == 0x28 && eventIndex == 0x00))
                            {
                                RecordClock(destination, clock, withWriteBack: false, withInterrupt, eventId, interruptContextId);
                                return;
                            }

                            break;
                        case 0x38:
                            if ((eopEventType == 0x04 && eventIndex is 0x00 or 0x05) || (eopEventType == 0x28 && eventIndex == 0x00))
                            {
                                RecordClock(destination, clock, withWriteBack: true, withInterrupt, eventId, interruptContextId);
                                return;
                            }

                            break;
                    }
                }

                break;
        }

        throw _host.Fatal(
            $"The end-of-pipe event type is unknown: source={eventWriteSource} type=0x{eopEventType:X2} action=0x{cacheAction:X2} " +
            $"index={eventIndex} bits={(is64Bit ? 64 : 32)} interrupt={interruptSelector} destination=0x{destination:X16}.");
    }

    private void Write32(ulong destination, uint value, bool withWriteBack, bool withInterrupt, int eventId, uint contextId)
    {
        WriteDword(destination, value);
        var kind = withInterrupt
            ? (withWriteBack ? EndOfPipeWriteKind.InterruptWriteBack32 : EndOfPipeWriteKind.Interrupt32)
            : (withWriteBack ? EndOfPipeWriteKind.WriteBack32 : EndOfPipeWriteKind.Write32);
        _host.RecordEndOfPipe(new EndOfPipeWrite(kind, SubmitId, destination, value, eventId, contextId));
    }

    private void Write64(ulong destination, ulong value, bool withWriteBack, bool withInterrupt, int eventId, uint contextId)
    {
        WriteQword(destination, value);
        var kind = withInterrupt
            ? (withWriteBack ? EndOfPipeWriteKind.InterruptWriteBack64 : EndOfPipeWriteKind.Interrupt64)
            : (withWriteBack ? EndOfPipeWriteKind.WriteBack64 : EndOfPipeWriteKind.Write64);
        _host.RecordEndOfPipe(new EndOfPipeWrite(kind, SubmitId, destination, value, eventId, contextId));
    }

    private void RecordClock(ulong destination, ulong clock, bool withWriteBack, bool withInterrupt, int eventId, uint contextId)
    {
        var kind = withInterrupt
            ? (withWriteBack ? EndOfPipeWriteKind.InterruptWriteBack64 : EndOfPipeWriteKind.Interrupt64)
            : (withWriteBack ? EndOfPipeWriteKind.ClockWriteBack : EndOfPipeWriteKind.ClockWrite);
        _host.RecordEndOfPipe(new EndOfPipeWrite(kind, SubmitId, destination, clock, eventId, contextId));
    }

    // GDS contents are only valid after the GPU finished, so this variant waits first.
    private void WriteGdsWords(ulong destination, ulong value, bool withInterrupt, uint contextId)
    {
        var wordOffset = (uint)(value & 0xFFFFu);
        var wordCount = (uint)(value >> 16);
        _host.SynchronizeGpu();
        var words = new uint[wordCount];
        _host.ReadGds(words, wordOffset, wordCount);
        WriteBytes(destination, System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(words));
        _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.GdsWrite32, SubmitId, destination, GdsWordOffset: wordOffset, GdsWordCount: wordCount));
        if (withInterrupt)
        {
            _host.TriggerInterrupt(InterruptEventId, contextId);
        }
    }

    internal void QueueInterrupt(uint interruptContextId) =>
        _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.InterruptOnly, SubmitId, EventId: InterruptEventId, ContextId: interruptContextId));

    private static bool ReleaseMemGcrNeedsBarrier(uint eopEventType, uint gcrControl) =>
        eopEventType != 0x28 ||
        (gcrControl & (GcrGl2MetadataInvalidate | GcrGl0VectorInvalidate | GcrGl1Invalidate | GcrGl2Invalidate | GcrGl2Writeback)) != 0;

    private static uint ReleaseMemCacheActionFromGcr(uint gcrControl) =>
        (gcrControl & GcrGl2Writeback) != 0 ? 0x38u : 0x00u;

    private static void WarnUnknownReleaseMemGcr(uint gcrControl)
    {
        var unknown = gcrControl & ~GcrKnownMask;
        if (unknown != 0 && Interlocked.Increment(ref _unknownGcrWarnings) <= 16)
        {
            Console.Error.WriteLine($"[LOADER][WARN] command_stream.release_mem_unknown_gcr bits=0x{unknown:X4}");
        }
    }

    // Native and wrapped packets use the same release-memory operation.
    internal void ReleaseMemory(
        uint cachePolicy,
        uint eopEventType,
        uint eventIndex,
        uint gcrControl,
        uint releaseDestination,
        uint dataSelection,
        uint interruptSelector,
        ulong destination,
        ulong value,
        uint interruptContextId)
    {
        if (releaseDestination > 1)
        {
            throw _host.Fatal($"The release-memory destination is not supported: destination={releaseDestination} address=0x{destination:X16}.");
        }

        if (dataSelection is not (0 or 1 or 2 or 3 or 5))
        {
            throw _host.Fatal($"The release-memory data selection is not supported: selection={dataSelection} address=0x{destination:X16}.");
        }

        WarnUnknownReleaseMemGcr(gcrControl);
        var gl2WriteBack = (gcrControl & GcrGl2Writeback) != 0;

        if (dataSelection == 0 || interruptSelector == 4 || (releaseDestination == 0 && destination == 0))
        {
            if (eopEventType != 0x28 || gcrControl != 0)
            {
                _host.EmitGlobalBarrier();
            }

            TriggerReleaseInterrupt(interruptSelector, interruptContextId);
            return;
        }

        if (ReleaseMemGcrNeedsBarrier(eopEventType, gcrControl))
        {
            _host.EmitGlobalBarrier();
        }

        var cacheAction = ReleaseMemCacheActionFromGcr(gcrControl);
        cachePolicy = 0;

        if (dataSelection == 1)
        {
            WriteEndOfPipe(false, cachePolicy, 0, 0x2F, cacheAction, 6, 2, destination, (uint)value, interruptSelector, interruptContextId);
            _host.Flush();
            return;
        }

        if (dataSelection == 5)
        {
            if (gl2WriteBack)
            {
                cacheAction = 0;
            }

            WriteEndOfPipe(false, cachePolicy, 0, 0x2F, cacheAction, 6, 1, destination, (uint)value, interruptSelector, interruptContextId);
            if (interruptSelector == 1)
            {
                _host.Flush();
            }

            return;
        }

        var keepNativeEventIndex =
            (eopEventType == 0x04 && cacheAction == 0x00) ||
            (eopEventType == 0x2F && eventIndex == 0x06 && cacheAction == 0x38);
        if (!keepNativeEventIndex)
        {
            eventIndex = 0;
        }

        // Data selection 3 samples the reference clock at the release point.
        var source = dataSelection == 3 ? 4u : 2u;
        WriteEndOfPipe(true, cachePolicy, 0, eopEventType, cacheAction, eventIndex, source, destination, value, interruptSelector, interruptContextId);
    }

    private void TriggerReleaseInterrupt(uint interruptSelector, uint interruptContextId)
    {
        switch (interruptSelector)
        {
            case 0:
            case 3:
                return;
            case 1:
            case 2:
            case 4:
                QueueInterrupt(interruptContextId);
                _host.Flush();
                return;
            default:
                throw _host.Fatal($"The release-memory interrupt selector is unknown: selector={interruptSelector}.");
        }
    }

    internal void RaiseEvent(uint eventType, uint eventIndex, ulong eventAddress)
    {
        var validCacheEventIndex = eventIndex is 0 or 7;
        switch (eventType)
        {
            case 0x07:
            case 0x0F:
            case 0x10:
                _host.EmitGlobalBarrier();
                break;
            case 0x16:
            case 0x31:
            case 0x2A:
            case 0x2C:
            case 0x2E:
                if (!validCacheEventIndex)
                {
                    throw _host.Fatal($"The event type is unknown: type=0x{eventType:X8} index=0x{eventIndex:X8}.");
                }

                _host.EmitGlobalBarrier();
                break;
            case 0x0D:
            case 0x0E:
            case 0x12:
            case 0x17:
            case 0x18:
            case 0x19:
            case 0x1A:
            case 0x1B:
            case 0x38:
            case 0x3A:
                break;
            case 0x39:
            {
                if (eventIndex != 1 || eventAddress == 0 || (eventAddress & 0x7u) != 0)
                {
                    throw _host.Fatal($"The occlusion-counter dump is invalid: index=0x{eventIndex:X8} address=0x{eventAddress:X16}.");
                }

                // Publish visible occlusion results for each depth block.
                const ulong readyBit = 1UL << 63;
                var result = readyBit | SyntheticOcclusionCounter;
                for (var depthBlock = 0u; depthBlock < 16u; depthBlock++)
                {
                    WriteQword(eventAddress + ((ulong)depthBlock * 2 * sizeof(ulong)), result);
                }

                SyntheticOcclusionCounter = (SyntheticOcclusionCounter + 1) & (readyBit - 1);
                break;
            }

            default:
                throw _host.Fatal($"The event type is unknown: type=0x{eventType:X8} index=0x{eventIndex:X8}.");
        }
    }

    // Flip packets first need a queue slot; without one the submission retries later.
    private bool TryReserveFlipSlot()
    {
        if (_host.HasFlipSlot())
        {
            return true;
        }

        Suspend();
        return false;
    }

    internal void SubmitFlip()
    {
        if (!TryReserveFlipSlot())
        {
            return;
        }

        var flip = PendingFlip;
        var requestId = _host.PrepareFlip(flip.Handle, flip.Index, flip.FlipMode, flip.FlipArgument);
        _host.RecordEndOfPipe(new EndOfPipeWrite(
            EndOfPipeWriteKind.Flip, SubmitId, FlipHandle: flip.Handle, FlipIndex: flip.Index, FlipMode: flip.FlipMode, FlipArgument: flip.FlipArgument, FlipRequestId: requestId));
        _host.Flush();
    }

    internal void SubmitFlipWithLabel(ulong destination, uint value)
    {
        if (!TryReserveFlipSlot())
        {
            return;
        }

        WriteDword(destination, value);
        var flip = PendingFlip;
        var requestId = _host.PrepareFlip(flip.Handle, flip.Index, flip.FlipMode, flip.FlipArgument);
        _host.RecordEndOfPipe(new EndOfPipeWrite(
            EndOfPipeWriteKind.FlipWithWrite32, SubmitId, destination, value,
            FlipHandle: flip.Handle, FlipIndex: flip.Index, FlipMode: flip.FlipMode, FlipArgument: flip.FlipArgument, FlipRequestId: requestId));
        _host.Flush();
    }

    internal void SubmitFlipWithInterrupt(uint eopEventType, uint cacheAction, ulong destination, uint value)
    {
        if (eopEventType != 0x04 || cacheAction != 0x38)
        {
            throw _host.Fatal($"The flip event type is unknown: type=0x{eopEventType:X8} action=0x{cacheAction:X8} destination=0x{destination:X16}.");
        }

        if (!TryReserveFlipSlot())
        {
            return;
        }

        WriteDword(destination, value);
        var flip = PendingFlip;
        var requestId = _host.PrepareFlip(flip.Handle, flip.Index, flip.FlipMode, flip.FlipArgument);
        _host.RecordEndOfPipe(new EndOfPipeWrite(
            EndOfPipeWriteKind.FlipWithInterruptWriteBack32, SubmitId, destination, value, InterruptEventId,
            FlipHandle: flip.Handle, FlipIndex: flip.Index, FlipMode: flip.FlipMode, FlipArgument: flip.FlipArgument, FlipRequestId: requestId));
        _host.Flush();
    }

    internal void WaitForFlip(uint videoOutHandle, uint displayBufferIndex)
    {
        _host.Flush();
        if (!_host.IsFlipDone((int)videoOutHandle, (int)displayBufferIndex))
        {
            Suspend();
        }
    }
}
