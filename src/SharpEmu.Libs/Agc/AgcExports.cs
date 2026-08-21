// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Media;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // The backend is a process-fixed singleton, so its offset-alignment
    // requirement is snapshot once: several per-draw paths (shader-key
    // hashing, buffer-offset alignment) read it in loops.
    private static readonly ulong _storageBufferOffsetAlignment =
        GuestGpu.Current.GuestStorageBufferOffsetAlignment;

#if DEBUG
    static AgcExports()
    {
        ValidateWriteDataControlDecoders();
        ValidateDispatchInitiators();
        ValidateSubmittedQueueAndReleaseMemDecoders();
        ValidateAcquireMemAndQueueResetDecoders();
        ValidateDepthTargetDecoder();
    }
#endif

    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;
    private const uint ItNop = 0x10;
    private const uint ItSetBase = 0x11;
    private const uint ItIndexBufferSize = 0x13;
    private const uint ItIndexBase = 0x26;
    private const uint ItDrawIndirect = 0x24;
    private const uint ItDrawIndexIndirect = 0x25;
    private const uint ItDrawIndex2 = 0x27;
    private const uint ItIndexType = 0x2A;
    private const uint ItDrawIndexAuto = 0x2D;
    private const uint ItNumInstances = 0x2F;
    private const uint ItDrawIndexMultiAuto = 0x30;
    private const uint ItDrawIndexOffset2 = 0x35;
    private const uint ItDrawIndexIndirectMulti = 0x38;
    private const uint DrawIndexedIndirectArgsSize = 20;
    private const uint DrawIndexedIndirectMaxScan = 1024;
    private const uint ItWriteData = 0x37;
    private const uint ItDispatchDirect = 0x15;
    private const uint ItDispatchIndirect = 0x16;
    private const uint ItSetPredication = 0x20;
    private const uint ItCondExec = 0x22;
    private const uint ItWaitRegMem = 0x3C;
    private const uint ItIndirectBuffer = 0x3F;
    private const uint ItEventWrite = 0x46;
    private const uint ItReleaseMem = 0x49;
    private const uint ItDmaData = 0x50;
    private const uint ItRewind = 0x59;
    private const uint ItSetContextReg = 0x69;
    private const uint ItSetShReg = 0x76;
    private const uint ItSetUconfigReg = 0x79;
    private const uint RewindValidBit = 1u << 31;
    private const uint RewindOffloadEnableBit = 1u << 24;
    private const uint ItGetLodStats = 0x8E;

    private static readonly HashSet<uint> KnownPm4Opcodes =
    [
        ItNop, ItSetBase, ItIndexBufferSize, ItIndexBase, ItDrawIndirect,
        ItDrawIndexIndirect, ItDrawIndex2, ItIndexType, ItDrawIndexAuto,
        ItNumInstances, ItDrawIndexMultiAuto, ItDrawIndexOffset2, ItWriteData,
        ItMemSemaphore, ItCopyData,
        ItDispatchDirect, ItDispatchIndirect, ItCondExec, ItWaitRegMem,
        ItIndirectBuffer, ItEventWrite, ItReleaseMem, ItDmaData,
        ItSetContextReg, ItSetShReg, ItSetUconfigReg, ItGetLodStats,
    ];

    private const uint RZero = 0x00;
    private const uint RDrawIndexAuto = 0x04;
    private const uint RDrawReset = 0x05;
    private const uint RWaitFlipDone = 0x06;
    private const uint RAcbReset = 0x09;
    private const uint RWaitMem32 = 0x0A;
    private const uint RPushMarker = 0x0B;
    private const uint RPopMarker = 0x0C;
    private const uint RShRegsIndirect = 0x11;
    private const uint RCxRegsIndirect = 0x12;
    private const uint RUcRegsIndirect = 0x13;
    private const uint RAcquireMem = 0x14;
    private const uint RWriteData = 0x15;
    private const uint RWaitMem64 = 0x16;
    private const uint RFlip = 0x17;
    private const uint RReleaseMem = 0x18;
    private const uint RDmaData = 0x19;

    // Command rings advance through contiguous fixed-size chunks; the sentinel
    // terminator (IT_INDIRECT_BUFFER target=1 size=0) continues at the next one.
    private const uint RingChunkBytes = 0x10000;

    // release_mem here raises an EOP interrupt; above this range it's
    // GPU-internal queue sync with no interrupt.
    private const ulong GpuLabelPoolBase = 0x2000000000UL;
    private const ulong GpuLabelPoolSize = 0x10000UL;

    private static bool IsCpuVisibleLabel(ulong address) =>
        address >= GpuLabelPoolBase &&
        address < GpuLabelPoolBase + GpuLabelPoolSize;

    // SharpEmu still has GPU label consumers that read guest memory. Mirror
    // the value after the producer completes. A value of 0 keeps the label in
    // the virtual GL2 view for diagnostics only.
    private static readonly bool _gpuLabelHostMirrorEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_LABEL_HOST_MIRROR"),
        "0",
        StringComparison.Ordinal);
    // Async-compute ring tracking, env-gated. Off by default; only
    // validated against Ghost of Yotei.
    private static readonly bool _forceSubmitOrphanPreamblesEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES"),
        "1",
        StringComparison.Ordinal);
    private static readonly object _orphanPreambleGate = new();
    // Multiple producers can share one target label; last-writer-wins would
    // starve waits on the others.
    private static readonly Dictionary<ulong, List<ulong>> _cbReleaseMemTargets = new();
    // CMASK meta-state tracking: maps colour-buffer addresses to their
    // compression metadata.  Keyed by colour-buffer base address so the
    // consumption path (which only knows the surface address) can query
    // directly without a reverse lookup.
    private record struct MetaSurfaceInfo(
        ulong CmaskAddress,
        uint ClearWord0,
        uint ClearWord1,
        bool IsCleared);
    private static readonly Dictionary<ulong, MetaSurfaceInfo> _metaSurfaces = new();
    // Reverse map: CMASK address → colour-buffer address.  Needed so
    // CheckCmaskWrite (which only sees the write target address) can
    // find the owning surface.
    private static readonly Dictionary<ulong, ulong> _cmaskToColorBuffer = new();
    // Guards _metaSurfaces and _cmaskToColorBuffer.  Two threads touch them:
    // the parse thread (registration in TrackCmaskAddresses, CheckCmaskWrite
    // from DMA/compute writes, EFC consumption) and the render thread
    // (MarkAllSurfacesCleared at guest flip, IsMetaClearedForSurface /
    // ConsumeMetaClear / GetMetaClearValue at pass-record time).  Plain
    // Dictionaries corrupt under concurrent write, so every access below
    // holds this gate.  Keep the critical sections tiny and never block on
    // anything external while holding it.
    private static readonly object _metaSurfaceGate = new();
    // header -> {ring base, write cursor} of the last submitted slice.
    // Submissions stay cursor-bounded since rings aren't zeroed. Lap
    // distinguishes a stale cursor from a previous pass over the same base.
    private static readonly Dictionary<ulong, (ulong Base, ulong Cursor, long Lap)> _orphanPreambleSubmitted = new();
    private static readonly HashSet<ulong> _orphanPreambleUnreadableLogged = new();
    // Last {base, cursor} per builder header, to detect arena switches.
    // Updated only from release_mem builds, not every packet.
    private static readonly Dictionary<ulong, (ulong Base, ulong Cursor, ulong ThreadHandle, long Timestamp)> _builderArenaLastSeen = new();

    // Keyed by the literal write address (rounded to its 64KB chunk), not
    // the builder header's self-reported Base — that can be far from where
    // a persistent ring is actually stalled.
    private static readonly Dictionary<ulong, (ulong ThreadHandle, long Timestamp)> _ringChunkWriters = new();

    private static void RecordRingChunkWriter(ulong writeAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled || writeAddress == 0)
        {
            return;
        }

        var chunkKey = writeAddress & ~(ulong)(RingChunkBytes - 1);
        lock (_orphanPreambleGate)
        {
            _ringChunkWriters[chunkKey] = (GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
        }
    }

    /// <summary>
    /// Stall diagnostics: which guest thread last wrote near
    /// <paramref name="ringWindowStart"/>, and how long ago.
    /// </summary>
    public static bool TryFindRingProducer(ulong ringWindowStart, out ulong threadHandle, out double secondsSinceLastWrite)
    {
        threadHandle = 0;
        secondsSinceLastWrite = 0;

        var chunkKey = ringWindowStart & ~(ulong)(RingChunkBytes - 1);
        lock (_orphanPreambleGate)
        {
            if (_ringChunkWriters.TryGetValue(chunkKey, out var writer))
            {
                threadHandle = writer.ThreadHandle;
                secondsSinceLastWrite =
                    (System.Diagnostics.Stopwatch.GetTimestamp() - writer.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                return true;
            }
        }

        // Fallback: match by chunk-range membership, preferring the closest
        // base at or below the wait address (CommandBufferAddress is the
        // parse window start, not the arena's absolute base).
        var bestBase = 0UL;
        var found = false;
        (ulong Base, ulong Cursor, ulong ThreadHandle, long Timestamp) bestSeen = default;
        lock (_orphanPreambleGate)
        {
            foreach (var (_, seen) in _builderArenaLastSeen)
            {
                if (seen.Base > ringWindowStart ||
                    ringWindowStart >= seen.Base + RingChunkBytes ||
                    (found && seen.Base <= bestBase))
                {
                    continue;
                }

                bestBase = seen.Base;
                bestSeen = seen;
                found = true;
            }
        }

        if (!found)
        {
            lock (_orphanPreambleGate)
            {
                var count = _builderArenaLastSeen.Count;
                var closestDelta = ulong.MaxValue;
                var closestBase = 0UL;
                foreach (var (_, seen) in _builderArenaLastSeen)
                {
                    var delta = seen.Base > ringWindowStart ? seen.Base - ringWindowStart : ringWindowStart - seen.Base;
                    if (delta < closestDelta)
                    {
                        closestDelta = delta;
                        closestBase = seen.Base;
                    }
                }

                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.ring_producer_miss window=0x{ringWindowStart:X16} entries={count} " +
                    $"closest_base=0x{closestBase:X16} delta=0x{closestDelta:X16}");
            }

            return false;
        }

        threadHandle = bestSeen.ThreadHandle;
        secondsSinceLastWrite =
            (System.Diagnostics.Stopwatch.GetTimestamp() - bestSeen.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        return true;
    }

    private static readonly HashSet<ulong> _knownBuilderHeaders = new();
    // Unsubmitted tails of abandoned arenas — still valid, ring memory isn't zeroed.
    private static readonly List<(ulong Header, ulong Base, ulong Start, ulong End)> _orphanPreambleClosedSlices = new();

    private static bool IsKnownBuilderVtable(ulong vtable) =>
        vtable is 0x8009F5750UL or 0x800AB4550UL;
    // Ranges the game itself submitted; a header overlapping one is not an orphan.
    private static readonly Dictionary<ulong, (ulong End, long Seq)> _gameSubmittedRanges = new();
    private static long _orphanTrackSequence;
    private static readonly Dictionary<(ulong Header, ulong Base), long> _arenaLapStartSequences = new();
    // Submission is deferred here, not inline, since IsSuspended isn't set
    // until the suspending queue's parse call unwinds.
    private static readonly List<ulong> _orphanPreamblePendingTargets = new();
    private static uint _orphanPreambleSyntheticOwner = 900000;

    // Last packet address to write each fence label, for salvaging a stuck one.
    private static readonly Dictionary<ulong, List<(ulong Packet, ulong OwnerHeader)>> _fenceWritePacketSites = new();
    private static long _lastFenceSalvageTimestamp;
    private static long _lastFenceScanTimestamp;

    private const ulong FenceLabelRegionStart = 0x2000000000UL;
    private const ulong FenceLabelRegionEnd = 0x2000001000UL;

    private static void RecordFenceWritePacketSite(ulong packetAddress, ulong destinationAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled ||
            destinationAddress < FenceLabelRegionStart ||
            destinationAddress >= FenceLabelRegionEnd)
        {
            return;
        }

        lock (_orphanPreambleGate)
        {
            if (!_fenceWritePacketSites.TryGetValue(destinationAddress, out var sites))
            {
                sites = new List<(ulong, ulong)>();
                _fenceWritePacketSites[destinationAddress] = sites;
            }

            foreach (var (existingPacket, _) in sites)
            {
                if (existingPacket == packetAddress)
                {
                    return;
                }
            }

            if (sites.Count >= 8)
            {
                return;
            }

            // Resolve the owning builder now, before its header moves on.
            ulong owner = 0;
            foreach (var (headerAddress, seen) in _builderArenaLastSeen)
            {
                if (seen.Base != 0 &&
                    packetAddress >= seen.Base &&
                    packetAddress < seen.Base + 0x10000)
                {
                    owner = headerAddress;
                    break;
                }
            }

            sites.Add((packetAddress, owner));
        }
    }

    private static void SalvageStuckFenceWrites(CpuContext ctx, SubmittedGpuState gpuState)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - Volatile.Read(ref _lastFenceSalvageTimestamp) <
            System.Diagnostics.Stopwatch.Frequency / 10)
        {
            return;
        }

        Volatile.Write(ref _lastFenceSalvageTimestamp, now);

        var candidates = new List<(ulong Dest, ulong Packet, ulong Owner)>();
        lock (_orphanPreambleGate)
        {
            foreach (var (dest, sites) in _fenceWritePacketSites)
            {
                foreach (var (packet, owner) in sites)
                {
                    candidates.Add((dest, packet, owner));
                }
            }
        }

        var salvagedAny = false;
        foreach (var (dest, packet, recordedOwner) in candidates)
        {
            salvagedAny |= TrySalvageFenceWritePacket(ctx, gpuState, dest, packet, recordedOwner);
        }

        // Fallback when the learned-site probe finds nothing: a bounded scan
        // over tracked arenas, matching the label's high dword, handed to
        // the same strict validator (throttled to once a second).
        if (salvagedAny ||
            now - Volatile.Read(ref _lastFenceScanTimestamp) <
                System.Diagnostics.Stopwatch.Frequency)
        {
            return;
        }

        Volatile.Write(ref _lastFenceScanTimestamp, now);

        var destinations = new HashSet<ulong>();
        var windows = new HashSet<ulong>();
        lock (_orphanPreambleGate)
        {
            foreach (var (dest, sites) in _fenceWritePacketSites)
            {
                destinations.Add(dest);
                foreach (var (site, _) in sites)
                {
                    windows.Add(site & ~0xFFFFUL);
                }
            }

            foreach (var (_, seen) in _builderArenaLastSeen)
            {
                if (seen.Base != 0)
                {
                    windows.Add(seen.Base & ~0xFFFFUL);
                }
            }
        }

        foreach (var window in windows)
        {
            var end = window + 0x20000;
            for (var address = window + 12; address + 4 <= end; address += 4)
            {
                if (!TryReadUInt32(ctx, address, out var destinationHigh))
                {
                    break;
                }

                if (destinationHigh != 0x20u ||
                    !TryReadUInt32(ctx, address - 4, out var destinationLow))
                {
                    continue;
                }

                var dest = ((ulong)destinationHigh << 32) | destinationLow;
                if (destinations.Contains(dest))
                {
                    _ = TrySalvageFenceWritePacket(ctx, gpuState, dest, address - 12, recordedOwner: 0);
                }
            }
        }
    }

    private static bool TrySalvageFenceWritePacket(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong dest,
        ulong packet,
        ulong recordedOwner)
    {
        if (!TryReadUInt32(ctx, packet, out var header) ||
            (header >> 30) != 3)
        {
            return false;
        }

        var length = Pm4Length(header);
        var op = (header >> 8) & 0xFFu;
        var register = (header >> 2) & 0x3Fu;
        if (length < 4 || length > 16 ||
            (op != ItWriteData && !(op == ItNop && register == RWriteData)))
        {
            return false;
        }

        if (!TryReadUInt64(ctx, packet + 8, out var packetDest) ||
            packetDest != dest ||
            !TryReadUInt32(ctx, packet + 16, out var value) ||
            !TryReadUInt32(ctx, dest, out var current) ||
            value != current + 1)
        {
            return false;
        }

        var owner = recordedOwner;
        if (owner == 0)
        {
            lock (_orphanPreambleGate)
            {
                foreach (var (headerAddress, seen) in _builderArenaLastSeen)
                {
                    if (seen.Base != 0 &&
                        packet >= seen.Base &&
                        packet < seen.Base + 0x10000)
                    {
                        owner = headerAddress;
                        break;
                    }
                }

                if (owner == 0)
                {
                    // Any known builder's queue can parse this; identity only affects ordering.
                    foreach (var headerAddress in _builderArenaLastSeen.Keys)
                    {
                        owner = headerAddress;
                        break;
                    }
                }
            }
        }

        if (owner == 0)
        {
            return false;
        }

        Console.Error.WriteLine(
            $"[LOADER][WARN] agc.fence_write_salvage packet=0x{packet:X16} " +
            $"dst=0x{dest:X16} value=0x{value:X} current=0x{current:X} header=0x{owner:X}");
        // Deliberately unclipped: the packet may sit inside a range the
        // game submitted on an earlier lap; the strict current+1 check
        // already proves the content is this lap's, and re-executing a
        // literal write of the same value later is idempotent.
        SubmitOrphanSlice(ctx, gpuState, owner, packet, packet + (ulong)length * sizeof(uint), 0);
        return true;
    }

    private static ulong ExtendClosedSliceOverTrailingFenceWrites(
        CpuContext ctx,
        ulong sliceEnd)
    {
        // Crosses decoration packets (EVENT_WRITE, NOPs); only commits through a qualifying write_data.
        var tentativeEnd = sliceEnd;
        for (var walked = 0; walked < 8; walked++)
        {
            if (!TryReadUInt32(ctx, tentativeEnd, out var header) ||
                (header >> 30) != 3)
            {
                return sliceEnd;
            }

            var length = Pm4Length(header);
            if (length == 0 || length > 64)
            {
                return sliceEnd;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            var standardWriteData = op == ItWriteData && length >= 4;
            var agcWriteData = op == ItNop && register == RWriteData && length >= 4;
            if (!standardWriteData && !agcWriteData)
            {
                if (op == ItEventWrite || (op == ItNop && register == 0))
                {
                    tentativeEnd += (ulong)length * sizeof(uint);
                    continue;
                }

                return sliceEnd;
            }

            if (!TryReadUInt32(ctx, tentativeEnd + 4, out var control) ||
                !TryReadUInt64(ctx, tentativeEnd + 8, out var destination) ||
                destination == 0)
            {
                return sliceEnd;
            }

            var (dst, _, _, _) = standardWriteData
                ? DecodeStandardWriteDataControl(control)
                : DecodeAgcWriteDataControl(control);
            if (dst is not (1 or 2 or 4 or 5) ||
                !TryReadUInt32(ctx, tentativeEnd + 16, out var packetValue) ||
                !TryReadUInt32(ctx, destination, out var currentValue) ||
                packetValue <= currentValue)
            {
                return sliceEnd;
            }

            tentativeEnd += (ulong)length * sizeof(uint);
            sliceEnd = tentativeEnd;
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_slice_tail_extend end=0x{sliceEnd:X16} " +
                $"dst=0x{destination:X16} value=0x{packetValue:X} current=0x{currentValue:X}");
        }

        return sliceEnd;
    }

    private static void TrackCbReleaseMemTarget(
        CpuContext ctx,
        ulong commandBufferAddress,
        ulong destinationAddress)
    {
        // Deliberately NOT filtered to a CPU-visible-label pool: this tracker
        // needs every release_mem target, not just a subset.
        if (!_forceSubmitOrphanPreamblesEnabled || destinationAddress == 0)
        {
            return;
        }

        // Snapshot the header on every packet the builder records; when its
        // base changes, queue the closed arena's remaining slice for the
        // next drain, or its unsubmitted tail is lost for good.
        ulong closedBase = 0, closedStart = 0, closedEnd = 0;
        ulong arenaCursor = 0;
        var haveHeader =
            TryReadUInt64(ctx, commandBufferAddress, out var arenaBase) &&
            TryReadUInt64(ctx, commandBufferAddress + 0x10, out arenaCursor) &&
            arenaBase != 0;

        lock (_orphanPreambleGate)
        {
            if (!_cbReleaseMemTargets.TryGetValue(destinationAddress, out var headers))
            {
                headers = new List<ulong>();
                _cbReleaseMemTargets[destinationAddress] = headers;
            }

            if (!headers.Contains(commandBufferAddress))
            {
                headers.Add(commandBufferAddress);
            }

            _knownBuilderHeaders.Add(commandBufferAddress);
            if (haveHeader)
            {
                var hadSeen = _builderArenaLastSeen.TryGetValue(commandBufferAddress, out var seen);
                if (hadSeen &&
                    seen.Base != 0 &&
                    seen.Base != arenaBase &&
                    seen.Cursor > seen.Base)
                {
                    closedBase = seen.Base;
                    closedEnd = ExtendClosedSliceOverTrailingFenceWrites(ctx, seen.Cursor);
                    _arenaLapStartSequences.TryGetValue(
                        (commandBufferAddress, seen.Base),
                        out var closingLap);
                    closedStart =
                        _orphanPreambleSubmitted.TryGetValue(commandBufferAddress, out var submitted) &&
                        submitted.Base == seen.Base &&
                        submitted.Lap == closingLap
                            ? submitted.Cursor
                            : seen.Base;
                    if (closedStart < closedEnd)
                    {
                        _orphanPreambleClosedSlices.Add(
                            (commandBufferAddress, closedBase, closedStart, closedEnd));
                    }
                }

                if (!hadSeen || seen.Base != arenaBase)
                {
                    // The builder just (re-)entered this arena: a fresh lap
                    // begins, invalidating earlier-lap game submissions of
                    // these addresses for clipping purposes.
                    _arenaLapStartSequences[(commandBufferAddress, arenaBase)] =
                        ++_orphanTrackSequence;
                }

                _builderArenaLastSeen[commandBufferAddress] =
                    (arenaBase, arenaCursor, GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }

        if (closedBase != 0 && closedStart < closedEnd)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_arena_closed header=0x{commandBufferAddress:X16} " +
                $"base=0x{closedBase:X16} slice=0x{closedStart:X16}-0x{closedEnd:X16}");
        }
    }

    // Hooked at the exact WAIT_REG_MEM suspend site so ctx/gpuState identity
    // matches what GpuWaitRegistry filters waiters on.
    private static void TryForceSubmitOrphanPreamble(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong targetAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled)
        {
            return;
        }

        // Actual submission is deferred to DrainPendingOrphanPreambles.
        lock (_orphanPreambleGate)
        {
            if (_cbReleaseMemTargets.ContainsKey(targetAddress) &&
                !_orphanPreamblePendingTargets.Contains(targetAddress))
            {
                _orphanPreamblePendingTargets.Add(targetAddress);
            }
        }
    }

    private static void RecordGameSubmittedRange(ulong commandAddress, uint dwordCount)
    {
        if (!_forceSubmitOrphanPreamblesEnabled || commandAddress == 0 || dwordCount == 0)
        {
            return;
        }

        var end = commandAddress + (ulong)dwordCount * 4;
        lock (_orphanPreambleGate)
        {
            // Replace, never merge — a shorter re-submission (lap restart)
            // must not keep shielding bytes past its real end.
            _gameSubmittedRanges[commandAddress] = (end, ++_orphanTrackSequence);
        }
    }

    // Called only where no DCB parse is on the stack. Loops because
    // submitting one orphan buffer can suspend on the next stage's target.
    private static void DrainPendingOrphanPreambles(CpuContext ctx, SubmittedGpuState gpuState)
    {
        if (!_forceSubmitOrphanPreamblesEnabled)
        {
            return;
        }

        while (true)
        {
            (ulong Header, ulong Base, ulong Start, ulong End) slice;
            lock (_orphanPreambleGate)
            {
                if (_orphanPreambleClosedSlices.Count == 0)
                {
                    break;
                }

                slice = _orphanPreambleClosedSlices[0];
                _orphanPreambleClosedSlices.RemoveAt(0);
            }

            long lapSequence;
            lock (_orphanPreambleGate)
            {
                _arenaLapStartSequences.TryGetValue((slice.Header, slice.Base), out lapSequence);
            }

            SubmitOrphanSliceClipped(
                ctx,
                gpuState,
                slice.Header,
                slice.Start,
                slice.End,
                targetAddress: 0,
                minimumRangeSequence: lapSequence);
        }

        while (true)
        {
            ulong targetAddress;
            ulong[] pendingHeaders;
            lock (_orphanPreambleGate)
            {
                if (_orphanPreamblePendingTargets.Count == 0)
                {
                    return;
                }

                targetAddress = _orphanPreamblePendingTargets[0];
                _orphanPreamblePendingTargets.RemoveAt(0);

                // Offer every builder of this target, not just one — counter
                // fences only pass once all producers' release_mem lands.
                pendingHeaders = _cbReleaseMemTargets.TryGetValue(targetAddress, out var headers)
                    ? OrderHeadersByConstructionTimeLocked(headers)
                    : Array.Empty<ulong>();
            }

            foreach (var headerAddress in pendingHeaders)
            {
                ForceSubmitOrphanPreambleHeader(ctx, gpuState, headerAddress, targetAddress);
            }
        }
    }

    // FIFO-preserving flush of one header's staged closed-arena slices. See
    // the call site in ForceSubmitOrphanPreambleHeader: a header's current
    // arena must never be enqueued ahead of its own abandoned predecessor.
    private static void FlushClosedSlicesForHeader(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress)
    {
        while (true)
        {
            (ulong Header, ulong Base, ulong Start, ulong End) slice = default;
            long lapSequence = 0;
            lock (_orphanPreambleGate)
            {
                var found = false;
                for (var index = 0; index < _orphanPreambleClosedSlices.Count; index++)
                {
                    if (_orphanPreambleClosedSlices[index].Header == headerAddress)
                    {
                        slice = _orphanPreambleClosedSlices[index];
                        _orphanPreambleClosedSlices.RemoveAt(index);
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return;
                }

                _arenaLapStartSequences.TryGetValue((slice.Header, slice.Base), out lapSequence);
            }

            SubmitOrphanSliceClipped(
                ctx,
                gpuState,
                slice.Header,
                slice.Start,
                slice.End,
                targetAddress: 0,
                minimumRangeSequence: lapSequence);
        }
    }

    private static void ForceSubmitOrphanPreambleHeader(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress,
        ulong targetAddress)
    {
        if (!TryReadUInt64(ctx, headerAddress, out var commandAddress) ||
            !TryReadUInt64(ctx, headerAddress + 8, out var limitAddress) ||
            !TryReadUInt64(ctx, headerAddress + 0x10, out var cursor) ||
            !TryReadUInt64(ctx, headerAddress + 0x20, out var vtable) ||
            commandAddress == 0)
        {
            // Transient, not a permanent skip — a builder can read as garbage
            // before it's fully constructed and become valid later.
            lock (_orphanPreambleGate)
            {
                if (!_orphanPreambleUnreadableLogged.Add(headerAddress))
                {
                    return;
                }
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_preamble_skip header=0x{headerAddress:X16} " +
                $"target=0x{targetAddress:X16} (unreadable at trigger time; will retry)");
            return;
        }

        if (!IsKnownBuilderVtable(vtable))
        {
            lock (_orphanPreambleGate)
            {
                if (_orphanPreambleSubmitted.TryGetValue(headerAddress, out var seen) &&
                    seen.Base == 0)
                {
                    return;
                }

                _orphanPreambleSubmitted[headerAddress] = (0, 0, 0);
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.orphan_preamble_skip header=0x{headerAddress:X16} " +
                $"target=0x{targetAddress:X16} vtable=0x{vtable:X16} (not an orphan builder class)");
            return;
        }

        // Covers a builder that moves to a new arena without ever building
        // another release_mem, which would otherwise leave its abandoned tail lost.
        lock (_orphanPreambleGate)
        {
            if (_builderArenaLastSeen.TryGetValue(headerAddress, out var lastSeen) &&
                lastSeen.Base != 0 &&
                lastSeen.Base != commandAddress &&
                lastSeen.Cursor > lastSeen.Base)
            {
                _arenaLapStartSequences.TryGetValue(
                    (headerAddress, lastSeen.Base),
                    out var closingLap);
                var closedStart =
                    _orphanPreambleSubmitted.TryGetValue(headerAddress, out var submittedBefore) &&
                    submittedBefore.Base == lastSeen.Base &&
                    submittedBefore.Lap == closingLap
                        ? submittedBefore.Cursor
                        : lastSeen.Base;
                var closedEnd = ExtendClosedSliceOverTrailingFenceWrites(ctx, lastSeen.Cursor);
                if (closedStart < closedEnd)
                {
                    _orphanPreambleClosedSlices.Add(
                        (headerAddress, lastSeen.Base, closedStart, closedEnd));
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.orphan_arena_closed header=0x{headerAddress:X16} " +
                        $"base=0x{lastSeen.Base:X16} slice=0x{closedStart:X16}-0x{closedEnd:X16} " +
                        "(sweep-detected switch)");
                }

                _arenaLapStartSequences[(headerAddress, commandAddress)] = ++_orphanTrackSequence;
                _builderArenaLastSeen[headerAddress] =
                    (commandAddress, cursor, lastSeen.ThreadHandle, lastSeen.Timestamp);
            }
        }

        // Flush this header's staged closures first, or the current-arena
        // submission below could overtake its own predecessor.
        FlushClosedSlicesForHeader(ctx, gpuState, headerAddress);

        if (cursor <= commandAddress)
        {
            return;
        }

        // Extends past the cursor for fence writes some titles make via raw
        // guest code, invisible to any AGC builder API.
        var extendedEnd = ExtendClosedSliceOverTrailingFenceWrites(ctx, cursor);

        ulong sliceStart;
        long lapSequence;
        lock (_orphanPreambleGate)
        {
            _arenaLapStartSequences.TryGetValue((headerAddress, commandAddress), out lapSequence);
            if (_orphanPreambleSubmitted.TryGetValue(headerAddress, out var last))
            {
                if (last.Base == 0)
                {
                    return; // permanently skipped
                }

                if (last.Base == commandAddress &&
                    last.Lap == lapSequence &&
                    cursor == last.Cursor)
                {
                    if (extendedEnd == cursor)
                    {
                        return; // no new content since the last slice
                    }

                    // Cursor unchanged; only the raw-written fence tail is new.
                    sliceStart = cursor;
                }
                else
                {
                    // Same base+lap grown -> delta only. Otherwise restart
                    // from the arena base; cross-lap cursors are meaningless.
                    sliceStart = last.Base == commandAddress &&
                        last.Lap == lapSequence &&
                        cursor > last.Cursor
                        ? last.Cursor
                        : commandAddress;
                }
            }
            else
            {
                sliceStart = commandAddress;
            }

            _orphanPreambleSubmitted[headerAddress] = (commandAddress, cursor, lapSequence);
        }

        SubmitOrphanSliceClipped(
            ctx,
            gpuState,
            headerAddress,
            sliceStart,
            extendedEnd,
            targetAddress,
            minimumRangeSequence: lapSequence);
    }

    // Clips out sub-ranges the game already submitted, to avoid double-executing them.
    private static void SubmitOrphanSliceClipped(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress,
        ulong sliceStart,
        ulong sliceEnd,
        ulong targetAddress,
        long minimumRangeSequence = 0)
    {
        while (sliceStart < sliceEnd)
        {
            ulong segmentEnd;
            lock (_orphanPreambleGate)
            {
                var advanced = true;
                while (advanced)
                {
                    advanced = false;
                    foreach (var (rangeStart, range) in _gameSubmittedRanges)
                    {
                        if (range.Seq >= minimumRangeSequence &&
                            sliceStart >= rangeStart && sliceStart < range.End)
                        {
                            sliceStart = range.End;
                            advanced = true;
                        }
                    }
                }

                if (sliceStart >= sliceEnd)
                {
                    return;
                }

                segmentEnd = sliceEnd;
                foreach (var (rangeStart, range) in _gameSubmittedRanges)
                {
                    if (range.Seq >= minimumRangeSequence &&
                        rangeStart > sliceStart && rangeStart < segmentEnd)
                    {
                        segmentEnd = rangeStart;
                    }
                }
            }

            SubmitOrphanSlice(ctx, gpuState, headerAddress, sliceStart, segmentEnd, targetAddress);
            sliceStart = segmentEnd;
        }
    }

    // Catches labels only ever polled via CPU-side usleep loops (no waiter registered).
    private static void SweepBuilderArenas(CpuContext ctx, SubmittedGpuState gpuState)
    {
        ulong[] headers;
        lock (_orphanPreambleGate)
        {
            if (_knownBuilderHeaders.Count == 0)
            {
                return;
            }

            headers = OrderHeadersByConstructionTimeLocked(_knownBuilderHeaders);
        }

        foreach (var headerAddress in headers)
        {
            ForceSubmitOrphanPreambleHeader(ctx, gpuState, headerAddress, targetAddress: 0);
        }
    }

    // Sorts by wall-clock time of each header's latest checkpoint, oldest
    // first. Must be called with _orphanPreambleGate already held.
    private static ulong[] OrderHeadersByConstructionTimeLocked(IEnumerable<ulong> headers)
    {
        var ordered = headers.ToArray();
        Array.Sort(ordered, (a, b) =>
        {
            var hasA = _builderArenaLastSeen.TryGetValue(a, out var seenA);
            var hasB = _builderArenaLastSeen.TryGetValue(b, out var seenB);
            var tsA = hasA ? seenA.Timestamp : 0;
            var tsB = hasB ? seenB.Timestamp : 0;
            return tsA.CompareTo(tsB);
        });
        return ordered;
    }

    private static void SubmitOrphanSlice(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        ulong headerAddress,
        ulong sliceStart,
        ulong sliceEnd,
        ulong targetAddress)
    {
        var dwordCount = (uint)((sliceEnd - sliceStart) / 4);
        if (dwordCount == 0)
        {
            return;
        }

        uint owner;
        lock (_orphanPreambleGate)
        {
            owner = ++_orphanPreambleSyntheticOwner;
        }

        Console.Error.WriteLine(
            $"[LOADER][WARN] agc.orphan_preamble_force_submit header=0x{headerAddress:X16} " +
            $"command=0x{sliceStart:X16} dwords={dwordCount} targetLabel=0x{targetAddress:X16} " +
            $"owner={owner}");

        lock (gpuState.Gate)
        {
            var queueState = new SubmittedDcbState
            {
                QueueName = $"acb.orphan_preamble[0x{headerAddress:X}]",
                IsForceSubmittedRing = true,
            };
            gpuState.ComputeQueues.Add(owner, queueState);
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                queueState,
                sliceStart,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets: true,
                indexSnapshots: null,
                vertexSnapshots: null);
            DrainResumableDcbs(ctx, gpuState, tracePackets: true);
        }
    }

    // Parse window for a ring resuming at appended commands: covers a full
    // chunk, safe since parsing re-suspends at the next unwritten word.
    private const uint RingResumeWindowDwords = 0x8000;
    private const uint RIndexBase = 0x1B;
    private const uint RIndexCount = 0x1C;
    private const uint SpiShaderPgmLoPs = 0x8;
    private const uint SpiShaderPgmHiPs = 0x9;
    private const uint SpiShaderPgmLoVs = 0x48;
    private const uint SpiShaderPgmHiVs = 0x49;
    private const uint SpiShaderPgmLoEs = 0xC8;
    private const uint SpiShaderPgmHiEs = 0xC9;
    private const uint SpiShaderPgmLoHs = 0x108;
    private const uint SpiShaderPgmHiHs = 0x109;
    private const uint SpiShaderPgmRsrc1Hs = 0x10A;
    private const uint SpiShaderPgmLoLs = 0x148;
    private const uint SpiShaderPgmHiLs = 0x149;
    // Not 0x8A/0x8B - those are SPI_SHADER_PGM_RSRC1/RSRC2_GS, and reading them
    // as an address yields a nonsensical 58-bit value.
    private const uint SpiShaderPgmLoGs = 0x88;
    private const uint SpiShaderPgmHiGs = 0x89;
    private const uint SpiShaderPgmRsrc1Gs = 0x8A;
    private const uint SpiShaderPgmChksumGs = 0x80;
    private const uint SpiPsInputEna = 0x1B3;
    private const uint SpiPsInputAddr = 0x1B4;
    private const uint ComputePgmLo = 0x20C;
    private const uint ComputePgmHi = 0x20D;
    private const uint ComputePgmRsrc2 = 0x213;
    private const uint ComputeStartX = 0x204;
    private const uint ComputeStartY = 0x205;
    private const uint ComputeStartZ = 0x206;
    private const uint ComputeNumThreadX = 0x207;
    private const uint ComputeNumThreadY = 0x208;
    private const uint ComputeNumThreadZ = 0x209;
    private const uint SpiPsInputCntl0 = 0x191;
    private const uint VgtPrimitiveType = 0x242;
    private const uint VgtIndexType = 0x243;
    // GE_INDX_OFFSET — base vertex for DrawIndexed / firstVertex for
    // DrawIndexAuto. Glyph meshes and UI icon batches rely on this.
    private const uint GeIndxOffset = 0x24A;
    private const uint PaScScreenScissorTl = 0x0C;
    private const uint PaScScreenScissorBr = 0x0D;
    private const uint CbTargetMask = 0x8E;
    private const uint PaScWindowOffset = 0x80;
    private const uint PaScWindowScissorTl = 0x81;
    private const uint PaScWindowScissorBr = 0x82;
    private const uint PaScGenericScissorTl = 0x90;
    private const uint PaScGenericScissorBr = 0x91;
    private const uint PaScVportScissor0Tl = 0x94;
    private const uint PaScVportScissor0Br = 0x95;
    private const uint PaClVportXScale = 0x10F;
    private const uint PaClVportXOffset = 0x110;
    private const uint PaClVportYScale = 0x111;
    private const uint PaClVportYOffset = 0x112;
    private const uint PaScVportZMin0 = 0xB4;
    private const uint PaScVportZMax0 = 0xB5;
    private const uint CbColorControl = 0x202;
    private const uint CbBlendRed = 0x105;
    private const uint CbBlendGreen = 0x106;
    private const uint CbBlendBlue = 0x107;
    private const uint CbBlendAlpha = 0x108;
    private const uint CbColor0Base = 0x318;
    private const uint CbColorRegisterStride = 15;
    private const uint CbColor0Info = 0x31C;
    private const uint CbColor0Cmask = 0x31F;
    private const uint CbColor0ClearWord0 = 0x323;
    private const uint CbColor0ClearWord1 = 0x324;
    private const uint CbColor0DccBase = 0x325;
    private const uint CbColor0BaseExt = 0x390;
    private const uint CbColor0CmaskBaseExt = 0x398;
    private const uint CbColor0DccBaseExt = 0x3A8;
    private const uint CbColor0Attrib2 = 0x3B0;
    private const uint CbColor0Attrib3 = 0x3B8;
    // CB_COLORn_INFO.DCC_ENABLE (gc_10_1_0_sh_mask.h). On GFX10 the legacy
    // FAST_CLEAR and COMPRESSION bits stay clear because DCC, not CMASK,
    // carries the compression.
    private const uint CbColorInfoDccEnableMask = 1u << 28;
    private const uint CbColorInfoFastClearEnableMask = 1u << 12;
    private const uint CbBlend0Control = 0x1E0;
    private const uint PaScModeCntl0 = 0x292;
    // GFX10 DB context registers (register byte address minus 0x28000, / 4).
    private const uint DbRenderControl = 0x000;
    private const uint DbDepthView = 0x002;
    private const uint DbHtileDataBase = 0x005;
    private const uint DbDepthSizeXy = 0x007;
    private const uint DbDepthClear = 0x00B;
    private const uint DbZInfo = 0x010;
    private const uint DbZReadBase = 0x012;
    private const uint DbZWriteBase = 0x014;
    private const uint DbZReadBaseHi = 0x01A;
    private const uint DbZWriteBaseHi = 0x01C;
    private const uint DbHtileDataBaseHi = 0x01E;
    private const uint DbHtileSurface = 0x2AF;
    private const int ColorTargetCount = 8;
    private const uint PsTextureUserDataRegister = 0xC;
    private const uint VsUserDataRegister = 0x4C;
    private const uint GsUserDataRegister = 0x8C;
    private const uint EsUserDataRegister = 0xCC;
    private const uint ComputeUserDataRegister = 0x240;
    private const uint NggUserDataScalarRegisterBase = 8;
    internal const uint Gen5TextureFormatR8G8B8A8Unorm = 10;
    internal const uint Gen5TextureFormatR16G16B16A16Float = 12;
    private const uint Gen5TextureType1D = 8;
    private const uint Gen5TextureType2D = 9;
    private const uint Gen5TextureType3D = 10;
    private const uint Gen5TextureTypeCube = 11;
    private const uint Gen5TextureType1DArray = 12;
    private const uint Gen5TextureType2DArray = 13;
    private const ulong MaxPresentedTextureBytes = 128UL * 1024UL * 1024UL;
    private const ulong VideoOutPixelFormatA8R8G8B8Srgb = 0x80000000;
    private const ulong VideoOutPixelFormatA8B8G8R8Srgb = 0x80002200;
    private const ulong VideoOutPixelFormat2R8G8B8A8Srgb = 0x8000000022000000;
    private const ulong VideoOutPixelFormat2B8G8R8A8Srgb = 0x8000000000000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2 = 0x8100000622000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2 = 0x8100000600000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2Srgb = 0x8100000022000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2Srgb = 0x8100000000000000;
    private const ulong VideoOutPixelFormat2R10G10B10A2Bt2100Pq = 0x8100070422000000;
    private const ulong VideoOutPixelFormat2B10G10R10A2Bt2100Pq = 0x8100070400000000;
    private const uint RegisterDefaultsVersion7 = 7;
    private const uint RegisterDefaultsVersion8 = 8;
    private const uint RegisterDefaultsVersion10 = 10;
    private const uint RegisterDefaultsVersion13 = 13;
    private const int RegisterDefaultsSize = 0x40;
    private const int RegisterDefaultBlockSize = 16 * 8;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderCxRegistersOffset = 0x18;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderInputSemanticsOffset = 0x30;
    private const ulong ShaderOutputSemanticsOffset = 0x38;
    private const ulong ShaderSizeOffset = 0x44;
    private const ulong ResourceRegistrationBytesPerResource = 0x118;
    private const ulong ResourceRegistrationBytesPerOwner = 0x1E0;
    private const int ResourceRegistrationMaxNameLength = 256;
    private const ulong ShaderNumInputSemanticsOffset = 0x50;
    private const ulong ShaderNumOutputSemanticsOffset = 0x56;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;
    private const int ShaderStructBytes = 0x60;
    private const uint MaximumDeclaredShaderSizeBytes = 1024 * 1024;
    private const int MaximumEmbeddedFusedScanBytes = 64 * 1024;
    private const ulong FusedShaderImageAlignment = 4;
    private const byte ComputeShaderType = 0;
    private const byte PsShaderType = 1;
    private const byte GsShaderType = 2;
    private const byte HsShaderType = 3;
    private const byte GsFrontShaderType = 4;
    private const byte HsFrontShaderType = 5;
    private const byte GsBackShaderType = 6;
    private const byte HsBackShaderType = 7;
    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
    private const ulong CommandBufferUserDataOffset = 0x28;
    private const ulong CommandBufferReservedDwOffset = 0x30;
    private const ulong ShaderSpecialGeCntlOffset = 0x00;
    private const ulong ShaderSpecialVgtShaderStagesEnOffset = 0x08;
    private const uint VgtShaderStagesHsW32EnBit = 1u << 21;
    private const uint VgtShaderStagesGsW32EnBit = 1u << 22;
    private const ulong ShaderSpecialVgtGsOutPrimTypeOffset = 0x20;
    private const ulong ShaderSpecialGeUserVgprEnOffset = 0x28;
    private const uint CbSetShRegisterRangeMarker = 0x6875000D;
    private static readonly object _submitTraceGate = new();
    private static readonly HashSet<uint> _tracedDcbSizes = new();
    private static readonly HashSet<(ulong Es, ulong Ps, GuestDrawKind Kind)> _tracedShaderTranslations = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedShaderDecodePairs = new();
    private static readonly HashSet<(ulong Es, ulong Ps, ulong Target, ulong Texture, uint VertexCount)> _tracedShaderDraws = new();
    private static readonly HashSet<(ulong Ps, string Error)> _tracedShaderFailures = new();
    private static readonly HashSet<(int Handle, int Index, ulong Address, string Path)> _tracedDisplayBuffers = new();
    private static readonly HashSet<ulong> _tracedComputeShaders = new();
    private static readonly HashSet<ulong> _tracedEmptySrtDrawRejects = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedFixedFullscreenClears = new();
    private static readonly HashSet<(ulong Address, uint X, uint Y, uint Z)>
        _tracedDispatchArguments = new();
    private static readonly HashSet<(ulong Address, uint Initiator, string Reason)>
        _rejectedDispatchArguments = new();
    private static readonly HashSet<uint> _tracedSubmittedDrawOpcodes = new();
    // Every PM4 opcode with no handler, logged once with its first two
    // payload dwords as a possible target address.
    private static readonly HashSet<uint> _seenUnknownOpcodes = new();
    // Concurrent so the per-draw/per-dispatch hit path is lock-free (and no longer
    // shares _submitTraceGate with tracing).
    private static readonly ConcurrentDictionary<
        (ulong Es, ulong EsState, ulong Ps, ulong PsState, ulong OutputLayout,
         ulong OutputMappings, uint OutputCount, uint Attributes, uint PsInputEna,
         uint PsInputAddr, ulong PsInputCntl, ulong AliasAlignment),
        (IGuestCompiledShader Vertex, IGuestCompiledShader Pixel)> _graphicsShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Cs, ulong State, uint LocalX, uint LocalY, uint LocalZ,
         uint WaveLanes, ulong AliasAlignment),
        IGuestCompiledShader> _computeShaderCache = new();
    private static readonly ConcurrentDictionary<
        (ulong Es, ulong State, ulong AliasAlignment),
        IGuestCompiledShader> _depthOnlyVertexShaderCache = new();
    private static readonly Dictionary<ulong, ulong> _shaderHeadersByCode = new();
    private static readonly ConditionalWeakTable<
        object,
        ConcurrentDictionary<(ulong Code, ulong Header), byte>>
        _embeddedFusedScanAttempts = new();
    private static readonly ConcurrentDictionary<ulong, byte> _arrayUploadUnsupported = new();
    private static readonly bool _traceAgc = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
        "1",
        StringComparison.Ordinal);
    // Drop a draw on an undecodable texture descriptor instead of substituting
    // a 1x1 fallback binding. Off by default so a garbage descriptor degrades
    // the pass rather than dropping it (Demon's Souls composite feeders).
    private static readonly bool _strictShaderDescriptors = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_STRICT_SHADER_DESCRIPTORS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceAgcShader =
        _traceAgc ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
            "1",
            StringComparison.Ordinal);
    private static readonly ulong? _traceComputeShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COMPUTE_SHADER_ADDRESS"));
    private static readonly ulong? _tracePixelShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_PIXEL_SHADER_ADDRESS"));
    private static readonly bool _traceDepthMetadata = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DEPTH_METADATA"),
        "1",
        StringComparison.Ordinal);
    private static readonly ConcurrentDictionary<
        (ulong Depth, ulong Htile, uint ZInfo, uint Surface, uint Control, uint RenderControl), byte>
        _tracedDepthMetadataStates = new();
    private static readonly ConcurrentDictionary<(ulong Htile, string Source), byte>
        _tracedHtileMetadataMarks = new();
    private static readonly ConcurrentDictionary<
        (ulong Depth, ulong Htile, uint Layer, uint ClearBits), byte>
        _tracedHtileMetadataConsumes = new();
    private static int _depthMetadataTraceCount;
    private static readonly ulong? _traceRenderTargetAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_RENDER_TARGET_ADDRESS"));
    // ACQUIRE_MEM does not update an image when CPU image tracking is off.
    // Do not add an empty ordered action. Set the option to 0 to add this action.
    private static readonly bool _skipNoopAcquireMem = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_SKIP_NOOP_ACQUIRE_MEM"),
        "0",
        StringComparison.Ordinal);
    private static readonly bool _traceDraws = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_DRAWS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceFramePackets = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FRAME_PACKETS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceVertexRanges = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VERTEX_RANGES"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _compatibilitySubmitCompletionEvent = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_AGC_SUBMIT_COMPLETION_EVENT"),
        "1",
        StringComparison.Ordinal);
    // Escape hatch for the cached-texture copy skip (per-draw texel copies
    // are re-enabled unconditionally when set), for A/B-ing rendering issues.
    private static readonly bool _textureCopySkipDisabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_NO_TEXTURE_SKIP"),
        "1",
        StringComparison.Ordinal);
    // GPU deswizzle: ship raw tiled bytes + params to the backend instead of
    // detiling on the CPU. On by default; SHARPEMU_GPU_DETILE=0 forces the CPU
    // path. Backend-agnostic here (only inspects DetileParams); the Vulkan/Metal
    // backends detile on the GPU, others fall back to the CPU path.
    private static readonly bool _gpuDetileEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_DETILE"),
        "0",
        StringComparison.Ordinal);

    // Diagnostics (SHARPEMU_LOG_GPU_DETILE=1): one line per distinct texture tile
    // mode and per-gate decision, so we can see which swizzle modes/formats a
    // title uses and whether each takes the GPU or CPU path.
    private static readonly bool _gpuDetileLog = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_GPU_DETILE"),
        "1",
        StringComparison.Ordinal);
    private static readonly HashSet<uint> _seenTextureTileModes = new();
    private static readonly HashSet<uint> _gpuDetileGateDiag = new();
    private static readonly bool _reuseGuestTextureSnapshots = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_REUSE_GUEST_TEXTURE_SNAPSHOTS"),
        "0",
        StringComparison.Ordinal);
    private static int _guestTextureSnapshotReuseLogged;
    private static long _dcbWriteDataTraceCount;
    private static int _tracedVertexRangeCount;
    private static long _dcbWaitRegMemTraceCount;
    private static long _createShaderTraceCount;
    private static long _duplicateTargetTraceCount;
    private static long _cbMetadataSkipTraceCount;
    private static long _packetPayloadTraceCount;
    private static bool _tracedMissingPixelShaderBindings;
    private static long _unsatisfiedWaitTraceCount;
    private static long _labelProducerSequence;
    private static readonly object _labelProducerGate = new();
    private static readonly List<LabelProducerTrace> _labelProducers = [];
    private const int LabelProducerSoftBound = 4096;
    // Raised when a compaction pass frees nothing because every record is still
    // active, so registration does not rescan the whole list on every add while
    // a queue is suspended. Reset once compaction can make progress again.
    private static int _labelProducerCompactionBound = LabelProducerSoftBound;
    private static readonly HashSet<(object Memory, ulong Address)>
        _tracedProducerlessWaits = new();
    private static long _shaderTranslationMissTraceCount;
    private static long _translatedDrawTraceCount;
    private static long _standardDmaTraceCount;
    private static long _packetParseFailureTraceCount;
    private static int _textureFallbackTraceCount;
    private static readonly object _softwarePresenterGate = new();
    private static readonly Dictionary<(ulong Source, ulong Destination), ulong> _softwarePresenterFingerprints = new();
    private static readonly Dictionary<(ulong Shader, ulong Source, ulong Destination), ulong> _softwareComputeBlitFingerprints = new();
    private static readonly object _registerDefaultsGate = new();
    private static readonly ConditionalWeakTable<object, RegisterDefaultsAllocation> _registerDefaultsAllocations = new();
    private static readonly ConditionalWeakTable<object, SubmittedGpuState> _submittedGpuStates = new();

    // Unwraps decorator chains so all threads resolve to one shared root —
    // ctx.Memory identity otherwise differs per native worker thread.
    private static object CanonicalMemory(object memory)
    {
        while (memory is SharpEmu.HLE.ICpuMemoryWrapper wrapper)
        {
            memory = wrapper.Inner;
        }

        return memory;
    }

    private static readonly RegisterDefaultGroup[] PrimaryRegisterDefaults =
        CreatePrimaryRegisterDefaults();

    private static readonly RegisterDefaultGroup[] InternalRegisterDefaults =
    [
        new(0, 0, 0x8FB4EDB5, [new(0x00E, 0)]),
        new(0, 1, 0xB994AD29, [new(0x2AF, 0)]),
        new(0, 2, 0xD427322F, [new(0x314, 0)]),
        new(0, 3, 0xF58FEA31, [new(0x1B5, 0)]),
        new(1, 0, 0x6AC156EF, [new(0x216, 0)]),
        new(1, 1, 0x6AC15610, [new(0x217, 0)]),
        new(1, 2, 0x6AC15009, [new(0x219, 0)]),
        new(1, 3, 0x6AC153BA, [new(0x21A, 0)]),
        new(1, 4, 0xBE7DCD73, [new(0x27D, 0)]),
        new(1, 5, 0x0C4B1438, [new(0x22A, 0)]),
        new(1, 6, 0xDB00D71A, [new(0x204, 0)]),
        new(1, 7, 0xDB00D249, [new(0x205, 0)]),
        new(1, 8, 0xDB00EC60, [new(0x206, 0)]),
        new(1, 9, 0x0C4D6FE4, [new(0x080, 0)]),
        new(1, 10, 0x0C4A80EF, [new(0x100, 0)]),
        new(1, 11, 0x0DD283E7, [new(0x006, 0)]),
        new(1, 12, 0xC620E68C, [new(0x081, 0)]),
        new(1, 13, 0xC67EFACF, [new(0x101, 0)]),
        new(1, 14, 0xD9E6D9F7, [new(0x001, 0)]),
        new(2, 0, 0x31F34B9F, [new(0x24F, 0)]),
        new(2, 1, 0xAC0F9E76, [new(0x80003FFF, 0)]),
        new(2, 2, 0x929FD95D, [new(0x250, 0)]),
    ];

    private readonly record struct TextureDescriptor(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint TileMode,
        uint Type,
        uint BaseLevel,
        uint LastLevel,
        uint Pitch,
        uint DstSelect,
        uint Depth = 1,
        uint BaseArray = 0,
        uint ArrayPitch = 0,
        uint MaxMip = 0,
        uint MinLod = 0,
        uint MinLodWarn = 0,
        uint BcSwizzle = 0,
        ulong MetadataAddress = 0,
        uint DescriptorFlags = 0,
        bool HasExtendedDescriptor = false)
    {
        public uint ResourceMipLevels
        {
            get
            {
                // RDNA2 table 45 explicitly distinguishes MAX_MIP (the
                // resource allocation) from BASE_LEVEL/LAST_LEVEL (the
                // resource view). Do not size a Vulkan image from a view:
                // another descriptor for the same allocation may expose a
                // different subset of its mip chain.
                var maximumMipLevels = GetMaximumMipLevels();
                var resourceMipLevels = HasExtendedDescriptor
                    ? MaxMip + 1
                    : maximumMipLevels;
                return Math.Min(Math.Max(resourceMipLevels, 1u), maximumMipLevels);
            }
        }

        public uint MipLevels
        {
            get
            {
                var descriptorMipLevels = LastLevel >= ViewBaseLevel
                    ? LastLevel - ViewBaseLevel + 1
                    : 1;
                return Math.Min(
                    descriptorMipLevels,
                    ResourceMipLevels - ViewBaseLevel);
            }
        }

        public uint ViewBaseLevel
        {
            get
            {
                // Some single-mip Gen5 descriptors use the reserved/inverted
                // 15-0 range as a mip-disabled sentinel. The resource still
                // has exactly one addressable level (MAX_MIP=0). Treating 15
                // literally makes Vulkan reject an otherwise compatible GPU
                // image and falls back to stale guest-memory pixels. For any
                // malformed range, keep BASE_LEVEL's meaning and clamp it to
                // the allocation's last addressable mip. In particular, the
                // common 15-0/MAX_MIP=0 sentinel resolves to mip 0 without
                // making LAST_LEVEL the base of unrelated inverted views.
                return Math.Min(BaseLevel, ResourceMipLevels - 1);
            }
        }

        private uint GetMaximumMipLevels()
        {
            var largestDimension = Type == 10
                ? Math.Max(Math.Max(Width, Height), Depth)
                : Math.Max(Width, Height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return maximumMipLevels;
        }
    }

    private readonly record struct RenderTargetDescriptor(
        uint Slot,
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint NumberType,
        uint ComponentSwap,
        uint TileMode);

    private sealed record TranslatedGuestDraw(
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint PrimitiveType,
        IGuestCompiledShader VertexShader,
        IGuestCompiledShader PixelShader,
        uint AttributeCount,
        uint VertexCount,
        uint InstanceCount,
        int BaseVertex,
        int VertexBufferBaseVertex,
        GuestIndexBuffer? IndexBuffer,
        IReadOnlyList<TranslatedImageBinding> Textures,
        IReadOnlyList<Gen5GlobalMemoryBinding> GlobalMemoryBindings,
        IReadOnlyList<Gen5VertexInputBinding> VertexInputs,
        IReadOnlyList<RenderTargetDescriptor> RenderTargets,
        GuestDepthTarget? DepthTarget,
        // Seam-shaped color targets are built once with the cached translation.
        IReadOnlyList<GuestRenderTarget> GuestTargets,
        GuestRenderState RenderState,
        IReadOnlyList<uint> PixelUserData,
        uint RawBlendControl,
        uint RawColorInfo,
        IReadOnlyList<uint> PixelInitialScalars,
        IReadOnlyList<uint> VertexInitialScalars,
        bool IsFullscreenColorClear = false,
        float ClearRed = 0f,
        float ClearGreen = 0f,
        float ClearBlue = 0f,
        float ClearAlpha = 1f,
        bool IsDccFastClear = false);

    private sealed record TranslatedImageBinding(
        TextureDescriptor Descriptor,
        bool IsStorage,
        uint MipLevel,
        IReadOnlyList<uint> SamplerDescriptor,
        bool IsArrayed = false);

    private readonly record struct GuestTextureSnapshotReuseKey(
        TextureDescriptor Descriptor,
        bool IsStorage,
        uint MipLevel,
        bool IsArrayed);

    private readonly record struct RenderTargetWriter(
        ulong Sequence,
        ulong ExportShaderAddress,
        ulong PixelShaderAddress,
        uint VertexCount,
        uint PrimitiveType);

    private readonly record struct ComputeImageWriter(
        ulong Sequence,
        ulong ShaderAddress,
        string Opcode);

    private readonly record struct ComputeDispatch(
        uint GroupCountX,
        uint GroupCountY,
        uint GroupCountZ,
        uint BaseGroupX,
        uint BaseGroupY,
        uint BaseGroupZ,
        uint WaveLaneCount,
        bool IsIndirect,
        uint ThreadCountX,
        uint ThreadCountY,
        uint ThreadCountZ);

    private readonly record struct SubmittedAcquireMem(
        uint Engine,
        uint CbDbControl,
        ulong BaseAddress,
        ulong SizeBytes,
        uint PollInterval,
        uint GcrControl)
    {
        // GFX10 GCR_CNTL invalidation controls. The host has no separate GLI,
        // GLM, GLK, GLV, GL1 and GL2 caches; they all converge on the guest
        // memory snapshots used to build Vulkan resources.
        private const uint GliInvalidateMask = 0x3u;
        private const int Gl1RangeShift = 2;
        private const uint Gl1RangeMask = 0x3u;
        private const uint GlmInvalidate = 1u << 5;
        private const uint GlkInvalidate = 1u << 7;
        private const uint GlvInvalidate = 1u << 8;
        private const uint Gl1Invalidate = 1u << 9;
        private const uint Gl2Discard = 1u << 13;
        private const uint Gl2Invalidate = 1u << 14;
        private const int Gl2RangeShift = 11;
        private const uint Gl2RangeMask = 0x3u;

        public bool InvalidatesGuestResources =>
            (GcrControl & (GliInvalidateMask |
                           GlmInvalidate |
                           GlkInvalidate |
                           GlvInvalidate |
                           Gl1Invalidate |
                           Gl2Discard |
                           Gl2Invalidate)) != 0;

        // sceAgc encodes its all-memory sentinel with a zero COHER_SIZE. GFX10
        // can also request ALL independently in GLI_INV, GL1_RANGE or
        // GL2_RANGE; in the host's unified resource cache, any invalidated
        // domain with ALL scope expands the operation to all tracked images.
        public bool CoversAllGuestMemory =>
            SizeBytes == 0 ||
            (GcrControl & GliInvalidateMask) == 1u ||
            ((GcrControl & (GlmInvalidate |
                            GlkInvalidate |
                            GlvInvalidate |
                            Gl1Invalidate)) != 0 &&
             ((GcrControl >> Gl1RangeShift) & Gl1RangeMask) == 0) ||
            ((GcrControl & (Gl2Discard | Gl2Invalidate)) != 0 &&
             ((GcrControl >> Gl2RangeShift) & Gl2RangeMask) == 0);
    }

    // Keep submitted geometry stable after the guest reuses its memory.
    // Set either variable to 0 only for a comparison test.
    private static readonly bool _retainSubmittedIndexData = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_RETAIN_SUBMITTED_INDEX_DATA"),
        "0",
        StringComparison.Ordinal);
    private static readonly bool _retainSubmittedVertexData = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_RETAIN_SUBMITTED_VERTEX_DATA"),
        "0",
        StringComparison.Ordinal);

    private sealed record SubmittedIndexSnapshot(
        ulong SourceAddress,
        uint IndexCount,
        int IndexStride,
        byte[] Data);

    private sealed record SubmittedVertexSnapshot(
        ulong ExportShaderAddress,
        IReadOnlyList<Gen5VertexInputBinding> Bindings);

    private sealed class SubmittedDcbState
    {
        public readonly record struct PendingSubmission(
            ulong CommandAddress,
            uint DwordCount,
            ulong SubmissionId,
            bool TracePackets,
            Dictionary<ulong, SubmittedIndexSnapshot>? IndexSnapshots,
            Dictionary<ulong, SubmittedVertexSnapshot>? VertexSnapshots);

        public Dictionary<uint, uint> CxRegisters { get; } = new();
        public Dictionary<uint, uint> ShRegisters { get; } = new();
        public Dictionary<uint, uint> UcRegisters { get; } = new();
        public TextureDescriptor? PresenterTexture { get; set; }
        public GuestDrawKind GuestDrawKind { get; set; }
        public TranslatedGuestDraw? TranslatedDraw { get; set; }
        public TranslatedGuestDraw? PendingTargetlessDraw { get; set; }
        public Dictionary<ulong, RenderTargetDescriptor> KnownRenderTargets { get; } = new();
        public Dictionary<ulong, RenderTargetWriter> RenderTargetWriters { get; } = new();
        public ulong IndirectArgsAddress { get; set; }
        public bool SawIndexedDraw { get; set; }
        public ulong IndexBufferAddress { get; set; }
        public uint IndexBufferCount { get; set; }
        public uint IndexSize { get; set; }
        public uint InstanceCount { get; set; } = 1;
        public uint DrawIndexOffset { get; set; }
        public bool PredicateSkip { get; set; }
        public string QueueName { get; set; } = "graphics";
        // Ident this queue's end-of-pipe completion interrupt is published under.
        // The graphics queue keeps 0; a compute queue takes the owner handle it
        // was submitted with, which is the same value the guest registers through
        // sceAgcDriverAddEqEvent.
        public ulong CompletionEventId { get; set; }
        public ulong ActiveSubmissionId { get; set; }
        public Dictionary<ulong, SubmittedIndexSnapshot>? ActiveIndexSnapshots { get; set; }
        public SubmittedIndexSnapshot? CurrentIndexSnapshot { get; set; }
        public Dictionary<ulong, SubmittedVertexSnapshot>? ActiveVertexSnapshots { get; set; }
        public SubmittedVertexSnapshot? CurrentVertexSnapshot { get; set; }
        public Queue<PendingSubmission> PendingSubmissions { get; } = new();
        public bool HasActiveSubmission { get; set; }
        public bool IsSuspended { get; set; }
        public bool IsFaulted { get; set; }
        public string? FaultReason { get; set; }
        public bool FaultedSubmissionReported { get; set; }

        // Set when parsing stops on an INDIRECT_BUFFER packet so the caller can
        // continue into the buffer it links to.
        public ulong PendingChainAddress { get; set; }
        public uint PendingChainDwords { get; set; }

        // Base of the ring chunk currently being parsed; advances by RingChunkBytes.
        public ulong RingChunkBase { get; set; }
        public bool FollowedChunkAdvance { get; set; }
        public ulong CompletionEventNotifiedSubmissionId { get; set; }
        public Dictionary<(uint Op, uint Register), uint> FramePacketCounts { get; } = new();
        public uint FramePacketCount { get; set; }
        public uint FrameDrawCount { get; set; }
        public uint FrameDispatchCount { get; set; }
        public ulong FlipCount { get; set; }
        // Coalesce ACQUIRE_MEM invalidations within one DCB parse so North
        // Yankton load does not enqueue hundreds of empty OrderedGuestActions.
        public bool PendingAcquireInvalidation { get; set; }
        public ulong PendingAcquireBase { get; set; }
        public ulong PendingAcquireSize { get; set; }

        // Growing ring: never follows the chunk-advance sentinel (builders jump
        // to non-contiguous chunks), parks on the first not-yet-written word instead.
        public bool IsForceSubmittedRing { get; set; }

        // Address of a synthetic ring-tail park (SuspendOnUnwrittenRingWord);
        // abandoned if a new submission means the game moved to a fresh ring.
        public ulong RingTailParkAddress { get; set; }

        // One-past the last fully-processed packet, so the orphan sweep can
        // reach packets a suspended queue never got back to.
        public ulong LastParsedAddress { get; set; }
    }

    private sealed class SubmittedGpuState
    {
        public object Gate { get; } = new();
        public SubmittedDcbState Graphics { get; } = new();
        public Dictionary<uint, SubmittedDcbState> ComputeQueues { get; } = new();
        public Dictionary<ulong, ComputeImageWriter> ComputeImageWriters { get; } = new();
        public AgcHtileMetadataTracker HtileMetadata { get; } = new();
        public Dictionary<uint, string> ResourceOwners { get; } = new();
        public Dictionary<uint, RegisteredAgcResource> RegisteredResources { get; } = new();
        public bool ResourceRegistrationInitialized { get; set; }
        public ulong ResourceRegistrationMemory { get; set; }
        public ulong ResourceRegistrationMemorySize { get; set; }
        public uint ResourceRegistrationMaxOwners { get; set; }
        public uint DefaultOwner { get; set; } = DefaultAgcOwner;
        public uint NextOwner { get; set; } = 1;
        public uint NextResource { get; set; } = 1;
        public ulong WorkSequence { get; set; }
        public ulong SubmissionSequence { get; set; }
        public bool WaitMonitorRunning { get; set; }
        public object WaitMonitorSignalGate { get; } = new();
        public long WaitMonitorSignalVersion { get; set; }

        // Coalesced drain scheduling; fields (not properties) so Interlocked can target them.
        public int DrainWorkerActive;
        public int DrainPending;
        public CpuContext? PendingDrainContext;
    }

    private readonly record struct RegisteredAgcResource(
        uint Owner,
        ulong Address,
        ulong Size,
        string Name,
        uint Type,
        uint Flags);

    private sealed class LabelProducerTrace
    {
        public long Sequence;
        public required object Memory;
        public ulong Address;
        public ulong Length;
        public ulong PacketAddress;
        public ulong SubmissionId;
        public required string QueueName;
        public required string DebugName;
        public bool Completed;
    }

    private readonly record struct RegisterDefaultValue(uint Offset, uint Value);

    private readonly record struct RegisterDefaultGroup(
        uint Space,
        uint Index,
        uint Type,
        RegisterDefaultValue[] Registers);

    private sealed record RegisterDefaultsAllocation(ulong Primary, ulong Internal);

    // NID captured from shipped titles; 'sceAgcInit' is a working label that collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "23LRUSvYu1M",
        ExportName = "sceAgcInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int Init(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        var version = (uint)ctx[CpuRegister.Rsi];
        if (stateAddress == 0 || !IsSupportedRegisterDefaultsVersion(version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TraceAgc($"agc.init state=0x{stateAddress:X16} version={version}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "2JtWUUiYBXs",
        ExportName = "sceAgcGetRegisterDefaults2",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: false);

    [SysAbiExport(
        Nid = "wRbq6ZjNop4",
        ExportName = "sceAgcGetRegisterDefaults2Internal",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetRegisterDefaults2Internal(CpuContext ctx) =>
        ReturnRegisterDefaults(ctx, internalDefaults: true);

    /// <summary>
    /// Reports that the GPU is not running in Trinity mode, matching the base
    /// console this backend emulates.
    /// </summary>
    [SysAbiExport(
        Nid = "BfBDZGbti7A",
        ExportName = "sceAgcGetIsTrinityMode",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetIsTrinityMode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "f3dg2CSgRKY",
        ExportName = "sceAgcCreateShader",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateShader(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var headerAddress = ctx[CpuRegister.Rsi];
        var codeAddress = ctx[CpuRegister.Rdx];
        if (headerAddress == 0 || codeAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, headerAddress, out var fileHeader) ||
            !TryReadUInt32(ctx, headerAddress + sizeof(uint), out var version))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (fileHeader != ShaderFileHeader || version != ShaderVersion)
        {
            TraceCreateShader(destinationAddress, headerAddress, codeAddress, $"invalid-header file=0x{fileHeader:X8} version=0x{version:X8}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!RelocatePointerField(ctx, headerAddress + ShaderCxRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderShRegistersOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderUserDataOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderSpecialsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderInputSemanticsOffset) ||
            !RelocatePointerField(ctx, headerAddress + ShaderOutputSemanticsOffset) ||
            !ctx.TryWriteUInt64(headerAddress + ShaderCodeOffset, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReadUInt64(ctx, headerAddress + ShaderUserDataOffset, out var userDataAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (userDataAddress != 0 &&
            (!RelocatePointerField(ctx, userDataAddress) ||
             !RelocatePointerField(ctx, userDataAddress + 0x08) ||
             !RelocatePointerField(ctx, userDataAddress + 0x10) ||
             !RelocatePointerField(ctx, userDataAddress + 0x18) ||
             !RelocatePointerField(ctx, userDataAddress + 0x20)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!PatchShaderProgramRegisters(ctx, headerAddress, codeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (destinationAddress != 0 &&
            !ctx.TryWriteUInt64(destinationAddress, headerAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (_submitTraceGate)
        {
            _shaderHeadersByCode[codeAddress] = headerAddress;
        }

        TryRegisterEmbeddedFusedProgram(ctx, codeAddress, headerAddress);

        TraceCreateShader(destinationAddress, headerAddress, codeAddress, "ok");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Registers a pre-combined shader whose continuation descriptor is in the
    /// same AGC upload. Some titles create this object without a fuse API call.
    /// </summary>
    internal static bool TryRegisterEmbeddedFusedProgram(
        CpuContext ctx,
        ulong entryCodeAddress,
        ulong entryHeaderAddress)
    {
        if (!TryReadByte(ctx, entryHeaderAddress + ShaderTypeOffset, out var entryType) ||
            entryType is not (GsFrontShaderType or HsFrontShaderType))
        {
            return false;
        }

        var attempts = _embeddedFusedScanAttempts.GetValue(
            ctx.Memory,
            static _ => new ConcurrentDictionary<(ulong Code, ulong Header), byte>());
        if (!attempts.TryAdd((entryCodeAddress, entryHeaderAddress), 0))
        {
            return false;
        }

        if (!TryReadUInt32(ctx, entryHeaderAddress + ShaderSizeOffset, out var entrySize) ||
            !IsValidDeclaredShaderSize(entrySize))
        {
            return false;
        }

        var upload = new byte[MaximumEmbeddedFusedScanBytes];
        var bytesRead = 0;
        const int readChunkBytes = 4 * 1024;
        while (bytesRead < upload.Length)
        {
            var chunkLength = Math.Min(readChunkBytes, upload.Length - bytesRead);
            if (!ctx.Memory.TryRead(
                    entryCodeAddress + (ulong)bytesRead,
                    upload.AsSpan(bytesRead, chunkLength)))
            {
                break;
            }

            bytesRead += chunkLength;
        }

        if (bytesRead < ShaderStructBytes)
        {
            return false;
        }

        var requiredContinuationType = entryType == GsFrontShaderType
            ? GsBackShaderType
            : HsBackShaderType;
        var waveSizeBit = entryType == GsFrontShaderType
            ? VgtShaderStagesGsW32EnBit
            : VgtShaderStagesHsW32EnBit;
        TryReadUInt64(
            ctx,
            entryHeaderAddress + ShaderSpecialsOffset,
            out var entrySpecialsAddress);

        ulong bestCodeAddress = 0;
        ulong bestHeaderAddress = 0;
        var bestDistance = ulong.MaxValue;
        for (var offset = 0;
             offset <= bytesRead - ShaderStructBytes;
             offset += sizeof(uint))
        {
            var descriptor = upload.AsSpan(offset, ShaderStructBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != ShaderFileHeader ||
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor[sizeof(uint)..]) != ShaderVersion ||
                descriptor[(int)ShaderTypeOffset] != requiredContinuationType)
            {
                continue;
            }

            var continuationCodeAddress = BinaryPrimitives.ReadUInt64LittleEndian(
                descriptor[(int)ShaderCodeOffset..]);
            var continuationSize = BinaryPrimitives.ReadUInt32LittleEndian(
                descriptor[(int)ShaderSizeOffset..]);
            if (continuationCodeAddress <= entryCodeAddress ||
                continuationCodeAddress - entryCodeAddress > uint.MaxValue ||
                !IsValidDeclaredShaderSize(continuationSize) ||
                !CanReadShaderRange(ctx, continuationCodeAddress, continuationSize))
            {
                continue;
            }

            var continuationSpecialsAddress = BinaryPrimitives.ReadUInt64LittleEndian(
                descriptor[(int)ShaderSpecialsOffset..]);
            if (entrySpecialsAddress != 0 && continuationSpecialsAddress != 0)
            {
                if (!TryReadUInt32(
                        ctx,
                        entrySpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint),
                        out var entryStages) ||
                    !TryReadUInt32(
                        ctx,
                        continuationSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint),
                        out var continuationStages) ||
                    ((entryStages ^ continuationStages) & waveSizeBit) != 0)
                {
                    continue;
                }
            }

            var distance = continuationCodeAddress - entryCodeAddress;
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            bestCodeAddress = continuationCodeAddress;
            bestHeaderAddress = entryCodeAddress + (ulong)offset;
        }

        if (bestHeaderAddress == 0)
        {
            return false;
        }

        Gen5ShaderTranslator.RegisterFusedProgram(
            ctx,
            entryCodeAddress,
            entryHeaderAddress,
            bestCodeAddress,
            bestHeaderAddress);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.fused_shader_discovered " +
            $"entry=0x{entryCodeAddress:X16} type={entryType} size=0x{entrySize:X} " +
            $"continuation=0x{bestCodeAddress:X16} type={requiredContinuationType} " +
            $"header=0x{bestHeaderAddress:X16}");
        return true;
    }

    private static bool IsValidDeclaredShaderSize(uint size) =>
        size != 0 &&
        (size & (sizeof(uint) - 1)) == 0 &&
        size <= MaximumDeclaredShaderSizeBytes;

    private static bool CanReadShaderRange(CpuContext ctx, ulong address, uint size)
    {
        Span<byte> word = stackalloc byte[sizeof(uint)];
        return ctx.Memory.TryRead(address, word) &&
               ctx.Memory.TryRead(address + size - sizeof(uint), word);
    }

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "dolOmWH+huQ",
        ExportName = "sceAgcGetFusedShaderSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetFusedShaderSize(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var frontAddress = ctx[CpuRegister.Rsi];
        var backAddress = ctx[CpuRegister.Rdx];
        if (destinationAddress == 0 || frontAddress == 0 || backAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, frontAddress + ShaderTypeOffset, out var frontType) ||
            !TryReadByte(ctx, backAddress + ShaderTypeOffset, out var backType) ||
            !TryReadByte(ctx, backAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!IsFusedShaderHalfPair(frontType, backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(destinationAddress, registerCount * 8UL) ||
            !ctx.TryWriteUInt64(destinationAddress + 8, FusedShaderImageAlignment))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.get_fused_shader_size front=0x{frontAddress:X16} back=0x{backAddress:X16} " +
            $"types={frontType}/{backType} registers={registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "fd5Bp5tGTgo",
        ExportName = "sceAgcFuseShaderHalves",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int FuseShaderHalves(CpuContext ctx)
    {
        var fusedAddress = ctx[CpuRegister.Rdi];
        var frontAddress = ctx[CpuRegister.Rsi];
        var backAddress = ctx[CpuRegister.Rdx];
        var scratchAddress = ctx[CpuRegister.Rcx];
        if (fusedAddress == 0 || frontAddress == 0 || backAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, frontAddress + ShaderTypeOffset, out var frontType) ||
            !TryReadByte(ctx, backAddress + ShaderTypeOffset, out var backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!IsFusedShaderHalfPair(frontType, backType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt64(ctx, frontAddress + ShaderSpecialsOffset, out var frontSpecialsAddress) ||
            !TryReadUInt64(ctx, backAddress + ShaderSpecialsOffset, out var backSpecialsAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var isGeometryPair = frontType == GsFrontShaderType;
        if (frontSpecialsAddress != 0 && backSpecialsAddress != 0)
        {
            if (!TryReadUInt32(ctx, frontSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint), out var frontStages) ||
                !TryReadUInt32(ctx, backSpecialsAddress + ShaderSpecialVgtShaderStagesEnOffset + sizeof(uint), out var backStages))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var waveSizeBit = isGeometryPair ? VgtShaderStagesGsW32EnBit : VgtShaderStagesHsW32EnBit;
            if (((frontStages ^ backStages) & waveSizeBit) != 0)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        if (!TryReadUInt64(ctx, backAddress + ShaderShRegistersOffset, out var backRegistersAddress) ||
            !TryReadByte(ctx, backAddress + ShaderNumShRegistersOffset, out var registerCount) ||
            !TryReadUInt64(ctx, frontAddress + ShaderCodeOffset, out var frontCodeAddress) ||
            !TryReadUInt64(ctx, backAddress + ShaderCodeOffset, out var backCodeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> header = stackalloc byte[ShaderStructBytes];
        if (!ctx.Memory.TryRead(backAddress, header) ||
            !ctx.Memory.TryWrite(fusedAddress, header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var fusedRegistersAddress = backRegistersAddress;
        if (scratchAddress != 0 && backRegistersAddress != 0 && registerCount != 0)
        {
            Span<byte> registers = stackalloc byte[registerCount * 8];
            if (!ctx.Memory.TryRead(backRegistersAddress, registers) ||
                !ctx.Memory.TryWrite(scratchAddress, registers))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            fusedRegistersAddress = scratchAddress;
        }

        if (!TryWriteByte(ctx, fusedAddress + ShaderTypeOffset, isGeometryPair ? GsShaderType : HsShaderType) ||
            !ctx.TryWriteUInt64(fusedAddress + ShaderUserDataOffset, 0) ||
            !ctx.TryWriteUInt64(fusedAddress + ShaderShRegistersOffset, fusedRegistersAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (isGeometryPair)
        {
            if (!TryReadUInt64(ctx, frontAddress + ShaderShRegistersOffset, out var frontRegistersAddress) ||
                !TryReadByte(ctx, frontAddress + ShaderNumShRegistersOffset, out var frontRegisterCount))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            for (var occurrence = 0; occurrence < 2; occurrence++)
            {
                if (!TryFindShaderRegister(ctx, fusedRegistersAddress, registerCount, SpiShaderPgmChksumGs, occurrence, out var fusedEntry) ||
                    !TryFindShaderRegister(ctx, frontRegistersAddress, frontRegisterCount, SpiShaderPgmChksumGs, occurrence, out var frontEntry))
                {
                    continue;
                }

                if (!TryReadUInt32(ctx, frontEntry + sizeof(uint), out var checksum) ||
                    !TryWriteUInt32(ctx, fusedEntry + sizeof(uint), checksum))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }

        if (!PatchFusedProgramAddress(
                ctx,
                fusedRegistersAddress,
                registerCount,
                isGeometryPair ? SpiShaderPgmLoEs : SpiShaderPgmLoLs,
                frontCodeAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Gen5ShaderTranslator.RegisterFusedProgram(
            ctx,
            frontCodeAddress,
            frontAddress,
            backCodeAddress,
            backAddress);

        TraceAgc(
            $"agc.fuse_shader_halves fused=0x{fusedAddress:X16} front=0x{frontAddress:X16} " +
            $"back=0x{backAddress:X16} scratch=0x{scratchAddress:X16} types={frontType}/{backType} " +
            $"registers={registerCount} entry=0x{frontCodeAddress:X16} " +
            $"continuation=0x{backCodeAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "vcmNN+AAXnY",
        ExportName = "sceAgcSetCxRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "cx");

    [SysAbiExport(
        Nid = "whb1RL7K4Ss",
        ExportName = "sceAgcSetCxRegIndirectPatchSetNumRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetNumRegisters(CpuContext ctx) =>
        SetIndirectPatchRegisterCount(ctx, "cx");

    [SysAbiExport(
        Nid = "Qrj4c+61z4A",
        ExportName = "sceAgcSetShRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "sh");

    [SysAbiExport(
        Nid = "6lNcCp+fxi4",
        ExportName = "sceAgcSetUcRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "uc");

    [SysAbiExport(
        Nid = "d-6uF9sZDIU",
        ExportName = "sceAgcSetCxRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "cx");

    [SysAbiExport(
        Nid = "z2duB-hHQSM",
        ExportName = "sceAgcSetShRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "sh");

    [SysAbiExport(
        Nid = "vRoArM9zaIk",
        ExportName = "sceAgcSetUcRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "uc");

    [SysAbiExport(
        Nid = "D9sr1xGUriE",
        ExportName = "sceAgcCreatePrimState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreatePrimState(CpuContext ctx)
    {
        var cxRegistersAddress = ctx[CpuRegister.Rdi];
        var ucRegistersAddress = ctx[CpuRegister.Rsi];
        var hullShaderAddress = ctx[CpuRegister.Rdx];
        var geometryShaderAddress = ctx[CpuRegister.Rcx];
        var primitiveType = (uint)ctx[CpuRegister.R8];

        // Hull is optional: tessellation pipelines (GTA fused HS, Ghost of Yōtei)
        // pass a non-null hull-state block here. Geometry-derived CX/UC writes
        // stay the same; the hull stage itself is not modelled yet, so it is
        // only recorded in the trace (#583).
        if (cxRegistersAddress == 0 || ucRegistersAddress == 0 || geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadByte(ctx, geometryShaderAddress + ShaderTypeOffset, out var shaderType) || !IsEsGeometryShaderType(shaderType) ||
            !TryReadUInt64(ctx, geometryShaderAddress + ShaderSpecialsOffset, out var specialsAddress) ||
            specialsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtShaderStagesEnOffset, cxRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialVgtGsOutPrimTypeOffset, cxRegistersAddress + 8) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeCntlOffset, ucRegistersAddress) ||
            !CopyShaderRegister(ctx, specialsAddress + ShaderSpecialGeUserVgprEnOffset, ucRegistersAddress + 8) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 16, VgtPrimitiveType) ||
            !TryWriteUInt32(ctx, ucRegistersAddress + 20, primitiveType))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.create_prim_state cx=0x{cxRegistersAddress:X16} uc=0x{ucRegistersAddress:X16} " +
            $"hull=0x{hullShaderAddress:X16} gs=0x{geometryShaderAddress:X16} type={shaderType} prim=0x{primitiveType:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Symbol name unconfirmed (not in ps5_names.txt); resolved from the
    // decrypted eboot's call site only. On Ghost of Yotei, the caller scans
    // this same buffer right after sceAgcCreatePrimState for 32 (offset,value)
    // pairs (a hardcoded size, not read from any header) and open-address-
    // probes them as a register hash table -- an out-of-bounds probe index
    // sourced from an unwritten pair was the AV. CreatePrimState only
    // populates the first 3 pairs; zero the rest of the scanned window so
    // every unpopulated slot is a harmless failed probe instead of
    // guest-stack garbage.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "dbOlWdppb4o",
        ExportName = "sceAgcAddPrimStateRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AddPrimStateRegisters(CpuContext ctx)
    {
        var ucRegistersAddress = ctx[CpuRegister.Rdi];
        if (ucRegistersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        const int prefilledPairBytes = 3 * 8; // sceAgcCreatePrimState's 3 (offset,value) pairs
        const int scannedTableBytes = 0x20 * 8; // caller's hardcoded probe-window size
        Span<byte> zero = stackalloc byte[scannedTableBytes - prefilledPairBytes];
        zero.Clear();
        if (!ctx.Memory.TryWrite(ucRegistersAddress + prefilledPairBytes, zero))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.add_prim_state_registers uc=0x{ucRegistersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "HV4j+E0MBHE",
        ExportName = "sceAgcCreateInterpolantMapping",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CreateInterpolantMapping(CpuContext ctx)
    {
        var registersAddress = ctx[CpuRegister.Rdi];
        var geometryShaderAddress = ctx[CpuRegister.Rsi];
        var pixelShaderAddress = ctx[CpuRegister.Rdx];

        if (registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // SPI_PS_INPUT_CNTL maps each PS VINTRP ATTR slot to a VS/GS param export.
        // Walk PS input semantics, find the GS output with the same semantic id,
        // and pack the hardware CNTL word (location in bits [4:0], Flat at 0x400).
        uint inputSemanticsCount = 0;
        ulong inputSemanticsAddress = 0;
        if (pixelShaderAddress != 0)
        {
            if (!TryReadUInt64(ctx, pixelShaderAddress + ShaderInputSemanticsOffset, out inputSemanticsAddress) ||
                !TryReadUInt32(ctx, pixelShaderAddress + ShaderNumInputSemanticsOffset, out inputSemanticsCount))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (inputSemanticsCount == 0 || inputSemanticsAddress == 0)
        {
            if (!TryWriteIdentityInterpolantRegisters(ctx, registersAddress, 0))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceAgc(
                $"agc.create_interpolant_mapping regs=0x{registersAddress:X16} " +
                $"gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16} inputs=0");
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (geometryShaderAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // NumOutputSemantics is a u16 at header +0x56.
        if (!TryReadUInt64(ctx, geometryShaderAddress + ShaderOutputSemanticsOffset, out var outputSemanticsAddress) ||
            !TryReadUInt16(ctx, geometryShaderAddress + ShaderNumOutputSemanticsOffset, out var outputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        inputSemanticsCount = Math.Min(inputSemanticsCount, 32u);
        for (uint psIndex = 0; psIndex < inputSemanticsCount; psIndex++)
        {
            if (!TryReadUInt32(
                    ctx,
                    inputSemanticsAddress + (psIndex * sizeof(uint)),
                    out var psWord))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var psSemantic = psWord & 0xFFu;
            uint? gsWord = null;
            if (outputSemanticsAddress != 0)
            {
                for (uint gsIndex = 0; gsIndex < outputSemanticsCount; gsIndex++)
                {
                    if (!TryReadUInt32(
                            ctx,
                            outputSemanticsAddress + (gsIndex * sizeof(uint)),
                            out var candidate))
                    {
                        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    if ((candidate & 0xFFu) == psSemantic)
                    {
                        gsWord = candidate;
                        break;
                    }
                }
            }

            var value = (psWord & 0x0030_0000u) != 0
                ? CreateInterpolantF16Value(psWord, gsWord)
                : CreateInterpolantNonF16Value(psWord, gsWord.HasValue);
            value = gsWord is { } matched
                ? CreateInterpolantMappingValue(value, psWord, matched)
                : CreateInterpolantDefaultParamValue(value, psWord);

            if (!TryWriteInterpolantRegister(ctx, registersAddress, psIndex, value))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (!TryWriteIdentityInterpolantRegisters(ctx, registersAddress, inputSemanticsCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.create_interpolant_mapping regs=0x{registersAddress:X16} " +
            $"gs=0x{geometryShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
            $"inputs={inputSemanticsCount} outputs={outputSemanticsCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    private static uint ApplyInterpolantDefaultValue(uint value, uint psWord)
    {
        value &= ~0x0000_0300u;
        value |= ((psWord >> 28) & 0x3u) << 8;
        return value;
    }

    private static uint ApplyInterpolantDefaultValueHi(uint value, uint psWord)
    {
        value &= ~0x0060_0000u;
        value |= ((psWord >> 30) & 0x3u) << 21;
        return value;
    }

    private static uint CreateInterpolantMappingValue(uint value, uint psWord, uint gsWord)
    {
        var flatShade =
            (psWord & 0x0040_0000u) != 0 || (psWord & 0x0100_0000u) != 0
                ? 0x0000_0400u
                : 0u;
        value &= ~0x0000_001Fu;
        value |= (gsWord >> 8) & 0x1Fu;
        value &= ~0x0000_0400u;
        value |= flatShade;
        return ApplyInterpolantDefaultValue(value, psWord);
    }

    private static uint CreateInterpolantDefaultParamValue(uint value, uint psWord)
    {
        value &= ~0x0000_001Fu;
        value &= ~0x0000_0400u;
        return ApplyInterpolantDefaultValue(value, psWord);
    }

    private static uint CreateInterpolantF16Value(uint psWord, uint? gsWord)
    {
        var value = (psWord << 4) & 0x0300_0000u;
        if (gsWord is null)
        {
            value |= 0x0018_0020u;
        }
        else
        {
            var commonWord = psWord & gsWord.Value;
            value &= 0xFFF7_FFDFu;
            value |= (commonWord >> 15) & 0x20u;
            value ^= 0x0008_0020u;
            value &= ~0x0010_0000u;
            value |= (~commonWord >> 1) & 0x0010_0000u;
        }

        return ApplyInterpolantDefaultValueHi(value, psWord);
    }

    private static uint CreateInterpolantNonF16Value(uint psWord, bool hasGsSemantic)
    {
        uint value = 0;
        if ((psWord & 0x0100_0000u) != 0 || !hasGsSemantic)
        {
            value |= 0x20u;
        }

        return value;
    }

    private static bool TryWriteInterpolantRegister(
        CpuContext ctx,
        ulong registersAddress,
        uint index,
        uint value)
    {
        var destination = registersAddress + (index * 8);
        return TryWriteUInt32(ctx, destination, SpiPsInputCntl0 + index) &&
               TryWriteUInt32(ctx, destination + sizeof(uint), value);
    }

    private static bool TryWriteIdentityInterpolantRegisters(
        CpuContext ctx,
        ulong registersAddress,
        uint firstIndex)
    {
        for (uint i = firstIndex; i < 32u; i++)
        {
            if (!TryWriteInterpolantRegister(ctx, registersAddress, i, i))
            {
                return false;
            }
        }

        return true;
    }

    private static uint[] ReadPsInputCntlRegisters(IReadOnlyDictionary<uint, uint> cxRegisters)
    {
        var cntl = new uint[32];
        for (uint i = 0; i < 32u; i++)
        {
            // Unprogrammed slots default to identity (ATTR i → param i).
            cntl[i] = cxRegisters.TryGetValue(SpiPsInputCntl0 + i, out var value)
                ? value
                : i;
        }

        return cntl;
    }

    private static ulong ComputePsInputCntlFingerprint(ReadOnlySpan<uint> cntl)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        foreach (var value in cntl)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "V++UgBtQhn0",
        ExportName = "sceAgcGetDataPacketPayloadAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetDataPacketPayloadAddress(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var commandAddress = ctx[CpuRegister.Rsi];
        var type = (int)ctx[CpuRegister.Rdx];
        if (outputAddress == 0 || commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var payloadAddress = commandAddress + 8;
        if (type == 0)
        {
            if (!TryReadUInt32(ctx, commandAddress, out var header))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            payloadAddress = (header & 0x3FFF_0000u) == 0x3FFF_0000u
                ? 0
                : commandAddress + 4;
        }

        if (!ctx.TryWriteUInt64(outputAddress, payloadAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (ShouldTraceHotPath(ref _packetPayloadTraceCount))
        {
            TraceAgc(
                $"agc.get_packet_payload out=0x{outputAddress:X16} cmd=0x{commandAddress:X16} " +
                $"type={type} payload=0x{payloadAddress:X16}");
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "LtTouSCZjHM",
        ExportName = "sceAgcCbNop",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbNop(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dwordCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 || dwordCount < 2 || dwordCount > 0x4001)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, dwordCount, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(dwordCount, ItNop, RZero)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 1; index < dwordCount; index++)
        {
            if (!TryWriteUInt32(ctx, commandAddress + ((ulong)index * sizeof(uint)), 0))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    // RenderThread/Subrender probe this before writing a NOP. Unresolved
    // GetSize returns NOT_FOUND and leaves command-buffer sizing broken.
    // CbNop rejects dwordCount < 2, so report that floor.
    [SysAbiExport(
        Nid = "t7PlZ9nt5Lc",
        ExportName = "sceAgcCbNopGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbNopGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "k3GhuSNmBLU",
        ExportName = "sceAgcCbDispatch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbDispatch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var groupCountX = (uint)ctx[CpuRegister.Rsi];
        var groupCountY = (uint)ctx[CpuRegister.Rdx];
        var groupCountZ = (uint)ctx[CpuRegister.Rcx];
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDispatchDirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, groupCountX) ||
            !TryWriteUInt32(ctx, commandAddress + 8, groupCountY) ||
            !TryWriteUInt32(ctx, commandAddress + 12, groupCountZ) ||
            !TryWriteUInt32(ctx, commandAddress + 16, DirectDispatchInitiator(modifier)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    private static uint DirectDispatchInitiator(uint modifier) =>
        // AGC's direct API takes workgroup counts by default. Preserve the
        // caller's USE_THREAD_DIMENSIONS bit when explicitly requested; do not
        // force it. Demon's Souls' 0xF00100 dispatch is paired with a
        // 0x3C004000 element bound (exactly 64 lanes per group), proving the
        // default packet is group-dimensional.
        (modifier & 0xA038u) | 0x41u;

    [SysAbiExport(
        Nid = "UZbQjYAwwXM",
        ExportName = "sceAgcCbSetShRegistersDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegistersDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (registerCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || registersAddress == 0 || registerCount > 4096)
        {
            return ReturnPointer(ctx, 0);
        }

        var registers = new RegisterDefaultValue[registerCount];
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var offset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return ReturnPointer(ctx, 0);
            }

            registers[index] = new RegisterDefaultValue(offset, value);
        }

        Array.Sort(registers, static (left, right) => left.Offset.CompareTo(right.Offset));
        ulong firstCommandAddress = 0;
        var startIndex = 0;
        while (startIndex < registers.Length)
        {
            var endIndex = startIndex + 1;
            while (endIndex < registers.Length &&
                   registers[endIndex].Offset == registers[endIndex - 1].Offset + 1)
            {
                endIndex++;
            }

            var valueCount = (uint)(endIndex - startIndex);
            var packetDwords = valueCount + 2;
            if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
                !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
                !TryWriteUInt32(ctx, commandAddress + 4, registers[startIndex].Offset & 0xFFFFu))
            {
                return ReturnPointer(ctx, 0);
            }

            firstCommandAddress = firstCommandAddress == 0 ? commandAddress : firstCommandAddress;
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!TryWriteUInt32(
                        ctx,
                        commandAddress + 8 + ((ulong)(index - startIndex) * sizeof(uint)),
                        registers[index].Value))
                {
                    return ReturnPointer(ctx, 0);
                }
            }

            startIndex = endIndex;
        }

        return ReturnPointer(ctx, firstCommandAddress);
    }

    [SysAbiExport(
        Nid = "JrtiDtKeS38",
        ExportName = "sceAgcAcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RAcbReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
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
        Nid = "KT-hTp-Ch14",
        ExportName = "sceAgcAcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var gcrControl = (uint)ctx[CpuRegister.Rsi];
        var baseAddress = ctx[CpuRegister.Rdx];
        var sizeBytes = ctx[CpuRegister.Rcx];
        var pollCycles = (uint)ctx[CpuRegister.R8];
        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0x8000_0000u) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    // Matches the fixed 8-dword ACQUIRE_MEM packet AcbAcquireMem writes above.
    [SysAbiExport(
        Nid = "ewobAQeMo5k",
        ExportName = "sceAgcAcbAcquireMemGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbAcquireMemGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "htn36gPnBk4",
        ExportName = "sceAgcAcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var address = ctx[CpuRegister.R8];
        var reference = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var pollCycles) ||
            commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 7u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)address & (size == 0 ? ~0x3u : ~0x7u)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32) & 0x3FFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }

        if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, EncodeWaitRegMem32Control(compareFunction, 0, cachePolicy)) ||
                !TryWriteUInt32(ctx, commandAddress + 24, EncodeWaitRegMemPoll(pollCycles)))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, EncodeWaitRegMem64Control(compareFunction, 0, cachePolicy)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, EncodeWaitRegMemPoll(pollCycles)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "eZ4+17OQz4Q",
        ExportName = "sceAgcAcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWriteData(CpuContext ctx) =>
        DcbWriteData(ctx);

    [SysAbiExport(
        Nid = "j3EtxFkSIhQ",
        ExportName = "sceAgcAcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var argumentsAddress = ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)argumentsAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(argumentsAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "n2fD4A+pb+g",
        ExportName = "sceAgcCbSetShRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 ||
            offset == 0 ||
            offset > 0x3FF ||
            !TryGetCbSetShRegisterRangeDirectLayout(valueCount, out var packetDwords, out _))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var markerAddress) ||
            !TryWriteUInt32(ctx, markerAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, markerAddress + 4, CbSetShRegisterRangeMarker) ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc($"agc.cb_set_sh_range buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset=0x{offset:X8} count={valueCount}");
        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "bxGoVxpdSPQ",
        ExportName = "sceAgcCbSetShRegisterRangeDirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirectGetSize(CpuContext ctx)
    {
        var valueCount = (uint)ctx[CpuRegister.Rdi];
        if (!TryGetCbSetShRegisterRangeDirectLayout(valueCount, out _, out var sizeBytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = sizeBytes;
        return (int)sizeBytes;
    }

    private static bool TryGetCbSetShRegisterRangeDirectLayout(
        uint valueCount,
        out uint packetDwords,
        out uint sizeBytes)
    {
        // The PM4 count field can encode at most 0x4001 dwords. This packet
        // uses two header dwords. The SharpEmu marker uses two more dwords.
        if (valueCount == 0 || valueCount > 0x3FFF)
        {
            packetDwords = 0;
            sizeBytes = 0;
            return false;
        }

        packetDwords = valueCount + 2;
        sizeBytes = (valueCount + 4) * sizeof(uint);
        return true;
    }

    [SysAbiExport(
        Nid = "wr23dPKyWc0",
        ExportName = "sceAgcCbReleaseMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbReleaseMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var action = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var gcrControl = (uint)(ctx[CpuRegister.Rdx] & 0xFFFF);
        var destination = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + 8, out var dataSelectionRaw) ||
            !TryReadUInt64(ctx, stackAddress + 16, out var data) ||
            !TryReadUInt64(ctx, stackAddress + 24, out var gdsOffsetRaw) ||
            !TryReadUInt64(ctx, stackAddress + 32, out var gdsSizeRaw) ||
            !TryReadUInt64(ctx, stackAddress + 40, out var interruptRaw) ||
            !TryReadUInt64(ctx, stackAddress + 48, out var interruptContextIdRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var dataSelection = (uint)(dataSelectionRaw & 0xFF);
        var gdsOffset = (uint)(gdsOffsetRaw & 0xFFFF);
        var gdsSize = (uint)(gdsSizeRaw & 0xFFFF);
        var interrupt = (uint)(interruptRaw & 0xFF);
        var interruptContextId = (uint)interruptContextIdRaw;
        if (commandBufferAddress == 0 ||
            destination > 1 ||
            dataSelection > 3 ||
            gdsOffset != 0 ||
            gdsSize > 2 ||
            interrupt > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RReleaseMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, action | (cachePolicy << 8)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                gcrControl | (dataSelection << 16) | (interrupt << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)data) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)(data >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 28, interruptContextId))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_release_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"action=0x{action:X2} gcr=0x{gcrControl:X4} dst=0x{destinationAddress:X16} data_sel={dataSelection} data=0x{data:X16}");
        TrackCbReleaseMemTarget(ctx, commandBufferAddress, destinationAddress);
        RecordRingChunkWriter(commandAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "TRO721eVt4g",
        ExportName = "sceAgcDcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var op = (uint)ctx[CpuRegister.Rsi];
        var state = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || op != 0x3FF || state != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RDrawReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_reset_queue buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ZvwO9euwYzc",
        ExportName = "sceAgcDcbSetCxRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RCxRegsIndirect, "cx");

    [SysAbiExport(
        Nid = "-HOOCn0JY48",
        ExportName = "sceAgcDcbSetShRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RShRegsIndirect, "sh");

    [SysAbiExport(
        Nid = "pFLArOT53+w",
        ExportName = "sceAgcDcbSetShRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegisterDirect(CpuContext ctx) =>
        DcbSetRegisterDirect(ctx, ItSetShReg, "sh");

    [SysAbiExport(
        Nid = "QhPDD513V0w",
        ExportName = "sceAgcDcbSetShRegisterDirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegisterDirectGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 3u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "hvUfkUIQcOE",
        ExportName = "sceAgcDcbSetUcRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RUcRegsIndirect, "uc");

    [SysAbiExport(
        Nid = "w4-d0n60hdo",
        ExportName = "sceAgcDcbSetUcRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegisterDirect(CpuContext ctx) =>
        DcbSetRegisterDirect(ctx, ItSetUconfigReg, "uc");

    [SysAbiExport(
        Nid = "aP1Ki9G3++4",
        ExportName = "sceAgcDcbSetUcRegisterDirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegisterDirectGetSize(CpuContext ctx)
    {
        // SET_UCONFIG_REG header + offset + value.
        ctx[CpuRegister.Rax] = 3u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "GIIW2J37e70",
        ExportName = "sceAgcDcbSetIndexSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexSize(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexSize = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        if (commandBufferAddress == 0 || cachePolicy != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItIndexType, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexSize))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_size buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} size={indexSize}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "8N2tmT3jmC8",
        ExportName = "sceAgcDcbSetIndexCount",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCount(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RIndexCount)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mljzuGDZRQ4",
        ExportName = "sceAgcDcbSetIndexCountGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCountGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 7u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "tSBxhAPyytQ",
        ExportName = "sceAgcDcbSetNumInstances",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetNumInstances(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var instanceCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNumInstances, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, instanceCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_num_instances buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={instanceCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "q88lQ+GP5Yk",
        ExportName = "sceAgcDcbDrawIndex",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndex(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var indexAddress = ctx[CpuRegister.Rdx];
        var modifier = (uint)ctx[CpuRegister.Rcx];

        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var baseCommand) ||
            !TryWriteUInt32(ctx, baseCommand, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 4, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, baseCommand + 8, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, baseCommand + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        // DRAW_INDEX_2 is six dwords: header, maximum index count, the
        // 64-bit index-buffer base, the draw count and the initiator.  The
        // former five-dword packet omitted both the real base and the count
        // field, so every call made by Unity looked like a zero-count draw to
        // the submitted-command parser and the complete scene was discarded.
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var drawCommand) ||
            !TryWriteUInt32(ctx, drawCommand, Pm4(6, ItDrawIndex2, 0)) ||
            !TryWriteUInt32(ctx, drawCommand + 4, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 8, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, drawCommand + 12, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, drawCommand + 16, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 20, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index buf=0x{commandBufferAddress:X16} " +
            $"base=0x{baseCommand:X16} draw=0x{drawCommand:X16} " +
            $"count={indexCount} index=0x{indexAddress:X16}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "1q1titRBL6o",
        ExportName = "sceAgcDcbDrawIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var emit = Interlocked.Increment(ref _indirectDrawEmitCount);

        if (emit <= 12 || emit % 250 == 0)
        {
            var rcx = ctx[CpuRegister.Rcx];
            var dump = string.Empty;
            for (var word = 0; word < 8; word++)
            {
                dump += TryReadUInt32(ctx, rcx + dataOffset + ((ulong)word * 4), out var raw)
                    ? $" {raw}"
                    : " ?";
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.emit_indirect#{emit} buf=0x{commandBufferAddress:X16} " +
                $"off=0x{dataOffset:X} rdx=0x{ctx[CpuRegister.Rdx]:X} rcx=0x{rcx:X} " +
                $"r8=0x{ctx[CpuRegister.R8]:X} rcx_words:{dump}");
        }

        if (commandBufferAddress == 0)
        {
            Interlocked.Increment(ref _indirectDrawEmitRejectCount);
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var drawCommand) ||
            !TryWriteUInt32(ctx, drawCommand, Pm4(5, ItDrawIndirect, 0)) ||
            !TryWriteUInt32(ctx, drawCommand + 4, dataOffset) ||
            !TryWriteUInt32(ctx, drawCommand + 8, 0) ||
            !TryWriteUInt32(ctx, drawCommand + 12, 0) ||
            !TryWriteUInt32(ctx, drawCommand + 16, 0))
        {
            var rejects = Interlocked.Increment(ref _indirectDrawEmitRejectCount);
            if (rejects <= 8 || rejects % 250 == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.emit_indirect_reject#{rejects} " +
                    $"buf=0x{commandBufferAddress:X16} reason=alloc_or_write");
            }

            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_indirect buf=0x{commandBufferAddress:X16} " +
            $"draw=0x{drawCommand:X16} offset=0x{dataOffset:X}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "Yw0jKSqop+E",
        ExportName = "sceAgcDcbDrawIndexAuto",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexAuto(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var modifier = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDrawIndexAuto)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_auto buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "t1vNu082-jM",
        ExportName = "sceAgcDcbDrawIndexIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, modifier))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index_indirect buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{dataOffset:X8} modifier=0x{modifier:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ypVBz4uPKcQ",
        ExportName = "sceAgcDcbDrawIndexIndirectMulti",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectMulti(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var drawCount = (uint)ctx[CpuRegister.Rdx];
        var stride = DrawIndexedIndirectArgsSize;
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItDrawIndexIndirectMulti, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, drawCount) ||
            !TryWriteUInt32(ctx, commandAddress + 24, stride) ||
            !TryWriteUInt32(ctx, commandAddress + 28, modifier))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index_indirect_multi buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{dataOffset:X8} draws={drawCount} " +
            $"stride={stride} modifier=0x{modifier:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mStuvI0zOtc",
        ExportName = "sceAgcDcbDrawIndexIndirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 5u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "r98I08t+LOg",
        ExportName = "sceAgcDcbDrawIndexIndirectMultiGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectMultiGetSize(CpuContext ctx)
    {
        // Eight, matching the packet DcbDrawIndexIndirectMulti emits.
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "rUuVjyR+Rd4",
        ExportName = "sceAgcDcbGetLodStatsGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStatsGetSize(CpuContext ctx)
    {
        var counterCount = (uint)ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = 0x10u + (counterCount * sizeof(uint));
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "vuSXe69VILM",
        ExportName = "sceAgcDcbGetLodStats",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStats(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var cachePolicy = (uint)ctx[CpuRegister.Rsi] & 0x3u;
        var destinationAddress = ctx[CpuRegister.Rdx];
        var control = (uint)ctx[CpuRegister.Rcx];
        var counterMask = (uint)ctx[CpuRegister.R8] & 0xFFu;
        var resetCounters = (uint)ctx[CpuRegister.R9] & 0x1u;
        if (!TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var enableRaw) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var counterSelectRaw) ||
            commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var enable = (uint)enableRaw & 0x1u;
        var counterSelect = (uint)counterSelectRaw & 0xFFu;
        var packetControl =
            (cachePolicy << 28) |
            (enable << 19) |
            (resetCounters << 18) |
            (counterMask << 10) |
            (counterSelect << 2);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItGetLodStats, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress & ~0x3Fu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, packetControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_get_lod_stats buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} control=0x{control:X8} counters=0x{counterMask:X2}");
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
        Nid = "57labkp+rSQ",
        ExportName = "sceAgcDcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var engine = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cbDbOp = (uint)ctx[CpuRegister.Rdx];
        var gcrControl = (uint)ctx[CpuRegister.Rcx];
        var baseAddress = ctx[CpuRegister.R8];
        var sizeBytes = ctx[CpuRegister.R9];
        if (!TryReadUInt32(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            engine > 1 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (engine << 31) | cbDbOp) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_acquire_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"engine={engine} cbdb=0x{cbDbOp:X8} gcr=0x{gcrControl:X8} base=0x{baseAddress:X16} size=0x{sizeBytes:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    // Matches the fixed 8-dword ACQUIRE_MEM packet DcbAcquireMem writes above.
    [SysAbiExport(
        Nid = "-vnlTPPXPrw",
        ExportName = "sceAgcDcbAcquireMemGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbAcquireMemGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "i1jyy49AjXU",
        ExportName = "sceAgcDcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWriteData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        var dwordCount = (uint)ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var incrementRaw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var writeConfirmRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var increment = (uint)(incrementRaw & 0xFF);
        var writeConfirm = (uint)(writeConfirmRaw & 0xFF);
        if (commandBufferAddress == 0 ||
            destinationAddress == 0 ||
            dataAddress == 0 ||
            dwordCount > 0x3FFD)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = dwordCount + 4;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RWriteData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination | (cachePolicy << 8) | (increment << 16) | (writeConfirm << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < dwordCount; index++)
        {
            if (!TryReadUInt32(ctx, dataAddress + ((ulong)index * sizeof(uint)), out var value) ||
                !TryWriteUInt32(ctx, commandAddress + 16 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        if (ShouldTraceHotPath(ref _dcbWriteDataTraceCount))
        {
            TraceAgc(
                $"agc.dcb_write_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"dst={destination} cache={cachePolicy} addr=0x{destinationAddress:X16} count={dwordCount} " +
                $"increment={increment} confirm={writeConfirm}");
        }

        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    // Extends the cursor cache over trailer packets built after a lap's last
    // release_mem, which would otherwise be dropped from the closed slice.
    private static void RefreshBuilderArenaCursorPassive(CpuContext ctx, ulong commandBufferAddress)
    {
        if (!_forceSubmitOrphanPreamblesEnabled)
        {
            return;
        }

        lock (_orphanPreambleGate)
        {
            if (!_knownBuilderHeaders.Contains(commandBufferAddress))
            {
                return;
            }
        }

        if (!TryReadUInt64(ctx, commandBufferAddress, out var arenaBase) ||
            arenaBase == 0 ||
            !TryReadUInt64(ctx, commandBufferAddress + 0x10, out var arenaCursor))
        {
            return;
        }

        lock (_orphanPreambleGate)
        {
            if (_builderArenaLastSeen.TryGetValue(commandBufferAddress, out var seen) &&
                seen.Base == arenaBase &&
                arenaCursor > seen.Cursor)
            {
                _builderArenaLastSeen[commandBufferAddress] =
                    (arenaBase, arenaCursor, GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
            }
        }
    }

    [SysAbiExport(
        Nid = "VmW0Tdpy420",
        ExportName = "sceAgcDcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var operation = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var address = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var reference) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            operation > 4 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 7u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
                 !TryWriteUInt32(ctx, commandAddress + 4, (uint)address & (size == 0 ? ~0x3u : ~0x7u)) ||
                 !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32) & 0x3FFFFu) ||
                 !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }
        else if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, EncodeWaitRegMem32Control(compareFunction, operation, cachePolicy)) ||
                !TryWriteUInt32(ctx, commandAddress + 24, EncodeWaitRegMemPoll(pollCycles)))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, EncodeWaitRegMem64Control(compareFunction, operation, cachePolicy)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, EncodeWaitRegMemPoll(pollCycles)))
        {
            return ReturnPointer(ctx, 0);
        }

        if (ShouldTraceHotPath(ref _dcbWaitRegMemTraceCount))
        {
            TraceAgc(
                $"agc.dcb_wait_reg_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"size={size} compare={compareFunction} op={operation} cache={cachePolicy} " +
                $"addr=0x{address:X16} ref=0x{reference:X16} mask=0x{mask:X16} poll={pollCycles}");
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "43WJ08sSugE",
        ExportName = "sceAgcDcbWaitOnAddressGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitOnAddressGetSize(CpuContext ctx)
    {
        var size = (uint)ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = size switch
        {
            0 => 14u * sizeof(uint),
            1 => 16u * sizeof(uint),
            _ => 0,
        };
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "u2T2DiA5hRI",
        ExportName = "sceAgcDcbStallCommandBufferParser",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParser(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var address = ctx[CpuRegister.Rdx];
        var reference = ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || size > 1 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        // Direct execution submits work synchronously, so there is no independent
        // hardware command processor to stall. Keep a well-formed no-op in the DCB
        // so packet addresses and the command-buffer cursor remain coherent.
        TraceAgc(
            $"agc.dcb_stall_parser buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"size={size} addr=0x{address:X16} reference=0x{reference:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+u6dKSLWM2o",
        ExportName = "sceAgcDcbStallCommandBufferParserGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParserGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "WmAc2MEj6Io",
        ExportName = "sceAgcDcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var source = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R8];
        var sourceCachePolicy = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var control4Raw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var sourceAddress) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var byteCount) ||
            !TryReadUInt64(ctx, stackAddress + (4 * sizeof(ulong)), out var control7Raw) ||
            !TryReadUInt64(ctx, stackAddress + (5 * sizeof(ulong)), out var control8Raw) ||
            !TryReadUInt64(ctx, stackAddress + (6 * sizeof(ulong)), out var control9Raw))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || byteCount == 0 || (byteCount & 3) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var control4 = (uint)(control4Raw & 0xFF);
        var control7 = (uint)(control7Raw & 0xFF);
        var control8 = (uint)(control8Raw & 0xFF);
        var control9 = (uint)(control9Raw & 0xFF);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RDmaData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination |
                (destinationCachePolicy << 8) |
                (source << 16) |
                (sourceCachePolicy << 24)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                control4 | (control7 << 8) | (control8 << 16) | (control9 << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, byteCount) ||
            !ctx.TryWriteUInt64(commandAddress + 16, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 24, sourceAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_dma_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} bytes={byteCount} " +
            $"control0=0x{destination | (destinationCachePolicy << 8) | (source << 16) | (sourceCachePolicy << 24):X8}");
        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "2ccJz9LQI+w",
        ExportName = "sceAgcDcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "-RnpfpxIhec",
        ExportName = "sceAgcAcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var sourceSelector = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationSelector = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceOrImmediate) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var byteCount) ||
            commandBufferAddress == 0 ||
            byteCount == 0 ||
            byteCount > 256u * 1024u * 1024u ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDmaData)) ||
            !ctx.TryWriteUInt64(commandAddress + 4, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 12, sourceOrImmediate) ||
            !TryWriteUInt32(ctx, commandAddress + 20, byteCount) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 24,
                sourceSelector | (destinationSelector << 8)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "M0ttm8h7SKA",
        ExportName = "sceAgcAcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "RmaJwLtc8rY",
        ExportName = "sceAgcDcbSetBaseIndirectArgs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetBaseIndirectArgs(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var baseIndex = (uint)ctx[CpuRegister.Rsi];
        var address = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItSetBase, 0) | (baseIndex << 1)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 1) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)address & ~7u) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "CtB+A9-VxO0",
        ExportName = "sceAgcDcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+kSrjIVxKFE",
        ExportName = "sceAgcDcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPushMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var markerAddress = ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryReadGuestCString(ctx, markerAddress, 4095, out var marker))
        {
            return ReturnPointer(ctx, 0);
        }

        var payloadDwords = Math.Max(((uint)marker.Length + 4) / 4, 1);
        var packetDwords = payloadDwords + 1;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RPushMarker)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < payloadDwords; index++)
        {
            uint value = 0;
            for (uint byteIndex = 0; byteIndex < sizeof(uint); byteIndex++)
            {
                var markerIndex = (index * sizeof(uint)) + byteIndex;
                if (markerIndex < (uint)marker.Length)
                {
                    value |= (uint)marker[(int)markerIndex] << ((int)byteIndex * 8);
                }
            }

            if (!TryWriteUInt32(ctx, commandAddress + 4 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "cpCILPya5Zk",
        ExportName = "sceAgcAcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPushMarker(CpuContext ctx) => DcbPushMarker(ctx);

    [SysAbiExport(
        Nid = "H7uZqCoNuWk",
        ExportName = "sceAgcDcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPopMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RPopMarker)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "6mFxkVqdmbQ",
        ExportName = "sceAgcAcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPopMarker(CpuContext ctx) => DcbPopMarker(ctx);

    [SysAbiExport(
        Nid = "IxYiarKlXxM",
        ExportName = "sceAgcDmaDataPatchSetDstAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetDstAddressOrOffset(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var destinationAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var destinationOffset = packetLength == 7 ? 4UL : 16UL;
        return ctx.TryWriteUInt64(commandAddress + destinationOffset, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // SRC counterpart of sceAgcDmaDataPatchSetDstAddressOrOffset. Without this,
    // the source stays 0 and the DMA copy — often a label write — never runs.
    [SysAbiExport(
        Nid = "cdDRpqcFGbU",
        ExportName = "sceAgcDmaDataPatchSetSrcAddressOrOffsetOrImmediate",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetSrcAddressOrOffsetOrImmediate(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var sourceValue = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var sourceOffset = packetLength == 7 ? 12UL : 24UL;
        return ctx.TryWriteUInt64(commandAddress + sourceOffset, sourceValue)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "eAy8eGNsCuU",
        ExportName = "sceAgcWriteDataPatchSetCachePolicy",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetCachePolicy(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 1);

    [SysAbiExport(
        Nid = "tmy-+rBpspY",
        ExportName = "sceAgcWriteDataPatchSetDst",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetDst(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 0);

    [SysAbiExport(
        Nid = "fPSCdQxgpSw",
        ExportName = "sceAgcWriteDataPatchSetAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetAddressOrOffset(CpuContext ctx)
    {
        // SDK revisions disagree on whether the packet or destination is the
        // first argument. Astro passes (destination, packet), while older
        // captures use (packet, destination), so identify the packet by its
        // header instead of hard-coding one ordering.
        var first = ctx[CpuRegister.Rdi];
        var second = ctx[CpuRegister.Rsi];
        ulong commandAddress;
        ulong destinationAddress;
        if (TryGetPacketIdentity(ctx, first, out var firstOp, out var firstRegister) &&
            firstOp == ItNop && firstRegister == RWriteData)
        {
            commandAddress = first;
            destinationAddress = second;
        }
        else if (TryGetPacketIdentity(ctx, second, out var secondOp, out var secondRegister) &&
                 secondOp == ItNop && secondRegister == RWriteData)
        {
            commandAddress = second;
            destinationAddress = first;
        }
        else
        {
            // Astro's SDK 9 ABI passes (address-or-offset, pointer-to-field)
            // rather than the whole packet. The field is already the packet's
            // 64-bit address payload, so patch it directly.
            if (second == 0 || !ctx.TryWriteUInt64(second, first))
            {
                return SetReturn(ctx, second == 0
                    ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceAgc(
                $"agc.patch_write_data_field field=0x{second:X16} value=0x{first:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        TraceAgc(
            $"agc.patch_write_data_addr cmd=0x{commandAddress:X16} dst=0x{destinationAddress:X16}");
        return ctx.TryWriteUInt64(commandAddress + 8, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3KDcnM3lrcU",
        ExportName = "sceAgcWaitRegMemPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 8UL
            : op == ItNop && register is RWaitMem32 or RWaitMem64
                ? 4UL
                : 0;
        if (fieldOffset == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItNop && register is RWaitMem32 or RWaitMem64
            ? TryWriteUInt32(
                  ctx,
                  commandAddress + fieldOffset,
                  (uint)address & (register == RWaitMem32 ? ~0x3u : ~0x7u)) &&
              TryWriteUInt32(ctx, commandAddress + fieldOffset + 4, (uint)(address >> 32) & 0x3FFFFu)
            : ctx.TryWriteUInt64(commandAddress + fieldOffset, address);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "n485EBnIWmk",
        ExportName = "sceAgcWaitRegMemPatchCompareFunction",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchCompareFunction(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var compareFunction = (uint)ctx[CpuRegister.Rsi];
        if (compareFunction > 7 ||
            !TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 4UL
            : op == ItNop && register == RWaitMem32
                ? 20UL
                : op == ItNop && register == RWaitMem64
                    ? 28UL
                    : 0;
        return fieldOffset != 0 &&
               TryPatchUInt32Bits(ctx, commandAddress + fieldOffset, 0x7u, compareFunction)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, fieldOffset == 0
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "7nOoijNPvEU",
        ExportName = "sceAgcWaitRegMemPatchReference",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchReference(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var reference = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 16, (uint)reference)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 16, (uint)reference)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 20, reference);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "hXAnLgDHCoI",
        ExportName = "sceAgcWaitRegMemPatchMask",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchMask(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var mask = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 20, (uint)mask)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 12, (uint)mask)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 12, mask);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    // PatchAddress/PatchData touch UInt64 fields at +12/+20 of an RReleaseMem
    // packet, so the packet is at least 7 dwords; use the 8-dword RELEASE_MEM
    // family size already used elsewhere in this file.
    [SysAbiExport(
        Nid = "hL7C0IRpWZI",
        ExportName = "sceAgcCbQueueEndOfPipeActionGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbQueueEndOfPipeActionGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "0fWWK5uG9rQ",
        ExportName = "sceAgcQueueEndOfPipeActionPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RReleaseMem)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 12, address)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "J8YCgfKAMQs",
        ExportName = "sceAgcQueueEndOfPipeActionPatchGcrCntl",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchGcrCntl(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x0000_FFFFu,
                (uint)ctx[CpuRegister.Rsi] & 0xFFFFu)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "MlEw1feXcjg",
        ExportName = "sceAgcQueueEndOfPipeActionPatchData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchData(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 20, ctx[CpuRegister.Rsi])
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "T9fjQIINoeE",
        ExportName = "sceAgcQueueEndOfPipeActionPatchType",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchType(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var dataSelection = (uint)ctx[CpuRegister.Rsi];
        TraceAgc(
            $"agc.eop_patch_type cmd=0x{commandAddress:X16} value=0x{dataSelection:X8}");
        if (dataSelection > 3 || !IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x00FF_0000u,
                dataSelection << 16)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool IsAgcReleaseMemPacket(CpuContext ctx, ulong commandAddress) =>
        TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) &&
        op == ItNop &&
        register == RReleaseMem;

    private static bool TryPatchUInt32Bits(
        CpuContext ctx,
        ulong address,
        uint mask,
        uint value)
    {
        return TryReadUInt32(ctx, address, out var current) &&
               TryWriteUInt32(ctx, address, PatchUInt32Bits(current, mask, value));
    }

    private static uint PatchUInt32Bits(uint current, uint mask, uint value) =>
        (current & ~mask) | (value & mask);

    [SysAbiExport(
        Nid = "l4fM9K-Lyks",
        ExportName = "sceAgcDcbSetIndexBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexBuffer(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexBufferAddress = ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(indexBufferAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(indexBufferAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_buffer buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} addr=0x{indexBufferAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "B+aG9DUnTKA",
        ExportName = "sceAgcDcbDrawIndexOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexOffset(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexOffset = (uint)ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexOffset2, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, indexOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 12, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 16, flags & 0xE000_0001u))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_offset buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset={indexOffset} count={indexCount} flags=0x{flags:X8}");
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
        Nid = "UglJIZjGssM",
        ExportName = "sceAgcDriverSubmitDcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitDcb(CpuContext ctx)
    {
        Interlocked.Increment(ref _dcbSubmitCount);
        Volatile.Write(ref _lastDcbSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        var packetAddress = ctx[CpuRegister.Rdi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            TraceAgc($"agc.driver_submit_dcb_rejected packet=0x{packetAddress:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }

            // Unconditional (unlike tracePackets above, not deduped by dwordCount):
            // every DriverSubmitDcb call's target address and size, so submission
            // history can be reconstructed even when most sizes repeat.
            TraceAgc($"agc.driver_submit_dcb_call addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        TraceAgc(
            $"agc.driver_submit_dcb packet=0x{packetAddress:X16} addr=0x{commandAddress:X16} " +
            $"dwords={dwordCount} end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        GuestGpu.Current.AttachGuestMemory(ctx.Memory);
        RecordGameSubmittedRange(commandAddress, dwordCount);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        var submittedIndexSnapshots = CaptureSubmittedIndexPackets(
            ctx,
            commandAddress,
            dwordCount,
            gpuState.Graphics.IndexSize,
            out var submittedVertexSnapshots);
        lock (gpuState.Gate)
        {
            gpuState.Graphics.QueueName = "dcb.graphics";
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                gpuState.Graphics,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets,
                submittedIndexSnapshots,
                submittedVertexSnapshots);
            DrainResumableDcbs(ctx, gpuState, tracePackets);
        }

        // No orphan-preamble drain here — this runs on a native guest worker
        // thread, where long managed work fail-fasts the runtime. The GPU
        // wait monitor drains this instead.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gSRnr79F8tQ",
        ExportName = "sceAgcDriverSubmitAcb",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitAcb(CpuContext ctx)
    {
        var ownerHandle = (uint)ctx[CpuRegister.Rdi];
        var packetAddress = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 ||
            !TryReadUInt64(ctx, packetAddress, out var commandAddress) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var dwordCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = false;
        if (_traceAgc)
        {
            lock (_submitTraceGate)
            {
                tracePackets = _tracedDcbSizes.Add(dwordCount);
            }

            // Unconditional (unlike tracePackets above, not deduped by dwordCount):
            // every DriverSubmitAcb call's target address and size.
            TraceAgc(
                $"agc.driver_submit_acb_call owner={ownerHandle} addr=0x{commandAddress:X16} dwords={dwordCount}");
        }

        TraceAgc(
            $"agc.driver_submit_acb owner={ownerHandle} packet=0x{packetAddress:X16} " +
            $"addr=0x{commandAddress:X16} dwords={dwordCount} " +
            $"end=0x{commandAddress + ((ulong)dwordCount * sizeof(uint)):X16}");

        GuestGpu.Current.AttachGuestMemory(ctx.Memory);
        RecordGameSubmittedRange(commandAddress, dwordCount);
        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            if (!gpuState.ComputeQueues.TryGetValue(ownerHandle, out var queueState))
            {
                queueState = new SubmittedDcbState();
                gpuState.ComputeQueues.Add(ownerHandle, queueState);
            }

            queueState.QueueName = $"acb.compute[{ownerHandle}]";
            queueState.CompletionEventId = ownerHandle;
            EnqueueSubmittedDcb(
                ctx,
                gpuState,
                queueState,
                commandAddress,
                dwordCount,
                ++gpuState.SubmissionSequence,
                tracePackets,
                indexSnapshots: null,
                vertexSnapshots: null);
            DrainResumableDcbs(ctx, gpuState, tracePackets);
        }

        // See DriverSubmitDcb: orphan drains run only on the wait monitor
        // thread, never in this guest-thread import window.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
    Nid = "uJziRsODk1c",
    ExportName = "sceAgcDriverGetResourceRegistrationMaxNameLength",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverGetResourceRegistrationMaxNameLength(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];

        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, outAddress, 256))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_resource_registration_max_name_length out=0x{outAddress:X16} value=256");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private const uint DefaultAgcOwner = 1;
    [SysAbiExport(
        Nid = "F0ZXt5q0ZTA",
        ExportName = "sceAgcDriverGetDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverGetDefaultOwner(CpuContext ctx)
    {
        var ownerAddress = ctx[CpuRegister.Rdi];

        if (ownerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, ownerAddress, DefaultAgcOwner))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.driver_get_default_owner out=0x{ownerAddress:X16} owner={DefaultAgcOwner}");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
    Nid = "W5z4eZrjEas",
    ExportName = "sceAgcDriverRegisterResource",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverRegisterResource(CpuContext ctx)
    {
        var resourceAddress = ctx[CpuRegister.Rdi];
        var owner = (uint)ctx[CpuRegister.Rsi];
        var nameAddress = ctx[CpuRegister.Rdx];
        var type = (uint)ctx[CpuRegister.R8];
        var flags = (uint)ctx[CpuRegister.R9];

        TraceAgc(
            $"agc.driver_register_resource resource=0x{resourceAddress:X16} owner={owner} " +
            $"name=0x{nameAddress:X16} type={type} flags={flags}");

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
    Nid = "-KRzWekV120",
    ExportName = "sceAgcDriverUnknown_KRzWekV120",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverUnknownKRzWekV120(CpuContext ctx)
    {
        TraceAgc(
            $"agc.driver_unknown_krz rdi=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"rsi=0x{ctx[CpuRegister.Rsi]:X16} rdx=0x{ctx[CpuRegister.Rdx]:X16} " +
            $"rcx=0x{ctx[CpuRegister.Rcx]:X16} r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16}");

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }
    #pragma warning restore SHEM006

    [SysAbiExport(
        Nid = "h9z6+0hEydk",
        ExportName = "sceAgcSuspendPoint",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SuspendPoint(CpuContext ctx)
    {
        TraceAgc("agc.suspend_point");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "qj7QZpgr9Uw",
        ExportName = "sceAgcUnknownQj7QZpgr9Uw",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int UnknownQj7QZpgr9Uw(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 1, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, 0x8000_0000))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.unknown_qj7 buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"arg1=0x{ctx[CpuRegister.Rsi]:X16} arg2=0x{ctx[CpuRegister.Rdx]:X16}");
        return ReturnPointer(ctx, commandAddress);
    }
    #pragma warning restore SHEM006

    private static void EnqueueSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        ulong submissionId,
        bool tracePackets,
        Dictionary<ulong, SubmittedIndexSnapshot>? indexSnapshots,
        Dictionary<ulong, SubmittedVertexSnapshot>? vertexSnapshots)
    {
        if (state.IsFaulted)
        {
            ReportFaultedSubmissionRejected(state, submissionId);
            return;
        }

        state.PendingSubmissions.Enqueue(new SubmittedDcbState.PendingSubmission(
            commandAddress,
            dwordCount,
            submissionId,
            tracePackets,
            indexSnapshots,
            vertexSnapshots));
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void ReportFaultedSubmissionRejected(
        SubmittedDcbState state,
        ulong submissionId)
    {
        if (state.FaultedSubmissionReported)
        {
            return;
        }

        state.FaultedSubmissionReported = true;
        Console.Error.WriteLine(
            $"[LOADER][WARN] agc.queue_submit_rejected queue={state.QueueName} " +
            $"submission={submissionId} reason='{state.FaultReason}'");
    }

    private static void PumpSubmittedQueue(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state)
    {
        if (state.IsFaulted)
        {
            return;
        }

        if (state.IsSuspended)
        {
            // An explicit new submission supersedes a ring-tail park — the
            // game moved to a fresh ring, so abandon the park.
            if (state.RingTailParkAddress == 0 ||
                state.PendingSubmissions.Count == 0 ||
                !GpuWaitRegistry.TryRemoveByState(state, state.RingTailParkAddress))
            {
                return;
            }

            TraceAgc(
                $"agc.dcb.ring_tail_superseded addr=0x{state.RingTailParkAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId}");
            // Keeps its full recorded extent so the arena sweep doesn't
            // double-run the tail once the game's own re-parse reaches it.
            state.RingTailParkAddress = 0;
            state.IsSuspended = false;
            state.HasActiveSubmission = false;
            state.ActiveIndexSnapshots = null;
            state.ActiveVertexSnapshots = null;
            NotifySubmittedDcbCompleted(gpuState, state, state.ActiveSubmissionId);
        }

        while (!state.HasActiveSubmission &&
               state.PendingSubmissions.TryDequeue(out var submission))
        {
            state.HasActiveSubmission = true;
            state.ActiveSubmissionId = submission.SubmissionId;
            state.ActiveIndexSnapshots = submission.IndexSnapshots;
            state.ActiveVertexSnapshots = submission.VertexSnapshots;
            state.RingChunkBase = state.IsForceSubmittedRing ? 0 : submission.CommandAddress;
            state.FollowedChunkAdvance = false;
            var isSuspended = ParseSubmittedDcb(
                ctx,
                gpuState,
                state,
                submission.CommandAddress,
                submission.DwordCount,
                submission.TracePackets);
            if (state.IsFaulted)
            {
                return;
            }

            state.IsSuspended = isSuspended;
            if (state.IsSuspended)
            {
                return;
            }

            state.HasActiveSubmission = false;
            state.ActiveIndexSnapshots = null;
            state.ActiveVertexSnapshots = null;
            NotifySubmittedDcbCompleted(gpuState, state, submission.SubmissionId);
        }
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
        // Hardware raises an end-of-pipe interrupt for every submission on every
        // queue, so this is unconditional. It stays safe for titles that do not
        // want it because delivery is registration-gated: TriggerRegisteredEvents
        // only queues onto equeues that registered this exact ident through
        // sceAgcDriverAddEqEvent. Graphics keeps ident 0; a compute queue uses the
        // owner handle it was submitted under.
        var completionEventId = state.CompletionEventId;
        var isGraphics = ReferenceEquals(state, gpuState.Graphics);
        var queueName = state.QueueName;
        void TriggerCompletionEvents()
        {
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

    // Returns true when parsing stops on a wait or a terminal queue fault.
    // Unsupported synchronization packets must not let later GPU work run.
    private static bool ParseSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (commandAddress == 0 || dwordCount == 0 || dwordCount > 1_000_000)
        {
            return false;
        }

        using var guestQueueScope = GuestGpu.Current.EnterGuestQueue(
            state.QueueName,
            state.ActiveSubmissionId);
        // A submission is one link of a chain, not necessarily the whole stream:
        // when a title's command arena fills mid-frame it continues in a fresh
        // buffer and links the two with an INDIRECT_BUFFER packet, then submits
        // only the first link. Stopping at the end of the submitted window drops
        // every packet past the switch -- including the flip and the end-of-frame
        // completion labels the guest is waiting on.
        for (var chainDepth = 0; ; chainDepth++)
        {
            if (chainDepth > MaxSubmittedChainDepth)
            {
                TraceAgc(
                    $"agc.dcb_chain_depth_exceeded queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} addr=0x{commandAddress:X16}");
                return false;
            }

            state.PendingChainAddress = 0;
            state.PendingChainDwords = 0;
            state.LastParsedAddress = commandAddress;
            var windowByteCount = checked((int)(dwordCount * sizeof(uint)));
            var rented = GuestDataPool.Shared.Rent(windowByteCount);
            bool suspended;
            try
            {
                _dcbWindowLease = DcbWindowInvalidationRegistry.Register(
                    commandAddress,
                    (ulong)windowByteCount);
                if (ctx.Memory.TryRead(commandAddress, rented.AsSpan(0, windowByteCount)))
                {
                    _dcbWindowBuffer = rented;
                    _dcbWindowStart = commandAddress;
                    _dcbWindowByteLength = windowByteCount;
                }
                else
                {
                    DropCurrentDcbWindow();
                }

                suspended = ParseSubmittedDcbCore(
                    ctx,
                    gpuState,
                    state,
                    commandAddress,
                    dwordCount,
                    tracePackets);
            }
            finally
            {
                DropCurrentDcbWindow();
                GuestDataPool.Shared.Return(rented);
            }

            // Record only what was actually parsed, not the full declared
            // size — else the orphan sweep either starves a suspended
            // queue's remaining packets or double-runs ones it already ran.
            if (!state.IsForceSubmittedRing && state.LastParsedAddress > commandAddress)
            {
                var consumedDwords = (uint)Math.Min(
                    (state.LastParsedAddress - commandAddress) / sizeof(uint),
                    dwordCount);
                RecordGameSubmittedRange(commandAddress, consumedDwords);
            }

            if (suspended)
            {
                return true;
            }

            var chainAddress = state.PendingChainAddress;
            var chainDwords = state.PendingChainDwords;
            if (chainAddress == 0 || chainDwords == 0 || chainDwords > 1_000_000)
            {
                return false;
            }

            commandAddress = chainAddress;
            dwordCount = chainDwords;
        }
    }

    // Deep enough for a title that links one continuation buffer per frame,
    // shallow enough that a self-referencing chain cannot spin forever.
    private const int MaxSubmittedChainDepth = 64;

    private static bool ParseSubmittedDcbCore(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        var offset = 0u;
        while (offset < dwordCount)
        {
            var currentAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, currentAddress, out var header))
            {
                TracePacketParseFailure(state, currentAddress, offset, 0, "header-read");
                return false;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.packet dw={offset} addr=0x{currentAddress:X16} " +
                        $"header=0x{header:X8} len=1 type=2");
                }

                offset++;
                continue;
            }

            if (header == 0 &&
                (state.FollowedChunkAdvance || state.IsForceSubmittedRing) &&
                _gpuWaitSuspendEnabled)
            {
                // Ring memory the game has not appended to yet — the bound the
                // CP's write pointer would impose. Park until it is written.
                return SuspendOnUnwrittenRingWord(
                    ctx, state, commandAddress, currentAddress, offset, tracePackets);
            }

            if (packetType != 3)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"packet-type-{packetType}");
                return false;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"length-{length}-remaining-{dwordCount - offset}");
                return false;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (!KnownPm4Opcodes.Contains(op) && _seenUnknownOpcodes.Add(op))
            {
                TryReadUInt32(ctx, currentAddress + 4, out var unknownPayload0);
                TryReadUInt32(ctx, currentAddress + 8, out var unknownPayload1);
                var possibleTarget = ((ulong)(unknownPayload1 & 0xFFFFu) << 32) | unknownPayload0;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.dcb.unknown_opcode op=0x{op:X2} reg=0x{register:X2} " +
                    $"len={length} addr=0x{currentAddress:X16} queue={state.QueueName} " +
                    $"payload0=0x{unknownPayload0:X8} payload1=0x{unknownPayload1:X8} " +
                    $"possible_target=0x{possibleTarget:X16}");
            }
            if (_traceFramePackets && ReferenceEquals(state, gpuState.Graphics))
            {
                var packetKey = (op, op == ItNop ? register : uint.MaxValue);
                state.FramePacketCounts[packetKey] =
                    state.FramePacketCounts.TryGetValue(packetKey, out var packetCount)
                        ? packetCount + 1
                        : 1;
                state.FramePacketCount++;
            }
            if (tracePackets)
            {
                TraceSubmittedPacket(ctx, currentAddress, offset, header, length, op, register);
            }

            if (_traceDraws)
            {
                CountSubmittedOpcode(op, register);
            }

            if ((header & 1u) != 0 && state.PredicateSkip)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.predicated_skip queue={state.QueueName} " +
                        $"packet=0x{currentAddress:X16} op=0x{op:X2} len={length}");
                }

                offset += length;
                continue;
            }

            var isAcquireMem = op == ItNop && register == RAcquireMem && length >= 8;
            // Flush coalesced ACQUIRE_MEM only before packets that consume guest
            // resources (draw/dispatch/dma/flip). Flushing before every register
            // write produced a storm of tiny ordered actions during load.
            if (!isAcquireMem &&
                PacketRequiresPendingAcquireFlush(op, register, length))
            {
                FlushPendingAcquireInvalidation(ctx, state, tracePackets);
            }

            if (op == ItSetPredication)
            {
                ApplySubmittedPredication(ctx, state, currentAddress, length, tracePackets);
                offset += length;
                continue;
            }

            if (op == ItRewind && length >= 2)
            {
                if (HandleSubmittedRewind(
                        ctx,
                        state,
                        commandAddress,
                        currentAddress,
                        offset,
                        length,
                        dwordCount,
                        tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // suspended until RewindPatchSetRewindState
                }

                offset += length;
                continue;
            }

            if (op == ItIndirectBuffer &&
                length >= 4 &&
                TryReadUInt32(ctx, currentAddress + 4, out var chainLow) &&
                TryReadUInt32(ctx, currentAddress + 8, out var chainHigh) &&
                TryReadUInt32(ctx, currentAddress + 12, out var chainDwords))
            {
                var chainAddress = ((ulong)(chainHigh & 0xFFFFu) << 32) | chainLow;
                var chainLength = chainDwords & 0xFFFFFu;
                // Titles emit a zeroed INDIRECT_BUFFER as padding for a branch they
                // decided not to take. Only a populated one redirects the stream.
                if (chainAddress != 0 && chainLength != 0)
                {
                    state.PendingChainAddress = chainAddress;
                    state.PendingChainDwords = chainLength;
                    state.RingChunkBase = chainAddress;
                    TraceAgc(
                        $"agc.dcb_chain queue={state.QueueName} " +
                        $"submission={state.ActiveSubmissionId} " +
                        $"packet=0x{currentAddress:X16} " +
                        $"target=0x{chainAddress:X16} dwords={chainLength}");

                    // The link is a jump, not a call: whatever follows it in this
                    // buffer is unreachable padding.
                    return false;
                }

                // target=1, size=0 is the ring-chunk-advance sentinel: continue
                // at the next contiguous chunk. Distinct from padding (target=0).
                if (chainAddress == 1 && state.RingChunkBase != 0)
                {
                    var nextChunk = state.RingChunkBase + RingChunkBytes;
                    TraceAgc(
                        $"agc.dcb.chunk_advance from=0x{currentAddress:X16} " +
                        $"next=0x{nextChunk:X16}");

                    state.PendingChainAddress = nextChunk;
                    state.PendingChainDwords = RingChunkBytes / sizeof(uint);
                    state.RingChunkBase = nextChunk;
                    state.FollowedChunkAdvance = true;
                    return false;
                }
            }

            if (op == ItNop &&
                register is RDrawReset or RAcbReset &&
                length >= 2)
            {
                ResetSubmittedParserState(state);
                TraceAgc(
                    $"agc.queue_reset queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"kind={(register == RDrawReset ? "draw" : "acb")} " +
                    $"packet=0x{currentAddress:X16}");
            }

            if (isAcquireMem)
            {
                ApplySubmittedAcquireMem(
                    ctx,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItSetShReg &&
                TryReadTextureDescriptor(ctx, currentAddress, length, out var texture))
            {
                state.PresenterTexture = texture;
            }

            ApplySubmittedRegisters(ctx, state, currentAddress, length, op, register);

            if (op == ItSetBase &&
                length >= 4 &&
                TryReadUInt32(ctx, currentAddress + 4, out var baseSelector) &&
                baseSelector == 1 &&
                TryReadUInt64(ctx, currentAddress + 8, out var indirectArgsAddress))
            {
                state.IndirectArgsAddress = indirectArgsAddress;
            }

            if (op == ItEventWrite &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + sizeof(uint), out var eventTypeRaw))
            {
                // IT_EVENT_WRITE has no interrupt selector on hardware; EOP
                // interrupts come from RELEASE_MEM only. Delivering kernel
                // events here would over-count completions.
                if (tracePackets)
                {
                    TraceAgc($"agc.dcb.event type=0x{eventTypeRaw & 0x3Fu:X2} queues=none");
                }
            }

            if (op == ItNop && register == RReleaseMem && length >= 7)
            {
                ApplySubmittedReleaseMem(ctx, gpuState, state, currentAddress, tracePackets);
            }

            if (op == ItReleaseMem && length >= 8)
            {
                ApplySubmittedStandardReleaseMem(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItMemSemaphore)
            {
                if (HandleSubmittedMemSemaphore(
                        ctx,
                        gpuState,
                        state,
                        commandAddress,
                        currentAddress,
                        offset,
                        length,
                        dwordCount,
                        tracePackets))
                {
                    return true;
                }
            }

            if (op == ItCopyData &&
                HandleSubmittedCopyData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    tracePackets))
            {
                return true;
            }

            if (op == ItNop && register == RWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: false,
                    tracePacket: tracePackets);
            }

            if (op == ItWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: true,
                    tracePacket: tracePackets);
            }

            if (op == ItNop && register == RDmaData && length >= 7)
            {
                // Ensure CMASK addresses are tracked before DMA fills
                var tempTargets = GetRenderTargets(state.CxRegisters);
                TrackCmaskAddresses(state.CxRegisters, tempTargets);

                ApplySubmittedDmaData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    compactLayout: length == 7,
                    tracePacket: tracePackets);
            }

            if (op == ItDmaData && length >= 7)
            {
                ApplySubmittedStandardDmaData(ctx, gpuState, state, currentAddress);
            }

            if (op == ItIndexBase &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBaseLo) &&
                TryReadUInt32(ctx, currentAddress + 8, out var indexBaseHi))
            {
                state.IndexBufferAddress =
                    indexBaseLo | ((ulong)indexBaseHi << 32);
            }

            if (op == ItIndexBufferSize &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBufferCount))
            {
                state.IndexBufferCount = indexBufferCount;
            }

            if (op == ItNop &&
                register == RIndexCount &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var customIndexCount))
            {
                state.IndexBufferCount = customIndexCount;
            }

            if (op == ItIndexType &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexSize))
            {
                state.IndexSize = indexSize & 0x3;
            }

            if (op == ItNumInstances &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var instanceCount))
            {
                state.InstanceCount = Math.Max(instanceCount, 1);
            }

            if (op == ItNop &&
                register is RWaitMem32 or RWaitMem64 &&
                length >= (register == RWaitMem32 ? 6u : 9u))
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: register == RWaitMem64, isStandard: false,
                        tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (op == ItWaitRegMem && length >= 7)
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: false, isStandard: true, tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (TryReadSubmittedDrawCount(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    op,
                    out var indexCount) &&
                indexCount != 0)
            {
                state.FrameDrawCount++;
                if (_traceAgcShader)
                {
                    lock (_submitTraceGate)
                    {
                        if (_tracedSubmittedDrawOpcodes.Add(op))
                        {
                            TraceAgcShader(
                                $"agc.draw_packet op=0x{op:X2} count={indexCount}");
                        }
                    }
                }

                var indexed = op is
                    ItDrawIndex2 or
                    ItDrawIndexOffset2 or
                    ItDrawIndexIndirect or
                    ItDrawIndexIndirectMulti;
                state.SawIndexedDraw |= indexed;
                try
                {
                    TryTranslateGuestDraw(ctx, gpuState, state, indexCount, indexed);
                }
                finally
                {
                    state.CurrentIndexSnapshot = null;
                    state.CurrentVertexSnapshot = null;
                }
            }

            if (op == ItNop &&
                register == RDrawIndexAuto &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var autoIndexCount) &&
                autoIndexCount != 0)
            {
                state.FrameDrawCount++;
                TryTranslateGuestDraw(
                    ctx,
                    gpuState,
                    state,
                    autoIndexCount,
                    indexed: false);
            }

            if (op is ItDispatchDirect or ItDispatchIndirect)
            {
                if (TryReadComputeDispatch(
                        ctx,
                        state,
                        currentAddress,
                        length,
                        op,
                        out var dispatch,
                        out _))
                {
                    state.FrameDispatchCount++;
                    ObserveComputeDispatch(ctx, gpuState, state, dispatch);
                }
            }

            if (op == ItNop &&
                register == RWaitFlipDone &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var waitVideoOutHandle) &&
                TryReadUInt32(ctx, currentAddress + 8, out var waitDisplayBufferIndex))
            {
                var waitSequence = GuestGpu.Current.SubmitOrderedGuestFlipWait(
                    unchecked((int)waitVideoOutHandle),
                    unchecked((int)waitDisplayBufferIndex));
                TraceAgcShader(
                    $"agc.flip_wait_safe queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"handle={waitVideoOutHandle} index={waitDisplayBufferIndex} " +
                    $"work_sequence={waitSequence}");
            }

            if (op == ItNop && register == RFlip && length >= 6)
            {
                TraceFramePacketSummary(state);
                SyncCpuWrittenGuestImages(ctx);
                GpuWaitRegistry.AdvanceFrame();
                if (!TryReadUInt32(ctx, currentAddress + 4, out var videoOutHandle) ||
                    !TryReadUInt32(ctx, currentAddress + 8, out var displayBufferIndexRaw) ||
                    !TryReadUInt32(ctx, currentAddress + 12, out var flipMode) ||
                    !TryReadUInt32(ctx, currentAddress + 16, out var flipArgLo) ||
                    !TryReadUInt32(ctx, currentAddress + 20, out var flipArgHi))
                {
                    return false;
                }

                var flipArg = unchecked((long)(((ulong)flipArgHi << 32) | flipArgLo));
                var displayBufferIndex = unchecked((int)displayBufferIndexRaw);
                var handle = unchecked((int)videoOutHandle);
                if (state.PendingTargetlessDraw is { } pendingComposite &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var pendingDisplayBuffer) &&
                    state.KnownRenderTargets.TryGetValue(
                        pendingDisplayBuffer.Address,
                        out var pendingDisplayTarget))
                {
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        pendingComposite.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(pendingComposite);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(
                            pendingComposite.VertexInputs,
                            pendingComposite.VertexBufferBaseVertex);
                    ProvideRenderTargetInitialData(ctx, pendingDisplayTarget);
                    GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                        pendingComposite.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        pendingComposite.AttributeCount,
                        [CreateGuestRenderTarget(pendingDisplayTarget)],
                        pendingComposite.VertexShader,
                        pendingComposite.VertexCount,
                        pendingComposite.InstanceCount,
                        pendingComposite.PrimitiveType,
                        pendingComposite.IndexBuffer,
                        vertexBuffers,
                        pendingComposite.RenderState,
                        pendingComposite.DepthTarget,
                        pendingComposite.PixelShaderAddress,
                        pendingComposite.BaseVertex);
                    TraceAgcShader(
                        $"agc.deferred_composite ps=0x{pendingComposite.PixelShaderAddress:X16} " +
                        $"src=0x{pendingComposite.Textures.FirstOrDefault()?.Descriptor.Address ?? 0:X16} " +
                        $"dst=0x{pendingDisplayTarget.Address:X16} " +
                        $"size={pendingDisplayTarget.Width}x{pendingDisplayTarget.Height}");
                    state.PendingTargetlessDraw = null;
                    state.TranslatedDraw = null;
                }

                if (VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var cachedDisplayBuffer) &&
                    GuestGpu.Current.TrySubmitOrderedGuestImageFlip(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer.Address,
                        cachedDisplayBuffer.Width,
                        cachedDisplayBuffer.Height,
                        cachedDisplayBuffer.PitchInPixel))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer,
                        "gpu-cache");
                }
                else if (state.SawIndexedDraw &&
                    state.TranslatedDraw is { } translatedDraw &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var translatedDisplayBuffer))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        translatedDisplayBuffer,
                        "draw-fallback");
                    var textures = CreateGuestDrawTextures(ctx, translatedDraw.Textures, out var fallbackTextureCount);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffersForPresent(ctx, translatedDraw);
                    GuestGpu.Current.SubmitTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDisplayBuffer.Width,
                        translatedDisplayBuffer.Height,
                        translatedDraw.AttributeCount);
                    TraceAgcShader(
                        $"agc.shader_present ps=0x{translatedDraw.PixelShaderAddress:X16} " +
                        $"spirv={translatedDraw.PixelShader.Payload.Length} textures={textures.Count} " +
                        $"global_buffers={globalMemoryBuffers.Count} " +
                        $"fallback={fallbackTextureCount} {translatedDisplayBuffer.Width}x{translatedDisplayBuffer.Height}");

                    for (var i = 0; i < translatedDraw.Textures.Count; i++)
                    {
                        var binding = translatedDraw.Textures[i];
                        var d = binding.Descriptor;

                        TraceAgcShader(
                            $"agc.present_desc[{i}] " +
                            $"addr=0x{d.Address:X16} " +
                            $"size={d.Width}x{d.Height} " +
                            $"fmt={d.Format} " +
                            $"num={d.NumberType} " +
                            $"type={d.Type} " +
                            $"tile={d.TileMode} " +
                            $"storage={binding.IsStorage}");
                    }
                }
                else if (state.SawIndexedDraw && state.PresenterTexture is { } sourceTexture)
                {
                    _ = TrySoftwarePresent(
                        ctx,
                        sourceTexture,
                        unchecked((int)videoOutHandle),
                        displayBufferIndex);
                }
                else if (state.SawIndexedDraw &&
                         state.GuestDrawKind != GuestDrawKind.None &&
                         VideoOutExports.TryGetDisplayBufferInfo(
                             handle,
                             displayBufferIndex,
                             out var displayBuffer))
                {
                    GuestGpu.Current.SubmitGuestDraw(
                        state.GuestDrawKind,
                        displayBuffer.Width,
                        displayBuffer.Height);
                }

                // A SetFlip reached via the orphan force-submit path is the
                // game's own packet, physically shared with a real queue's
                // ring — force-submitting it can race ahead of that queue's
                // natural parse and present it twice once the real queue
                // catches up. Only a genuine game queue may flip.
                if (!state.IsForceSubmittedRing)
                {
                    _ = VideoOutExports.SubmitFlipFromAgc(ctx, handle, displayBufferIndex, unchecked((int)flipMode), flipArg);
                }

                state.SawIndexedDraw = false;
                state.GuestDrawKind = GuestDrawKind.None;
                if (state.PendingTargetlessDraw is { } unusedPendingDraw)
                {
                    ReturnPooledDrawArrays(
                        unusedPendingDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.PendingTargetlessDraw = null;
                }
                state.TranslatedDraw = null;
            }

            offset += length;
            state.LastParsedAddress = commandAddress + (ulong)offset * sizeof(uint);
        }

        FlushPendingAcquireInvalidation(ctx, state, tracePackets);
        return false;
    }

    private static void TraceFramePacketSummary(SubmittedDcbState state)
    {
        if (!_traceFramePackets)
        {
            return;
        }

        var flip = ++state.FlipCount;
        if (flip <= 8 || flip % 60 == 0 || state.FrameDrawCount == 0)
        {
            var opcodes = string.Join(
                ',',
                state.FramePacketCounts
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key.Op)
                    .Take(32)
                    .Select(entry => entry.Key.Register == uint.MaxValue
                        ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                        : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
            Console.Error.WriteLine(
                $"[FRAMEPKT] flip={flip} submission={state.ActiveSubmissionId} " +
                $"packets={state.FramePacketCount} draws={state.FrameDrawCount} " +
                $"dispatches={state.FrameDispatchCount} opcodes=[{opcodes}]");
        }

        state.FramePacketCounts.Clear();
        state.FramePacketCount = 0;
        state.FrameDrawCount = 0;
        state.FrameDispatchCount = 0;
    }

    private static void TracePacketParseFailure(
        SubmittedDcbState state,
        ulong address,
        uint offset,
        uint header,
        string reason)
    {
        if (!_traceFramePackets ||
            Interlocked.Increment(ref _packetParseFailureTraceCount) > 128)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[FRAMEPKT] parse-failure queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} offset={offset} " +
            $"address=0x{address:X16} header=0x{header:X8} reason={reason}");
    }

    private static void TraceDisplayBuffer(
        int handle,
        int index,
        VideoOutExports.DisplayBufferInfo buffer,
        string path)
    {
        lock (_submitTraceGate)
        {
            if (!_tracedDisplayBuffers.Add((handle, index, buffer.Address, path)))
            {
                return;
            }
        }

        TraceAgcShader(
            $"agc.display_buffer handle={handle} index={index} " +
            $"addr=0x{buffer.Address:X16} fmt=0x{buffer.PixelFormat:X16} " +
            $"tile={buffer.TilingMode} size={buffer.Width}x{buffer.Height} " +
            $"pitch={buffer.PitchInPixel} path={path}");
    }

    private static void ApplySubmittedDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool compactLayout,
        bool tracePacket)
    {
        var byteCountOffset = compactLayout ? 20UL : 12UL;
        var destinationOffset = compactLayout ? 4UL : 16UL;
        var sourceOffset = compactLayout ? 12UL : 24UL;
        var selectorOffset = compactLayout ? 24UL : 4UL;
        if (!TryReadUInt32(ctx, packetAddress + byteCountOffset, out var byteCount) ||
            !TryReadUInt64(ctx, packetAddress + destinationOffset, out var destinationAddress) ||
            !TryReadUInt64(ctx, packetAddress + sourceOffset, out var sourceAddress) ||
            !TryReadUInt32(ctx, packetAddress + selectorOffset, out var selectorControl))
        {
            return;
        }

        var immediateFill = IsWrappedDmaGuestMemoryFill(compactLayout, selectorControl);
        if (immediateFill)
        {
            RegisterActiveHtile(gpuState, state.CxRegisters);
            MarkHtileMetadataClear(
                gpuState,
                destinationAddress,
                "agc-dma-fill");
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
                var copied =
                    byteCount != 0 &&
                    byteCount <= 256u * 1024u * 1024u &&
                    destinationAddress != 0 &&
                    (immediateFill
                        ? TryFillGuestMemory(ctx, (uint)sourceAddress, destinationAddress, byteCount)
                        : sourceAddress != 0 &&
                          TryCopyGuestMemory(ctx, sourceAddress, destinationAddress, byteCount));
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(
                        ctx,
                        destinationAddress,
                        byteCount,
                        immediateFill ? (uint)sourceAddress : null);
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.dma_data dst=0x{destinationAddress:X16} " +
                        $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                        $"fill={immediateFill} copied={copied}");
                }
            },
            $"agc_dma_data dst=0x{destinationAddress:X16} bytes={byteCount}",
            packetAddress,
            destinationAddress,
            byteCount,
            deferLabelCompletion: true);
    }

    internal static bool IsWrappedDmaGuestMemoryFill(
        bool compactLayout,
        uint selectorControl)
    {
        var sourceSelector = compactLayout
            ? selectorControl & 0xFFu
            : (selectorControl >> 16) & 0xFFu;
        var destinationSelector = compactLayout
            ? (selectorControl >> 8) & 0xFFu
            : selectorControl & 0xFFu;
        return sourceSelector == 2 && destinationSelector is 0 or 3;
    }

    private static bool PacketRequiresPendingAcquireFlush(
        uint op,
        uint register,
        uint length) =>
        op is ItDispatchDirect or ItDispatchIndirect ||
        op is ItDrawIndirect or
            ItDrawIndexIndirect or
            ItDrawIndexIndirectMulti or
            ItDrawIndex2 or
            ItDrawIndexAuto or
            ItDrawIndexMultiAuto or
            ItDrawIndexOffset2 ||
        op is ItMemSemaphore or ItCopyData ||
        op == ItDmaData ||
        (op == ItNop && register == RDmaData && length >= 7) ||
        (op == ItNop && register == RFlip && length >= 6) ||
        (op == ItNop && register == RDrawIndexAuto && length >= 2) ||
        (op == ItNop && register == RWaitFlipDone && length >= 3);

    private static void SubmitOrderedGpuSideEffect(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        Action action,
        string debugName,
        ulong packetAddress,
        ulong producerAddress = 0,
        ulong producerLength = 0,
        bool deferLabelCompletion = false)
    {
        var producer = RegisterLabelProducer(
            ctx.Memory,
            state,
            packetAddress,
            producerAddress,
            producerLength,
            debugName);

        void CompleteAndWake()
        {
            CompleteLabelProducer(producer);
            lock (gpuState.WaitMonitorSignalGate)
            {
                gpuState.WaitMonitorSignalVersion++;
                Monitor.Pulse(gpuState.WaitMonitorSignalGate);
            }

            // Resuming a DCB can enqueue and wait for another dispatch, never
            // reentrantly on the Vulkan render thread. Drains are coalesced —
            // hundreds of completions per frame each queueing an independent
            // full drain turns the shared Gate into a thundering herd — via
            // one request flag and a single re-looping worker.
            RequestResumableDcbDrain(ctx, gpuState);
        }

        void ApplyAndQueueCompletion()
        {
            action();
            // No label producer → nothing to wake; skip the follow-up enqueue
            // that was doubling OrderedGuestAction traffic during load.
            if (producer is null)
            {
                CompleteAndWake();
                return;
            }

            // Release/write-data paths cannot enqueue Vulkan image mirrors, so
            // complete the producer in the same ordered action. DMA can enqueue
            // a mirror while applying; defer completion until after those
            // follow-ups so waiters see the mirrored image.
            if (!deferLabelCompletion)
            {
                CompleteAndWake();
                return;
            }

            if (GuestGpu.Current.SubmitOrderedGuestAction(
                    CompleteAndWake,
                    $"{debugName} completion") == 0)
            {
                CompleteAndWake();
            }
        }

        if (GuestGpu.Current.SubmitOrderedGuestAction(
                ApplyAndQueueCompletion,
                debugName) == 0)
        {
            // Headless/startup submissions have no Vulkan queue to order
            // against, so retaining the previous immediate behavior is exact.
            ApplyAndQueueCompletion();
        }
    }

    private static void RequestResumableDcbDrain(CpuContext ctx, SubmittedGpuState gpuState)
    {
        Volatile.Write(ref gpuState.PendingDrainContext, ctx);
        Interlocked.Exchange(ref gpuState.DrainPending, 1);
        if (Interlocked.CompareExchange(ref gpuState.DrainWorkerActive, 1, 0) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => RunResumableDcbDrainWorker(state),
                gpuState,
                preferLocal: false);
        }
    }

    private static void RunResumableDcbDrainWorker(SubmittedGpuState gpuState)
    {
        while (true)
        {
            Interlocked.Exchange(ref gpuState.DrainPending, 0);
            if (Volatile.Read(ref gpuState.PendingDrainContext) is { } drainContext)
            {
                lock (gpuState.Gate)
                {
                    DrainResumableDcbs(drainContext, gpuState, tracePackets: _traceAgc);
                }
            }

            if (Volatile.Read(ref gpuState.DrainPending) != 0)
            {
                continue;
            }

            Volatile.Write(ref gpuState.DrainWorkerActive, 0);
            // A request may have slipped in between the pending check and the
            // hand-back; re-claim the duty unless another worker already did.
            if (Volatile.Read(ref gpuState.DrainPending) == 0 ||
                Interlocked.CompareExchange(ref gpuState.DrainWorkerActive, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private static LabelProducerTrace? RegisterLabelProducer(
        object memory,
        SubmittedDcbState state,
        ulong packetAddress,
        ulong address,
        ulong length,
        string debugName)
    {
        if (address == 0 || length == 0)
        {
            return null;
        }

        memory = CanonicalMemory(memory);
        var producer = new LabelProducerTrace
        {
            Sequence = Interlocked.Increment(ref _labelProducerSequence),
            Memory = memory,
            Address = address,
            Length = length,
            PacketAddress = packetAddress,
            SubmissionId = state.ActiveSubmissionId,
            QueueName = state.QueueName,
            DebugName = debugName,
        };
        lock (_labelProducerGate)
        {
            if (_labelProducers.Count >= _labelProducerCompactionBound)
            {
                // Active producer records are synchronization state, not a
                // diagnostic cache. Removing one can hide an earlier
                // same-submission label write and make a valid in-stream fence
                // suspend forever. Compact only completed history; if all
                // records are active, correctness takes precedence over the
                // soft diagnostic bound.
                var removed = CompactCompletedEntries(
                    _labelProducers,
                    static candidate => candidate.Completed,
                    targetCount: LabelProducerSoftBound * 3 / 4);
                _labelProducerCompactionBound = removed == 0
                    ? _labelProducers.Count * 2
                    : LabelProducerSoftBound;
            }

            _labelProducers.Add(producer);
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(memory, address, length))
            {
                TraceAgc(
                    $"agc.wait_producer_scheduled label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"packet=0x{packetAddress:X16} action='{debugName}'");
            }
        }

        return producer;
    }

    internal static int CompactCompletedEntries<T>(
        List<T> entries,
        Func<T, bool> isCompleted,
        int targetCount)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(isCompleted);
        targetCount = Math.Max(0, targetCount);

        // Single order-preserving pass. Removing one-by-one would shift the
        // tail on every eviction, which is quadratic on a list this size and
        // runs while the label gate is held.
        var removable = entries.Count - targetCount;
        var removed = 0;
        var write = 0;
        for (var read = 0; read < entries.Count; read++)
        {
            if (removed < removable && isCompleted(entries[read]))
            {
                removed++;
                continue;
            }

            entries[write++] = entries[read];
        }

        entries.RemoveRange(write, entries.Count - write);
        return removed;
    }

    private static void CompleteLabelProducer(LabelProducerTrace? producer)
    {
        if (producer is null)
        {
            return;
        }

        lock (_labelProducerGate)
        {
            producer.Completed = true;
        }

        if (_traceAgc)
        {
            foreach (var waiting in GpuWaitRegistry.SnapshotInRange(
                         producer.Memory,
                         producer.Address,
                         producer.Length))
            {
                TraceAgc(
                    $"agc.wait_producer_completed label=0x{waiting.Address:X16} " +
                    $"waiters={waiting.Count} producer_seq={producer.Sequence} " +
                    $"queue={producer.QueueName} submission={producer.SubmissionId} " +
                    $"action='{producer.DebugName}'");
            }
        }
    }

    private static void TraceWaitProducerState(
        object memory,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong commandAddress,
        ulong packetAddress,
        bool stale,
        ulong? currentValue = null)
    {
        memory = CanonicalMemory(memory);
        LabelProducerTrace? producer = null;
        lock (_labelProducerGate)
        {
            for (var index = _labelProducers.Count - 1; index >= 0; index--)
            {
                var candidate = _labelProducers[index];
                if (!ReferenceEquals(candidate.Memory, memory) ||
                    !RangesOverlap(
                        candidate.Address,
                        candidate.Length,
                        waiter.WaitAddress,
                        waiter.Is64Bit ? (ulong)sizeof(ulong) : sizeof(uint)))
                {
                    continue;
                }

                producer = candidate;
                break;
            }

            if (_tracedProducerlessWaits.Count >= 4096)
            {
                _tracedProducerlessWaits.Clear();
            }

            if (!stale)
            {
                // Count before the deduplication below: the warning fires once
                // per label, so on its own it cannot say how often a queue
                // actually suspends.
                GpuWaitProfile.RecordSuspend(producer is not null);
            }

            if (!stale && producer is null &&
                !_tracedProducerlessWaits.Add(
                    (memory, waiter.WaitAddress)))
            {
                return;
            }
        }

        // Producer-backed waits are trace-only. Keep the producer lookup above
        // because producerless waits are always warned, but do not build the
        // detailed condition strings when AGC tracing is disabled.
        if (producer is not null && !_traceAgc)
        {
            return;
        }

        var prefix = stale ? "agc.wait_stale" : "agc.wait_suspended";
        var current = currentValue.HasValue
            ? $"0x{currentValue.Value:X16}"
            : "unreadable";
        var condition =
            $"value={current} mask=0x{waiter.Mask:X16} " +
            $"ref=0x{waiter.ReferenceValue:X16} cmp={waiter.CompareFunction} " +
            $"control=0x{waiter.ControlValue:X8} bits={(waiter.Is64Bit ? 64 : 32)} " +
            $"form={(waiter.IsStandard ? "standard" : "agc-nop")}";
        if (producer is null)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] {prefix} label=0x{waiter.WaitAddress:X16} " +
                $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
                $"command=0x{commandAddress:X16} packet=0x{packetAddress:X16} " +
                condition + " " +
                "producer=none-observed; remaining-suspended");
            return;
        }

        TraceAgc(
            $"{prefix} label=0x{waiter.WaitAddress:X16} " +
            $"queue={waiter.QueueName} submission={waiter.SubmissionId} " +
            condition + " " +
            $"producer_seq={producer.Sequence} producer_state=" +
            $"{(producer.Completed ? "completed" : "queued")} " +
            $"producer_queue={producer.QueueName} " +
            $"producer_submission={producer.SubmissionId} " +
            $"producer_packet=0x{producer.PacketAddress:X16} " +
            $"action='{producer.DebugName}'");
    }

    private static void ApplySubmittedAcquireMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryDecodeSubmittedAcquireMem(ctx, packetAddress, out var acquire))
        {
            TraceAgc(
                $"agc.acquire_mem_decode_failed queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16}");
            return;
        }

        // The bulk PM4 read is itself a parser-side cache. Do not retain it
        // across a guest cache-invalidation point.
        DropCurrentDcbWindow();

        if (!acquire.InvalidatesGuestResources)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.acquire_mem_skip_no_invalidate queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
                    $"gcr=0x{acquire.GcrControl:X8}");
            }

            return;
        }

        var size = acquire.CoversAllGuestMemory ? ulong.MaxValue : acquire.SizeBytes;
        NotePendingAcquireInvalidation(state, acquire.BaseAddress, size);

        if (tracePacket)
        {
            TraceAgc(
                $"agc.acquire_mem_coalesce queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
                $"engine={acquire.Engine} cbdb=0x{acquire.CbDbControl:X8} " +
                $"base=0x{acquire.BaseAddress:X16} size=0x{acquire.SizeBytes:X16} " +
                $"scope={(acquire.CoversAllGuestMemory ? "all" : "range")} " +
                $"poll={acquire.PollInterval} gcr=0x{acquire.GcrControl:X8} " +
                $"pending_base=0x{state.PendingAcquireBase:X16} " +
                $"pending_size=0x{state.PendingAcquireSize:X16}");
        }
    }

    private static void NotePendingAcquireInvalidation(
        SubmittedDcbState state,
        ulong baseAddress,
        ulong sizeBytes)
    {
        if (!state.PendingAcquireInvalidation)
        {
            state.PendingAcquireInvalidation = true;
            state.PendingAcquireBase = baseAddress;
            state.PendingAcquireSize = sizeBytes;
            return;
        }

        if (state.PendingAcquireSize == ulong.MaxValue || sizeBytes == ulong.MaxValue)
        {
            state.PendingAcquireBase = 0;
            state.PendingAcquireSize = ulong.MaxValue;
            return;
        }

        var existingEnd = state.PendingAcquireBase > ulong.MaxValue - state.PendingAcquireSize
            ? ulong.MaxValue
            : state.PendingAcquireBase + state.PendingAcquireSize;
        var newEnd = baseAddress > ulong.MaxValue - sizeBytes
            ? ulong.MaxValue
            : baseAddress + sizeBytes;
        var mergedBase = Math.Min(state.PendingAcquireBase, baseAddress);
        var mergedEnd = Math.Max(existingEnd, newEnd);
        state.PendingAcquireBase = mergedBase;
        state.PendingAcquireSize = mergedEnd == ulong.MaxValue
            ? ulong.MaxValue
            : mergedEnd - mergedBase;
    }

    private static void FlushPendingAcquireInvalidation(
        CpuContext ctx,
        SubmittedDcbState state,
        bool tracePacket)
    {
        if (!state.PendingAcquireInvalidation)
        {
            return;
        }

        var baseAddress = state.PendingAcquireBase;
        var sizeBytes = state.PendingAcquireSize;
        state.PendingAcquireInvalidation = false;
        state.PendingAcquireBase = 0;
        state.PendingAcquireSize = 0;

        if (_skipNoopAcquireMem && !SharpEmu.HLE.GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var queueName = state.QueueName;
        var submissionId = state.ActiveSubmissionId;
        var debugName =
            $"acquire_mem_flush base=0x{baseAddress:X16} size=0x{sizeBytes:X16}";
        void ApplyAcquire()
        {
            SyncCpuWrittenGuestImages(ctx, baseAddress, sizeBytes);
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.acquire_mem_applied queue={queueName} " +
                    $"submission={submissionId} " +
                    $"work_sequence={GuestGpu.Current.CurrentGuestWorkSequenceForDiagnostics} " +
                    $"base=0x{baseAddress:X16} size=0x{sizeBytes:X16}");
            }
        }

        var sequence = GuestGpu.Current.SubmitOrderedGuestAction(ApplyAcquire, debugName);
        if (sequence == 0)
        {
            ApplyAcquire();
        }
    }

    private static bool TryDecodeSubmittedAcquireMem(
        CpuContext ctx,
        ulong packetAddress,
        out SubmittedAcquireMem acquire)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var coherControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sizeLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sizeHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var baseLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var baseHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var pollInterval) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var gcrControl))
        {
            acquire = default;
            return false;
        }

        acquire = DecodeSubmittedAcquireMem(
            coherControl,
            sizeLow,
            sizeHigh,
            baseLow,
            baseHigh,
            pollInterval,
            gcrControl);
        return true;
    }

    private static SubmittedAcquireMem DecodeSubmittedAcquireMem(
        uint coherControl,
        uint sizeLow,
        uint sizeHigh,
        uint baseLow,
        uint baseHigh,
        uint pollInterval,
        uint gcrControl)
    {
        // GFX10 ACQUIRE_MEM expresses COHER_SIZE and COHER_BASE in 256-byte
        // units. SIZE_HI is 8 bits and BASE_HI is 24 bits in the packet.
        var sizeUnits = sizeLow | ((ulong)(sizeHigh & 0xFFu) << 32);
        var baseUnits = baseLow | ((ulong)(baseHigh & 0x00FF_FFFFu) << 32);
        return new SubmittedAcquireMem(
            Engine: coherControl >> 31,
            CbDbControl: coherControl & 0x7FFF_FFFFu,
            BaseAddress: baseUnits << 8,
            SizeBytes: sizeUnits << 8,
            PollInterval: pollInterval & 0xFFFFu,
            GcrControl: gcrControl & 0x7FFFFu);
    }

    private static void ResetSubmittedParserState(SubmittedDcbState state)
    {
        // Queue ownership, pending submissions and suspension bookkeeping are
        // deliberately retained. Work emitted before this packet already owns
        // immutable snapshots; clearing these fields affects only commands
        // translated after RESET at this precise packet position.
        state.CxRegisters.Clear();
        state.ShRegisters.Clear();
        state.UcRegisters.Clear();
        state.PresenterTexture = null;
        state.GuestDrawKind = GuestDrawKind.None;
        state.TranslatedDraw = null;
        state.RenderTargetWriters.Clear();
        state.IndirectArgsAddress = 0;
        state.SawIndexedDraw = false;
        state.IndexBufferAddress = 0;
        state.IndexBufferCount = 0;
        state.IndexSize = 0;
        state.InstanceCount = 1;
        state.DrawIndexOffset = 0;
    }

    private static void ApplySubmittedPredication(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool tracePacket)
    {
        if (packetLength < 3 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var first) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var second))
        {
            return;
        }

        const uint flagsMask = 0x0007_1100u;
        uint flags;
        ulong predicateAddress;
        if (packetLength >= 4 &&
            (first & ~flagsMask) == 0 &&
            TryReadUInt32(ctx, packetAddress + 12, out var third) &&
            third <= 0xFFFFu)
        {
            flags = first;
            predicateAddress = ((ulong)third << 32) | (second & 0xFFFF_FFF0u);
        }
        else
        {
            flags = second;
            predicateAddress = (first & 0xFFFF_FFF0u) | ((ulong)(second & 0xFFu) << 32);
        }

        var operation = (flags >> 16) & 0x7u;
        if (operation == 0)
        {
            state.PredicateSkip = false;
            return;
        }

        if (operation != 3)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.predication_unsupported packet=0x{packetAddress:X16} " +
                    $"op={operation} addr=0x{predicateAddress:X16}");
            }

            return;
        }

        var waitOperation = (flags >> 12) & 1u;
        var value = 0UL;
        var readSucceeded = false;
        void ReadPredicate() =>
            readSucceeded = ctx.TryReadUInt64(predicateAddress, out value);

        if (waitOperation != 0)
        {
            var sequence = GuestGpu.Current.SubmitOrderedGuestAction(
                ReadPredicate,
                $"set_predication read 0x{predicateAddress:X16}");
            if (sequence == 0)
            {
                ReadPredicate();
            }
            else if (!GuestGpu.Current.WaitForGuestWork(sequence))
            {
                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.predication_wait_failed packet=0x{packetAddress:X16} " +
                        $"addr=0x{predicateAddress:X16} sequence={sequence}");
                }

                return;
            }
        }
        else
        {
            ReadPredicate();
        }

        if (!readSucceeded)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.predication_read_failed packet=0x{packetAddress:X16} " +
                    $"addr=0x{predicateAddress:X16}");
            }

            return;
        }

        var condition = (flags >> 8) & 1u;
        state.PredicateSkip = condition == 0 ? value != 0 : value == 0;
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.predication packet=0x{packetAddress:X16} " +
                $"addr=0x{predicateAddress:X16} value=0x{value:X16} " +
                $"condition={condition} wait={waitOperation} skip={state.PredicateSkip}");
        }
    }

    private static bool RangesOverlap(
        ulong leftAddress,
        ulong leftLength,
        ulong rightAddress,
        ulong rightLength)
    {
        var leftEnd = leftAddress > ulong.MaxValue - leftLength
            ? ulong.MaxValue
            : leftAddress + leftLength;
        var rightEnd = rightAddress > ulong.MaxValue - rightLength
            ? ulong.MaxValue
            : rightAddress + rightLength;
        return leftAddress < rightEnd && rightAddress < leftEnd;
    }

    /// <summary>
    /// Mirrors guest-side DMA/CPU writes to a render target's surface into
    /// our separate Vulkan image once per flip (Dreaming Sarah's fog layer,
    /// Chowdren's fog-noise memset). Surfaces only the GPU writes are skipped.
    /// </summary>
    private static void SyncCpuWrittenGuestImages(
        CpuContext ctx,
        ulong scopeAddress = 0,
        ulong scopeByteCount = ulong.MaxValue)
    {
        // Uploads used to copy full planes here and SubmitGuestImageWrite on the
        // AGC producer thread, which hit the payload guest-work caps and
        // soft-locked titles (GTA). The presenter's render drain owns the
        // read/upload/re-arm; this call is only a scoped wake.
        _ = ctx;
        if (!SharpEmu.HLE.GuestImageWriteTracker.Enabled || scopeByteCount == 0)
        {
            return;
        }

        GuestGpu.Current.RequestCpuWrittenGuestImageSync(scopeAddress, scopeByteCount);
    }

    private static long _dmaMirrorTraceCount;
    private static readonly Dictionary<(uint Op, uint Register), long> _submittedOpcodeCounts = new();
    private static long _submittedOpcodeTotal;

    private static void CountSubmittedOpcode(uint op, uint register)
    {
        var key = (op, op == ItNop ? register : uint.MaxValue);
        lock (_submittedOpcodeCounts)
        {
            _submittedOpcodeCounts[key] =
                _submittedOpcodeCounts.TryGetValue(key, out var count) ? count + 1 : 1;
            if (++_submittedOpcodeTotal % 500_000 == 0)
            {
                var summary = string.Join(
                    ' ',
                    _submittedOpcodeCounts
                        .OrderByDescending(entry => entry.Value)
                        .Select(entry => entry.Key.Register == uint.MaxValue
                            ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                            : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
                Console.Error.WriteLine($"[PKT] total={_submittedOpcodeTotal} {summary}");
            }
        }
    }

    private static void MirrorDmaWriteToGuestImage(
        CpuContext ctx,
        ulong destinationAddress,
        ulong byteCount,
        uint? fillValue)
    {
        // Check if this DMA write targets a CMASK address (shadPS4's FillBuffer
        // logic: when a buffer fill targets CMASK metadata, mark it as "all clear")
        if (fillValue is { } fillVal && fillVal == 0)
        {
            CheckCmaskWrite(destinationAddress, null);
        }

        var hasImage = GuestGpu.Current.TryGetGuestImageExtent(
            destinationAddress,
            out var width,
            out var height,
            out var imageBytes);
        if (_traceDraws && Interlocked.Increment(ref _dmaMirrorTraceCount) <= 400)
        {
            Console.Error.WriteLine(
                $"[DMA] dst=0x{destinationAddress:X} bytes={byteCount} " +
                $"fill={(fillValue is { } f ? $"0x{f:X8}" : "copy")} image={hasImage}");
        }

        if (!hasImage)
        {
            return;
        }

        if (imageBytes == 0 || byteCount < imageBytes)
        {
            return;
        }

        if (fillValue is { } fill)
        {
            GuestGpu.Current.SubmitGuestImageFill(destinationAddress, fill);
            return;
        }

        var pixels = new byte[imageBytes];
        if (ctx.Memory.TryRead(destinationAddress, pixels))
        {
            GuestGpu.Current.SubmitGuestImageWrite(destinationAddress, pixels);
        }
    }

    private static void ApplySubmittedStandardDmaData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var sourceLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var sourceHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationLow) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var destinationHigh) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var command))
        {
            return;
        }

        var byteCount = command & 0x1F_FFFFu;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var sourceSelect = (control >> 29) & 0x3u;
        var sourceAddressIncrement = (command >> 28) & 0x1u;
        var destinationAddress = destinationLow | ((ulong)destinationHigh << 32);
        var writesGuestMemory =
            byteCount != 0 &&
            destinationSwap == 0 &&
            destinationSelect is 0 or 3 &&
            (destinationSelect == 3 || destinationAddressSpace == 0);
        var fillsGuestMemory =
            sourceSelect == 2 ||
            (sourceSelect is 0 or 3 && sourceAddressIncrement != 0);
        if (writesGuestMemory && fillsGuestMemory)
        {
            RegisterActiveHtile(gpuState, state.CxRegisters);
            MarkHtileMetadataClear(
                gpuState,
                destinationAddress,
                "dma-fill");
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () => ApplySubmittedStandardDmaDataSnapshot(
                ctx,
                control,
                sourceLow,
                sourceHigh,
                destinationLow,
                destinationHigh,
                command),
            $"dma_data dst=0x{destinationHigh:X8}{destinationLow:X8} bytes={byteCount}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? byteCount : 0,
            deferLabelCompletion: true);
    }

    private static void ApplySubmittedStandardDmaDataSnapshot(
        CpuContext ctx,
        uint control,
        uint sourceLow,
        uint sourceHigh,
        uint destinationLow,
        uint destinationHigh,
        uint command)
    {
        var byteCount = command & 0x1F_FFFFu;
        var sourceSelect = (control >> 29) & 0x3u;
        var destinationSelect = (control >> 20) & 0x3u;
        var destinationSwap = (command >> 24) & 0x3u;
        var sourceAddressSpace = (command >> 26) & 0x1u;
        var destinationAddressSpace = (command >> 27) & 0x1u;
        var sourceAddressIncrement = (command >> 28) & 0x1u;
        if (byteCount == 0 ||
            destinationSwap != 0 ||
            destinationSelect is not (0 or 3) ||
            (destinationSelect == 0 && destinationAddressSpace != 0))
        {
            return;
        }

        var destinationAddress =
            destinationLow | ((ulong)destinationHigh << 32);
        InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
        bool copied;
        ulong sourceAddress;
        if (sourceSelect is 0 or 3 &&
            (sourceSelect == 3 || sourceAddressSpace == 0))
        {
            sourceAddress = sourceLow | ((ulong)sourceHigh << 32);
            if (sourceAddressIncrement != 0)
            {
                copied =
                    TryReadUInt32(ctx, sourceAddress, out var fillValue) &&
                    TryFillGuestMemory(
                        ctx,
                        fillValue,
                        destinationAddress,
                        byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, fillValue);
                }
            }
            else
            {
                copied = TryCopyGuestMemory(
                    ctx,
                    sourceAddress,
                    destinationAddress,
                    byteCount);
                if (copied)
                {
                    MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, fillValue: null);
                }
            }
        }
        else if (sourceSelect == 2)
        {
            sourceAddress = 0;
            copied = TryFillGuestMemory(
                ctx,
                sourceLow,
                destinationAddress,
                byteCount);
            if (copied)
            {
                MirrorDmaWriteToGuestImage(ctx, destinationAddress, byteCount, sourceLow);
            }
        }
        else
        {
            return;
        }

        if (ShouldTraceHotPath(ref _standardDmaTraceCount))
        {
            TraceAgcShader(
                $"agc.dma_packet dst=0x{destinationAddress:X16} " +
                $"src=0x{sourceAddress:X16} bytes={byteCount} " +
                $"src_sel={sourceSelect} fill={sourceAddressIncrement != 0 || sourceSelect == 2} " +
                $"copied={copied}");
        }
    }

    private static void ApplySubmittedWriteData(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        bool standardPacket,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var destinationAddress))
        {
            return;
        }

        var (destination, incrementAddress, writeConfirm, cachePolicy) = standardPacket
            ? DecodeStandardWriteDataControl(control)
            : DecodeAgcWriteDataControl(control);
        RecordFenceWritePacketSite(packetAddress, destinationAddress);
        var dwordCount = packetLength - 4;
        var values = new uint[dwordCount];
        for (uint index = 0; index < dwordCount; index++)
        {
            var sourceAddress = packetAddress + 16 + ((ulong)index * sizeof(uint));
            if (!TryReadUInt32(ctx, sourceAddress, out values[index]))
            {
                return;
            }
        }

        if (TrySubmitGpuOnlyWriteDataLabel(
                ctx,
                gpuState,
                state,
                packetAddress,
                destinationAddress,
                destination,
                incrementAddress,
                writeConfirm,
                cachePolicy,
                values,
                tracePacket))
        {
            return;
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(
                    destinationAddress,
                    incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint));
                var wroteData = destination is 1 or 2 or 4 or 5;
                for (uint index = 0; wroteData && index < dwordCount; index++)
                {
                    var targetAddress = destinationAddress +
                        (incrementAddress ? (ulong)index * sizeof(uint) : 0);
                    wroteData = TryWriteUInt32(ctx, targetAddress, values[index]);
                    if (wroteData)
                    {
                        GpuWaitRegistry.RecordProduced(
                            ctx.Memory, targetAddress, values[index]);
                    }
                }

                // Like ReleaseMem dataSel=2: a 64-bit WAIT_REG_MEM watches an
                // 8-byte label written as two 32-bit dwords. Record the combined
                // 64-bit value so a 64-bit EQ can latch even though the writes
                // landed as two 32-bit stores.
                if (wroteData && dwordCount >= 2 && incrementAddress)
                {
                    var combined = ((ulong)values[1] << 32) | values[0];
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory, destinationAddress, combined);
                    // Also latch the high half's address for symmetry: a stray
                    // 32-bit wait on the high dword should not be confused, but
                    // recording it does not hurt and mirrors the per-dword stores.
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.write_data dst={destination} " +
                        $"addr=0x{destinationAddress:X16} count={dwordCount} " +
                        $"increment={incrementAddress} confirm={writeConfirm} " +
                        $"cache={cachePolicy} standard={standardPacket} wrote={wroteData}");
                }
            },
            $"write_data dst=0x{destinationAddress:X16} count={dwordCount}",
            packetAddress,
            destination is 1 or 2 or 4 or 5 ? destinationAddress : 0,
            destination is 1 or 2 or 4 or 5
                ? incrementAddress ? (ulong)dwordCount * sizeof(uint) : sizeof(uint)
                : 0);
    }

    private static bool TrySubmitGpuOnlyWriteDataLabel(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        ulong destinationAddress,
        uint destination,
        bool incrementAddress,
        bool writeConfirm,
        uint cachePolicy,
        uint[] values,
        bool tracePacket)
    {
        if (!TryClassifyGpuLabelWrite(
                state.QueueName,
                destination,
                incrementAddress,
                writeConfirm,
                cachePolicy,
                out var producerEngine) ||
            values.Length == 0)
        {
            return false;
        }

        var byteCount = checked((ulong)values.Length * sizeof(uint));
        var debugName =
            $"gpu_label dst=0x{destinationAddress:X16} count={values.Length}";
        var producer = RegisterLabelProducer(
            ctx.Memory,
            state,
            packetAddress,
            destinationAddress,
            byteCount,
            debugName);

        void Publish(GuestGpuLabelDependency dependency)
        {
            GpuWaitRegistry.RecordVirtualProducedRange(
                ctx.Memory,
                destinationAddress,
                values,
                dependency,
                cachePolicy,
                producerEngine);

            CompleteLabelProducer(producer);
            lock (gpuState.WaitMonitorSignalGate)
            {
                gpuState.WaitMonitorSignalVersion++;
                Monitor.Pulse(gpuState.WaitMonitorSignalGate);
            }

            RequestResumableDcbDrain(ctx, gpuState);
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.write_data_gpu_label dst={destination} " +
                    $"addr=0x{destinationAddress:X16} count={values.Length} " +
                    $"queue={state.QueueName} dependency=" +
                    $"g{dependency.GraphicsTimeline}/c{dependency.ComputeTimeline}");
            }
        }

        void PublishHost()
        {
            for (var index = 0; index < values.Length; index++)
            {
                TryWriteUInt32(
                    ctx,
                    destinationAddress + ((ulong)index * sizeof(uint)),
                    values[index]);
            }

            // Invalidate after the full write. A parser that drops its cached
            // window must only fall back after the new value is in memory.
            InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
        }

        if (GuestGpu.Current.SubmitGpuLabelSignal(
                Publish,
                _gpuLabelHostMirrorEnabled ? PublishHost : null,
                debugName) != 0)
        {
            return true;
        }

        // The backend cannot keep this label on the GPU. Remove the trace
        // producer and let the existing CPU-visible ordered action handle it.
        CompleteLabelProducer(producer);
        return false;
    }

    internal static bool TryClassifyGpuLabelWrite(
        string queueName,
        uint destination,
        bool incrementAddress,
        bool writeConfirm,
        uint cachePolicy,
        out GpuWaitRegistry.VirtualLabelEngine engine)
    {
        var computeQueue = queueName.StartsWith("acb.", StringComparison.Ordinal);
        engine = computeQueue
            ? GpuWaitRegistry.VirtualLabelEngine.Mec
            : destination == 5u
                ? GpuWaitRegistry.VirtualLabelEngine.Me
                : GpuWaitRegistry.VirtualLabelEngine.Pfp;

        return cachePolicy <= 2 &&
            incrementAddress &&
            writeConfirm &&
            (computeQueue ? destination == 2u : destination is 4u or 5u);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeStandardWriteDataControl(uint control)
    {
        // GFX10 PKT3_WRITE_DATA is not byte-packed like sceAgcDcbWriteData's
        // NOP wrapper: DST_SEL is 11:8, ADDR_INCR is bit 16 (0 increments),
        // WR_CONFIRM is bit 20, and CACHE_POLICY is 26:25. In particular, the
        // low byte is reserved and must never be interpreted as DST_SEL.
        return (
            Destination: (control >> 8) & 0xFu,
            IncrementAddress: (control & (1u << 16)) == 0,
            WriteConfirm: (control & (1u << 20)) != 0,
            CachePolicy: (control >> 25) & 0x3u);
    }

    private static (uint Destination, bool IncrementAddress, bool WriteConfirm, uint CachePolicy)
        DecodeAgcWriteDataControl(uint control) =>
        (
            Destination: control & 0xFFu,
            IncrementAddress: ((control >> 16) & 0xFFu) == 0,
            WriteConfirm: ((control >> 24) & 0xFFu) != 0,
            CachePolicy: (control >> 8) & 0xFFu);

#if DEBUG
    private static void ValidateWriteDataControlDecoders()
    {
        // Regression vector: reserved low-byte noise previously decoded 0xA5
        // as DST_SEL, causing a valid standard memory write to be discarded.
        const uint standardControl = 0xA5u | (5u << 8) | (1u << 16) | (1u << 20) | (2u << 25);
        var standard = DecodeStandardWriteDataControl(standardControl);
        System.Diagnostics.Debug.Assert(standard.Destination == 5u);
        System.Diagnostics.Debug.Assert(!standard.IncrementAddress);
        System.Diagnostics.Debug.Assert(standard.WriteConfirm);
        System.Diagnostics.Debug.Assert(standard.CachePolicy == 2u);

        const uint agcControl = 4u | (3u << 8) | (1u << 24);
        var agc = DecodeAgcWriteDataControl(agcControl);
        System.Diagnostics.Debug.Assert(agc.Destination == 4u);
        System.Diagnostics.Debug.Assert(agc.IncrementAddress);
        System.Diagnostics.Debug.Assert(agc.WriteConfirm);
        System.Diagnostics.Debug.Assert(agc.CachePolicy == 3u);
    }

    private static void ValidateDispatchInitiators()
    {
        const uint threadCount = 0x00F0_0100u;
        const uint localSize = 64u;
        var initiator = DirectDispatchInitiator(0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 5)) == 0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 6)) != 0);
        System.Diagnostics.Debug.Assert(threadCount * localSize == 0x3C00_4000u);
        System.Diagnostics.Debug.Assert(CeilDivide(20, 8) == 3);
        System.Diagnostics.Debug.Assert(CeilDivide(12, 8) == 2);
    }

    private static void ValidateSubmittedQueueAndReleaseMemDecoders()
    {
        var nggRegisters = new Dictionary<uint, uint>
        {
            [GsUserDataRegister - 1] = 3u << 1,
        };
        System.Diagnostics.Debug.Assert(
            SelectExportUserDataRegister(nggRegisters) == GsUserDataRegister);

        var queue = new SubmittedDcbState();
        queue.PendingSubmissions.Enqueue(new(0x1000, 8, 11, false));
        queue.PendingSubmissions.Enqueue(new(0x2000, 16, 12, true));
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 11);
        System.Diagnostics.Debug.Assert(
            queue.PendingSubmissions.Dequeue().SubmissionId == 12);

        var control = (1u << 16) | (2u << 29);
        var decoded = DecodeStandardReleaseMemControl(control);
        System.Diagnostics.Debug.Assert(decoded.Destination == 1u);
        System.Diagnostics.Debug.Assert(decoded.DataSelection == 2u);
        System.Diagnostics.Debug.Assert(
            PatchUInt32Bits(0xABCD_1234u, 0x00FF_0000u, 3u << 16) ==
            0xAB03_1234u);
    }

    private static void ValidateAcquireMemAndQueueResetDecoders()
    {
        var range = DecodeSubmittedAcquireMem(
            0x8000_7FC0u,
            0x0000_0123u,
            0x45u,
            0x89AB_CDEFu,
            0x0012_3456u,
            0x1_000Au,
            0x0001_0388u);
        System.Diagnostics.Debug.Assert(range.Engine == 1u);
        System.Diagnostics.Debug.Assert(range.CbDbControl == 0x7FC0u);
        System.Diagnostics.Debug.Assert(range.SizeBytes == 0x0000_4500_0001_2300UL);
        System.Diagnostics.Debug.Assert(range.BaseAddress == 0x1234_5689_ABCD_EF00UL);
        System.Diagnostics.Debug.Assert(range.PollInterval == 0xAu);
        System.Diagnostics.Debug.Assert(range.InvalidatesGuestResources);
        System.Diagnostics.Debug.Assert(!range.CoversAllGuestMemory);

        var all = DecodeSubmittedAcquireMem(0, 0, 0, 0, 0, 0, 0x280u);
        System.Diagnostics.Debug.Assert(all.CoversAllGuestMemory);
        System.Diagnostics.Debug.Assert(all.InvalidatesGuestResources);
        var explicitAll = DecodeSubmittedAcquireMem(0, 1, 0, 0, 0, 0, 0x103C0u);
        System.Diagnostics.Debug.Assert(explicitAll.CoversAllGuestMemory);

        var queue = new SubmittedDcbState
        {
            QueueName = "validator",
            ActiveSubmissionId = 7,
            HasActiveSubmission = true,
            IsSuspended = true,
            IndexBufferAddress = 0x1000,
            IndexBufferCount = 12,
            IndexSize = 1,
            InstanceCount = 4,
            DrawIndexOffset = 2,
            IndirectArgsAddress = 0x2000,
            SawIndexedDraw = true,
            GuestDrawKind = GuestDrawKind.FullscreenBarycentric,
        };
        queue.CxRegisters.Add(1, 2);
        queue.ShRegisters.Add(3, 4);
        queue.UcRegisters.Add(5, 6);
        queue.PendingSubmissions.Enqueue(new(0x3000, 2, 8, false));
        ResetSubmittedParserState(queue);
        System.Diagnostics.Debug.Assert(queue.CxRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.ShRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.UcRegisters.Count == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferAddress == 0);
        System.Diagnostics.Debug.Assert(queue.IndexBufferCount == 0);
        System.Diagnostics.Debug.Assert(queue.IndexSize == 0);
        System.Diagnostics.Debug.Assert(queue.InstanceCount == 1);
        System.Diagnostics.Debug.Assert(queue.DrawIndexOffset == 0);
        System.Diagnostics.Debug.Assert(queue.IndirectArgsAddress == 0);
        System.Diagnostics.Debug.Assert(!queue.SawIndexedDraw);
        System.Diagnostics.Debug.Assert(queue.GuestDrawKind == GuestDrawKind.None);
        System.Diagnostics.Debug.Assert(queue.QueueName == "validator");
        System.Diagnostics.Debug.Assert(queue.ActiveSubmissionId == 7);
        System.Diagnostics.Debug.Assert(queue.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(queue.IsSuspended);
        System.Diagnostics.Debug.Assert(queue.PendingSubmissions.Count == 1);
    }

    private static void ValidateDepthTargetDecoder()
    {
        var registers = new Dictionary<uint, uint>
        {
            [DbDepthControl] = 0x2u | 0x4u | (1u << 4),
            [DbDepthSizeXy] = 1919u | (1079u << 16),
            [DbDepthClear] = BitConverter.SingleToUInt32Bits(1f),
            [DbZInfo] = 3u | (24u << 4),
            [DbZReadBase] = 0x0123_4567u,
            [DbZWriteBase] = 0x0123_4567u,
            [DbZReadBaseHi] = 2u,
            [DbZWriteBaseHi] = 2u,
        };
        var depth = DecodeDepthTarget(registers);
        System.Diagnostics.Debug.Assert(depth is not null);
        System.Diagnostics.Debug.Assert(depth.Width == 1920 && depth.Height == 1080);
        System.Diagnostics.Debug.Assert(depth.GuestFormat == 3u);
        System.Diagnostics.Debug.Assert(depth.SwizzleMode == 24u);
        System.Diagnostics.Debug.Assert(depth.Address == 0x0000_0201_2345_6700UL);
        System.Diagnostics.Debug.Assert(depth.ClearDepth == 1f);
    }
#endif

    // SHARPEMU_GPU_WAIT_MODE=force reverts to the legacy behaviour of faking a
    // satisfying value at parse time. Default (suspend) properly suspends the
    // DCB on an unmet WAIT_REG_MEM and resumes it once the awaited completion
    // label is genuinely written by a later submit — preserving cross-submit
    // ordering so the work after a wait (e.g. the final composite) does not run
    // ahead of the compute it samples.
    private static readonly bool _gpuWaitSuspendEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_MODE"),
        "force",
        StringComparison.OrdinalIgnoreCase);

    // Optional age for one-shot missing-producer diagnostics. Stale waits are
    // never removed or force-satisfied in the default suspend mode: doing so
    // advances a queue without its real cross-queue producer and can publish
    // incomplete CPU/GPU state. Only SHARPEMU_GPU_WAIT_MODE=force retains the
    // explicit legacy mutation path above. Default 0 disables age diagnostics.
    private static readonly long _gpuWaitStaleTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_GPU_WAIT_FALLBACK_MS"),
             out var fallbackMs) && fallbackMs >= 0
            ? fallbackMs
            : 0L) * System.Diagnostics.Stopwatch.Frequency / 1000L;

    // How long a suspended GPU wait may sit before the deadlock breaker may
    // release it using the last value a real producer wrote to its label. Long
    // enough that legitimate GPU work (which completes within a frame) never
    // trips it; short enough that a wedged cross-queue cycle unblocks quickly.
    private static readonly long _gpuDeadlockBreakTicks =
        (long.TryParse(
             Environment.GetEnvironmentVariable("SHARPEMU_GPU_DEADLOCK_BREAK_MS"),
             out var deadlockMs) && deadlockMs > 0
            ? deadlockMs
            : 500L) * System.Diagnostics.Stopwatch.Frequency / 1000L;

    // Reads the WAIT_REG_MEM watched address, reference, mask, and 3-bit compare
    // function for both the AGC NOP-encapsulated (RWaitMem32/64) and the standard
    // ItWaitRegMem packet layouts.
    private static bool TryParseSubmittedWait(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        bool is64Bit,
        bool isStandard,
        out ulong waitAddress,
        out ulong reference,
        out ulong mask,
        out uint compareFunction,
        out uint controlValue)
    {
        waitAddress = 0;
        reference = 0;
        mask = 0;
        compareFunction = 0;
        controlValue = 0;
        if (isStandard)
        {
            if (!TryReadUInt32(ctx, packetAddress + 4, out var stdControl) ||
                !TryReadUInt64(ctx, packetAddress + 8, out waitAddress) ||
                !TryReadUInt32(ctx, packetAddress + 16, out var stdRef) ||
                !TryReadUInt32(ctx, packetAddress + 20, out var stdMask))
            {
                return false;
            }

            compareFunction = stdControl & 0x7u;
            controlValue = stdControl;
            reference = stdRef;
            mask = stdMask;
            return true;
        }

        var legacyWait32 = !is64Bit && packetLength == 6;
        var controlOffset = is64Bit ? 28u : legacyWait32 ? 16u : 20u;
        if (!TryReadUInt64(ctx, packetAddress + 4, out waitAddress) ||
            !TryReadUInt32(ctx, packetAddress + controlOffset, out var control))
        {
            return false;
        }

        compareFunction = control & 0x7u;
        controlValue = control;
        if (is64Bit)
        {
            return TryReadUInt64(ctx, packetAddress + 12, out mask) &&
                   TryReadUInt64(ctx, packetAddress + 20, out reference);
        }

        var referenceOffset = legacyWait32 ? 20u : 16u;
        if (!TryReadUInt32(ctx, packetAddress + 12, out var mask32) ||
            !TryReadUInt32(ctx, packetAddress + referenceOffset, out var reference32))
        {
            return false;
        }

        mask = mask32;
        reference = reference32;
        return true;
    }

    // Parks on ring memory not yet written by the game; resumes once it appends more.
    private static bool SuspendOnUnwrittenRingWord(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong wordAddress,
        uint offset,
        bool tracePacket)
    {
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = wordAddress,
            TotalDwords = offset + RingResumeWindowDwords,
            ResumeOffset = offset,
            ReferenceValue = 0,
            Mask = 0xFFFF_FFFFu,
            CompareFunction = 4, // resume once the dword becomes nonzero
            ControlValue = 0,
            Is64Bit = false,
            IsStandard = false,
            WaitAddress = wordAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };
        GpuWaitRegistry.Register(waiter.WaitAddress, waiter);
        state.RingTailParkAddress = wordAddress;
        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.ring_tail_pending addr=0x{wordAddress:X16} " +
                $"queue={state.QueueName}");
        }

        return true;
    }

    // Returns true when the DCB should suspend parsing at this wait (its
    // continuation was registered into GpuWaitRegistry); false to keep parsing
    // (already satisfied, unreadable, or legacy force-satisfy mode).
    // How long an indirect dispatch may wait for its producing dispatch to write
    // non-zero dimensions before we give up and drop it (matching the pre-existing
    // reject behavior). The producer runs on the render thread within a frame or
    // two; this only bounds the pathological/legitimately-empty case.
    private const long IndirectDimsRetryBudgetMs = 150;

    private static readonly object _indirectDimsGate = new();
    // Keys (memory, packetAddress) whose retry deadline elapsed. Added by
    // DrainResumableDcbs when it resumes an expired retry, consumed by the very
    // next re-parse of that packet so it drops instead of re-suspending. Never
    // persists across frames — a fresh submit of the same packet retries anew.
    private static readonly HashSet<(object, ulong)> _indirectDimsExpired = new();

    // Suspends an indirect-dispatch DCB until the guest buffer holding its
    // thread-group dimensions becomes non-zero (written by a prior GPU dispatch),
    // then re-parses the dispatch. Returns false — so the caller drops the work —
    // when the dims already expired once (genuinely empty dispatch).
    private static bool HandleSubmittedIndirectDimsWait(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint dwordCount,
        ulong dimsAddress,
        bool tracePacket)
    {
        if (!_gpuWaitSuspendEnabled ||
            dimsAddress == 0 ||
            dimsAddress % sizeof(uint) != 0)
        {
            return false;
        }

        var key = (ctx.Memory, packetAddress);
        lock (_indirectDimsGate)
        {
            // This is the re-parse right after the deadline elapsed: drop the
            // dispatch instead of suspending again.
            if (_indirectDimsExpired.Remove(key))
            {
                return false;
            }
        }

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress, // re-parse this dispatch packet
            ResumeOffset = offset,
            TotalDwords = dwordCount,
            WaitAddress = dimsAddress,
            ReferenceValue = 0,
            Mask = 0xFFFFFFFF,
            CompareFunction = 4, // NOT_EQUAL: dims became available
            Is64Bit = false,
            IsStandard = false,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            RetryDeadlineTicks = System.Diagnostics.Stopwatch.GetTimestamp() +
                (IndirectDimsRetryBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000L),
            State = state,
        };

        GpuWaitRegistry.Register(dimsAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dispatch_indirect_wait dims=0x{dimsAddress:X16} " +
                $"packet=0x{packetAddress:X16} queue={state.QueueName}");
        }

        return true;
    }

    private static bool HandleSubmittedRewind(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint length,
        uint dwordCount,
        bool tracePacket)
    {
        var bodyAddress = packetAddress + sizeof(uint);
        if (!TryReadUInt32(ctx, bodyAddress, out var body))
        {
            return false;
        }

        if ((body & RewindValidBit) != 0)
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.rewind_valid queue={state.QueueName} " +
                    $"packet=0x{packetAddress:X16} body=0x{body:X8}");
            }

            return false; // already valid — keep parsing
        }

        if (!_gpuWaitSuspendEnabled)
        {
            return false;
        }

        // Suspend until RewindPatchSetRewindState sets bit 31 on the body dword.
        const uint compareEqual = 3;
        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)length * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + length,
            ReferenceValue = RewindValidBit,
            Mask = RewindValidBit,
            CompareFunction = compareEqual,
            ControlValue = 0,
            Is64Bit = false,
            IsStandard = true,
            WaitAddress = bodyAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        GpuWaitRegistry.Register(bodyAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(
            ctx.Memory,
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        TraceAgcShader(
            $"agc.rewind_suspend queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} " +
            $"packet=0x{packetAddress:X16} body=0x{bodyAddress:X16}");
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.rewind_suspend queue={state.QueueName} " +
                $"packet=0x{packetAddress:X16} body=0x{body:X8}");
        }

        return true;
    }

    private static bool HandleSubmittedWaitRegMem(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong commandAddress,
        ulong packetAddress,
        uint offset,
        uint length,
        uint dwordCount,
        bool is64Bit,
        bool isStandard,
        bool tracePacket)
    {
        if (!TryParseSubmittedWait(
                ctx, packetAddress, length, is64Bit, isStandard,
                out var waitAddress, out var reference, out var mask, out var compareFunction,
                out var controlValue))
        {
            return false;
        }

        // COMPARE_FUNC=0 is the hardware "always" condition. Reserved 7 is
        // also fail-open; neither condition may register a waiter. Validate
        // the watched memory before any read so null/malformed packets cannot
        // become permanent entries keyed by address zero.
        if (compareFunction is 0 or 7)
        {
            TraceSubmittedWait(
                waitAddress,
                0,
                mask,
                reference,
                compareFunction,
                is64Bit ? 64 : 32,
                tracePacket);
            return false;
        }

        var requiredAlignment = is64Bit ? sizeof(ulong) : sizeof(uint);
        if (waitAddress == 0 ||
            mask == 0 ||
            waitAddress % (ulong)requiredAlignment != 0)
        {
            TraceAgc(
                $"agc.dcb.wait_reject addr=0x{waitAddress:X16} " +
                $"mask=0x{mask:X16} compare={compareFunction} bits=" +
                $"{(is64Bit ? 64 : 32)} standard={isStandard} " +
                $"packet=0x{packetAddress:X16} reason=invalid-address-or-mask");
            return false;
        }

        ulong currentValue = 0;
        GuestGpuLabelDependency currentDependency = default;
        var waitCachePolicy = (controlValue >> 25) & 0x3u;
        bool hasCurrent;
        if (waitCachePolicy <= 2 &&
            GpuWaitRegistry.TryReadVirtual(
                ctx.Memory,
                waitAddress,
                is64Bit,
                out currentValue,
                out currentDependency,
                waitCachePolicy))
        {
            hasCurrent = true;
        }
        else if (is64Bit)
        {
            hasCurrent = TryReadUInt64(ctx, waitAddress, out currentValue);
        }
        else if (TryReadUInt32(ctx, waitAddress, out var current32))
        {
            currentValue = current32;
            hasCurrent = true;
        }
        else
        {
            hasCurrent = false;
        }

        TraceSubmittedWait(
            waitAddress, currentValue, mask, reference, compareFunction,
            is64Bit ? 64 : 32, tracePacket);

        var waiter = new GpuWaitRegistry.WaitingDcb
        {
            CommandBufferAddress = commandAddress,
            ResumeAddress = packetAddress + ((ulong)length * sizeof(uint)),
            TotalDwords = dwordCount,
            ResumeOffset = offset + length,
            ReferenceValue = reference,
            Mask = mask,
            CompareFunction = compareFunction,
            ControlValue = controlValue,
            Is64Bit = is64Bit,
            IsStandard = isStandard,
            WaitAddress = waitAddress,
            Memory = ctx.Memory,
            QueueName = state.QueueName,
            SubmissionId = state.ActiveSubmissionId,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        if (hasCurrent && GpuWaitRegistry.Compare(waiter, currentValue))
        {
            // Value satisfies the condition, but only bypass if the label was
            // written in the current frame. A stale label from a previous frame
            // means the producer hasn't written yet this frame — must wait.
            if (GpuWaitRegistry.IsLabelFresh(ctx.Memory, waitAddress))
            {
                GuestGpu.Current.RequireGpuLabelDependency(currentDependency);
                return false; // satisfied by current-frame write — keep parsing
            }
        }

        if (!_gpuWaitSuspendEnabled)
        {
            if (hasCurrent)
            {
                ForceSatisfyGpuWait(ctx, waiter, currentValue);
            }

            return false;
        }

        if (!hasCurrent)
        {
            return false; // cannot evaluate the label — do not stall the DCB
        }

        GpuWaitRegistry.Register(waitAddress, waiter);
        var gpuState = _submittedGpuStates.GetValue(
            CanonicalMemory(ctx.Memory),
            static _ => new SubmittedGpuState());
        EnsureGpuWaitMonitor(ctx, gpuState);
        TryForceSubmitOrphanPreamble(ctx, gpuState, waitAddress);
        TraceWaitProducerState(
            ctx.Memory,
            waiter,
            commandAddress,
            packetAddress,
            stale: false,
            currentValue);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.suspended addr=0x{waitAddress:X16} ref=0x{reference:X16} " +
                $"mask=0x{mask:X16} cur=0x{currentValue:X16} cmp={compareFunction}");
        }

        return true;
    }

    /// <summary>
    /// Direct guest CPU stores can satisfy a GPU wait without crossing another
    /// AGC import. Keep one low-frequency monitor per guest memory while waits
    /// exist so those real stores wake their queues. The monitor never changes
    /// a label: it uses the same masked comparison as submission-time parsing
    /// and resumes only after the guest value genuinely satisfies the packet.
    /// </summary>
    private static void EnsureGpuWaitMonitor(
        CpuContext submitContext,
        SubmittedGpuState gpuState)
    {
        if (gpuState.WaitMonitorRunning)
        {
            return;
        }

        gpuState.WaitMonitorRunning = true;
        var monitorContext = new CpuContext(
            submitContext.Memory,
            submitContext.TargetGeneration);
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => MonitorGpuWaits(state.Context, state.GpuState),
            (Context: monitorContext, GpuState: gpuState),
            preferLocal: false);
    }

    // Lets a stall snapshot show whether MonitorGpuWaits' loop is still alive.
    private static long _gpuWaitMonitorHeartbeatCount;
    private static long _gpuWaitMonitorHeartbeatTimestamp;

    public static (long Count, double SecondsSinceLastIteration) GpuWaitMonitorHeartbeat()
    {
        var count = Volatile.Read(ref _gpuWaitMonitorHeartbeatCount);
        var lastTicks = Volatile.Read(ref _gpuWaitMonitorHeartbeatTimestamp);
        var seconds = lastTicks == 0
            ? -1
            : (System.Diagnostics.Stopwatch.GetTimestamp() - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return (count, seconds);
    }

    // Distinguishes a stopped render loop from an orphan-mechanism gap.
    private static long _dcbSubmitCount;
    private static long _lastDcbSubmitTimestamp;

    public static (long Count, double SecondsSinceLastSubmit) DcbSubmitHeartbeat()
    {
        var count = Volatile.Read(ref _dcbSubmitCount);
        var lastTicks = Volatile.Read(ref _lastDcbSubmitTimestamp);
        var seconds = lastTicks == 0
            ? -1
            : (System.Diagnostics.Stopwatch.GetTimestamp() - lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        return (count, seconds);
    }

    // Stall-diagnostic accessor for DirectExecutionBackend's producer scan.
    public static List<(ulong Header, ulong Base, ulong Cursor)> SnapshotBuilderArenas()
    {
        var result = new List<(ulong, ulong, ulong)>();
        lock (_orphanPreambleGate)
        {
            foreach (var (headerAddress, seen) in _builderArenaLastSeen)
            {
                result.Add((headerAddress, seen.Base, seen.Cursor));
            }
        }

        return result;
    }

    // Shows whether a header is silently blacklisted vs. genuinely idle.
    public static string DumpOrphanPreambleState()
    {
        var sb = new System.Text.StringBuilder();
        var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        var freq = (double)System.Diagnostics.Stopwatch.Frequency;
        lock (_orphanPreambleGate)
        {
            sb.Append(
                $"pending_targets={_orphanPreamblePendingTargets.Count} " +
                $"closed_slices={_orphanPreambleClosedSlices.Count} " +
                $"tracked_targets={_cbReleaseMemTargets.Count} " +
                $"tracked_headers={_builderArenaLastSeen.Count}");

            var allHeaders = new HashSet<ulong>(_builderArenaLastSeen.Keys);
            allHeaders.UnionWith(_orphanPreambleSubmitted.Keys);
            foreach (var header in allHeaders)
            {
                var hasSubmitted = _orphanPreambleSubmitted.TryGetValue(header, out var submitted);
                var hasSeen = _builderArenaLastSeen.TryGetValue(header, out var seen);
                var blacklisted = hasSubmitted && submitted.Base == 0;
                var seenAgeText = hasSeen
                    ? $"{(nowTicks - seen.Timestamp) / freq:F1}s"
                    : "never";
                sb.Append(
                    $"\n  header=0x{header:X} blacklisted={blacklisted} " +
                    $"submitted_base=0x{(hasSubmitted ? submitted.Base : 0):X} " +
                    $"submitted_cursor=0x{(hasSubmitted ? submitted.Cursor : 0):X} " +
                    $"last_seen_base=0x{(hasSeen ? seen.Base : 0):X} " +
                    $"last_seen_cursor=0x{(hasSeen ? seen.Cursor : 0):X} " +
                    $"last_seen_age={seenAgeText}");
            }
        }

        return sb.ToString();
    }

    private static void MonitorGpuWaits(
        CpuContext ctx,
        SubmittedGpuState gpuState)
    {
        var delayMilliseconds = 1;
        long observedSignal;
        lock (gpuState.WaitMonitorSignalGate)
        {
            observedSignal = gpuState.WaitMonitorSignalVersion;
        }

        while (true)
        {
            Interlocked.Increment(ref _gpuWaitMonitorHeartbeatCount);
            Volatile.Write(ref _gpuWaitMonitorHeartbeatTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            try
            {
            var madeProgress = false;
            lock (gpuState.Gate)
            {
                var before = GpuWaitRegistry.CountForMemory(ctx.Memory);
                // Under orphan force-submit, the monitor must outlive the
                // waits — the arena sweep below is the only thing that
                // catches CPU-side usleep polling with no WAIT_REG_MEM.
                if (before == 0 && !_forceSubmitOrphanPreamblesEnabled)
                {
                    gpuState.WaitMonitorRunning = false;
                    return;
                }

                var remaining = before;
                if (before != 0)
                {
                    var resumed = DrainResumableDcbs(ctx, gpuState, tracePackets: _traceAgc);
                    remaining = GpuWaitRegistry.CountForMemory(ctx.Memory);
                    madeProgress = resumed != 0;
                    if (_traceAgc && resumed != 0)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] agc.wait_monitor_resumed count={resumed} " +
                            $"remaining={remaining}");
                    }

                    SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TraceGpuWaitSnapshot(
                        ctx.Memory);
                    GpuWaitProfile.RecordMonitorPoll(resumed != 0);
                    GpuWaitProfile.ReportIfDue(remaining);
                    if (remaining == 0 && !_forceSubmitOrphanPreamblesEnabled)
                    {
                        gpuState.WaitMonitorRunning = false;
                        return;
                    }
                }
            }

            // Re-offering every live wait also covers producers the game
            // builds after the wait registered.
            if (_forceSubmitOrphanPreamblesEnabled)
            {
                foreach (var (address, _) in
                         GpuWaitRegistry.SnapshotInRange(ctx.Memory, 0, ulong.MaxValue))
                {
                    TryForceSubmitOrphanPreamble(ctx, gpuState, address);
                }

                DrainPendingOrphanPreambles(ctx, gpuState);
                SweepBuilderArenas(ctx, gpuState);
                SalvageStuckFenceWrites(ctx, gpuState);
            }

            delayMilliseconds = madeProgress
                ? 1
                : Math.Min(delayMilliseconds * 2, 16);
            lock (gpuState.WaitMonitorSignalGate)
            {
                if (gpuState.WaitMonitorSignalVersion == observedSignal)
                {
                    Monitor.Wait(gpuState.WaitMonitorSignalGate, delayMilliseconds);
                }

                observedSignal = gpuState.WaitMonitorSignalVersion;
            }
            }
            catch (Exception ex)
            {
                // No other supervisor: an unlogged exception here would
                // silently end AGC activity forever. Log and keep looping.
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] agc.wait_monitor_iteration_exception " +
                    $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                Thread.Sleep(16);
            }
        }
    }

    /// <summary>
    /// Writes a value that satisfies the waiter's comparison. This deliberately
    /// exists only behind SHARPEMU_GPU_WAIT_MODE=force for legacy A/B testing;
    /// normal and stale waits must never mutate their watched label.
    /// </summary>
    private static void ForceSatisfyGpuWait(
        CpuContext ctx,
        in GpuWaitRegistry.WaitingDcb waiter,
        ulong value)
    {
        var address = waiter.WaitAddress;
        var mask = waiter.Mask;
        if (address == 0 || mask == 0)
        {
            return;
        }

        var maskedRef = waiter.ReferenceValue & mask;
        ulong? satisfyMasked = waiter.CompareFunction switch
        {
            1 => maskedRef == 0 ? null : (maskedRef - 1) & mask,            // <
            2 => maskedRef,                                                 // <=
            3 => maskedRef,                                                 // ==
            4 => (~maskedRef) & mask,                                       // !=
            5 => maskedRef,                                                 // >=
            6 => maskedRef == mask ? null : (maskedRef + 1) & mask,         // >
            _ => null,
        };

        if (satisfyMasked is not { } satisfy)
        {
            return;
        }

        var newValue = (value & ~mask) | (satisfy & mask);
        if (waiter.Is64Bit)
        {
            ctx.TryWriteUInt64(address, newValue);
        }
        else
        {
            TryWriteUInt32(ctx, address, unchecked((uint)newValue));
        }
    }

    // WAIT_REG_MEM packets whose condition is not met suspend their DCB into
    // GpuWaitRegistry. Each submit re-checks every suspended DCB against current
    // guest memory (labels are advanced by ReleaseMem/WriteData/DmaData packets
    // or direct CPU writes) and resumes the ones now satisfied. A resumed DCB
    // can itself write labels that unblock others, so loop to a fixed point.
    private static int DrainResumableDcbs(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        bool tracePackets)
    {
        if (!_gpuWaitSuspendEnabled)
        {
            return 0;
        }

        var resumedCount = 0;
        for (var pass = 0; pass < 256; pass++)
        {
            var woken = GpuWaitRegistry.CollectSatisfied(ctx.Memory, (address, is64Bit) =>
                is64Bit
                    ? TryReadUInt64(ctx, address, out var value64) ? value64 : (ulong?)null
                    : TryReadUInt32(ctx, address, out var value32) ? value32 : (ulong?)null);

            // Indirect-dispatch dimension retries whose deadline elapsed are
            // resumed so they drop instead of stalling. Flag each so its immediate
            // re-parse drops the dispatch rather than suspending again.
            var expiredRetries = GpuWaitRegistry.CollectExpiredRetries(
                ctx.Memory, System.Diagnostics.Stopwatch.GetTimestamp());
            if (expiredRetries is not null)
            {
                lock (_indirectDimsGate)
                {
                    foreach (var retry in expiredRetries)
                    {
                        _indirectDimsExpired.Add((ctx.Memory, retry.ResumeAddress));
                    }
                }

                foreach (var retry in expiredRetries)
                {
                    ResumeSuspendedDcb(ctx, gpuState, retry, tracePackets);
                }
            }

            // Break cross-queue deadlocks: a waiter stuck past the deadline whose
            // label a real producer already signalled (but guest memory has since
            // been reset for reuse) is released using that produced value. Only
            // fires for genuinely wedged waits, so fast-resolving ones on working
            // titles are untouched.
            var deadlockBroken = GpuWaitRegistry.CollectDeadlockBroken(
                ctx.Memory, System.Diagnostics.Stopwatch.GetTimestamp(), _gpuDeadlockBreakTicks);
            if (deadlockBroken is not null)
            {
                foreach (var waiter in deadlockBroken)
                {
                    if (tracePackets)
                    {
                        TraceAgc(
                            $"agc.deadlock_break label=0x{waiter.WaitAddress:X16} " +
                            $"queue={waiter.QueueName} submission={waiter.SubmissionId}");
                    }

                    ResumeSuspendedDcb(ctx, gpuState, waiter, tracePackets);
                }
            }

            if (woken is null && expiredRetries is null && deadlockBroken is null)
            {
                if (_gpuWaitStaleTicks > 0 &&
                    GpuWaitRegistry.CollectUnreportedStale(
                        ctx.Memory,
                        System.Diagnostics.Stopwatch.GetTimestamp(),
                        _gpuWaitStaleTicks) is { } stale)
                {
                    foreach (var waiter in stale)
                    {
                        ulong? currentValue = waiter.Is64Bit
                            ? TryReadUInt64(ctx, waiter.WaitAddress, out var value64)
                                ? value64
                                : null
                            : TryReadUInt32(ctx, waiter.WaitAddress, out var value32)
                                ? value32
                                : null;
                        TraceWaitProducerState(
                            ctx.Memory,
                            waiter,
                            waiter.CommandBufferAddress,
                            waiter.ResumeAddress,
                            stale: true,
                            currentValue);
                    }
                }

                return resumedCount;
            }

            if (woken is not null)
            {
                foreach (var waiter in woken)
                {
                    ResumeSuspendedDcb(ctx, gpuState, waiter, tracePackets);
                    resumedCount++;
                }
            }
        }

        return resumedCount;
    }

    private static void ResumeSuspendedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        in GpuWaitRegistry.WaitingDcb waiter,
        bool tracePackets)
    {
        var state = waiter.State as SubmittedDcbState ?? gpuState.Graphics;
        if (state.IsFaulted)
        {
            return;
        }

        // Any resume ends a ring-tail park; SuspendOnUnwrittenRingWord re-arms
        // it if the continued parse parks again.
        state.RingTailParkAddress = 0;
        var remainingDwords = waiter.TotalDwords - waiter.ResumeOffset;
        var waitedMilliseconds = waiter.RegisteredTicks == 0
            ? 0.0
            : (System.Diagnostics.Stopwatch.GetTimestamp() - waiter.RegisteredTicks) *
              1000.0 / System.Diagnostics.Stopwatch.Frequency;
        TraceAgcShader(
            $"agc.queue_resumed queue={waiter.QueueName} " +
            $"submission={waiter.SubmissionId} label=0x{waiter.WaitAddress:X16} " +
            $"resume=0x{waiter.ResumeAddress:X16} remaining_dwords={remainingDwords} " +
            $"waited_ms={waitedMilliseconds:F3}");
        GpuWaitProfile.RecordResume(waiter.WaitAddress, waitedMilliseconds);
        if (remainingDwords == 0)
        {
            state.IsSuspended = false;
            state.HasActiveSubmission = false;
            state.ActiveIndexSnapshots = null;
            state.ActiveVertexSnapshots = null;
            NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
            PumpSubmittedQueue(ctx, gpuState, state);
            return;
        }

        if (tracePackets)
        {
            TraceAgc(
                $"agc.dcb.resumed addr=0x{waiter.WaitAddress:X16} " +
                $"resume=0x{waiter.ResumeAddress:X16} dwords={remainingDwords} forced=False");
        }

        System.Diagnostics.Debug.Assert(state.HasActiveSubmission);
        System.Diagnostics.Debug.Assert(state.IsSuspended);
        state.QueueName = waiter.QueueName ?? state.QueueName;
        state.ActiveSubmissionId = waiter.SubmissionId;
        state.IsSuspended = false;
        using var guestQueueScope = GuestGpu.Current.EnterGuestQueue(
            state.QueueName,
            state.ActiveSubmissionId);
        GuestGpu.Current.RequireGpuLabelDependency(waiter.Dependency);
        var isSuspended = ParseSubmittedDcb(
            ctx,
            gpuState,
            state,
            waiter.ResumeAddress,
            remainingDwords,
            tracePackets);
        if (state.IsFaulted)
        {
            return;
        }

        if (isSuspended)
        {
            state.IsSuspended = true;
            return;
        }

        state.HasActiveSubmission = false;
        state.ActiveIndexSnapshots = null;
        state.ActiveVertexSnapshots = null;
        NotifySubmittedDcbCompleted(gpuState, state, waiter.SubmissionId);
        PumpSubmittedQueue(ctx, gpuState, state);
    }

    private static void TraceSubmittedWait(
        ulong address,
        ulong value,
        ulong mask,
        ulong reference,
        uint compareFunction,
        int bits,
        bool tracePacket)
    {
        var maskedValue = value & mask;
        var satisfied = compareFunction switch
        {
            0 => true,
            1 => maskedValue < reference,
            2 => maskedValue <= reference,
            3 => maskedValue == reference,
            4 => maskedValue != reference,
            5 => maskedValue >= reference,
            6 => maskedValue > reference,
            _ => true,
        };
        if (!tracePacket && (satisfied || !ShouldTraceHotPath(ref _unsatisfiedWaitTraceCount)))
        {
            return;
        }

        TraceAgc(
            $"agc.dcb.wait_reg_mem bits={bits} addr=0x{address:X16} " +
            $"value=0x{value:X16} mask=0x{mask:X16} ref=0x{reference:X16} " +
            $"compare={compareFunction} satisfied={satisfied}");
    }

    private static void ApplySubmittedStandardReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out _) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi))
        {
            return;
        }

        var (destination, dataSelection) = DecodeStandardReleaseMemControl(control);
        var interruptSelection = (control >> 24) & 0x7u;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 or 4 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        var writesGuestMemory = destination is 0 or 1 &&
                                destinationAddress != 0 &&
                                writeLength != 0;

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                if (writesGuestMemory)
                {
                    InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                }

                var wroteData = writesGuestMemory && (dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    2 => ctx.TryWriteUInt64(destinationAddress, data),
                    // Hardware counter writes are timing values sampled at the
                    // release point, not the immediate payload in ordinal 6/7.
                    3 or 4 => ctx.TryWriteUInt64(
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                });

                // Record + latch the written value so a same-frame label reset
                // cannot lose the wakeup, and so the deadlock breaker can release
                // a cross-queue waiter later (see ApplySubmittedReleaseMem).
                if (wroteData && dataSelection is 1 or 2)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory, destinationAddress, dataSelection == 1 ? dataLo : data);
                }
                else if (!wroteData && dataSelection is 1 or 2)
                {
                    // See ApplySubmittedReleaseMem: a dropped label write strands
                    // every waiter on this label permanently.
                    ReportLabelWriteFailure(
                        "release_mem_standard", destinationAddress, data, dataSelection);
                }

                // Only deliver a kevent when int_sel requests one — the
                // driver's completion refcount signals on an exact zero
                // crossing, and an unrequested kevent drives it negative and
                // permanently loses the frame-graph kick.
                var wokenQueues = interruptSelection != 0
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        data)
                    : 0;

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem_standard dst_sel={destination} " +
                        $"dst=0x{destinationAddress:X16} data_sel={dataSelection} " +
                        $"data=0x{data:X16} wrote={wroteData} " +
                        $"int={interruptSelection} woken={wokenQueues}");
                }
            },
            $"release_mem_standard dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? writeLength : 0);
    }

    private static long _labelWriteFailureCount;

    /// <summary>
    /// Reports a GPU release-label write that could not reach guest memory.
    /// Rate-limited (first 16, then powers of two) because a wedged queue can
    /// retry, but never silenced: this is the difference between a diagnosable
    /// fault and a permanently suspended graphics queue with no explanation.
    /// </summary>
    private static void ReportLabelWriteFailure(
        string packet,
        ulong destinationAddress,
        ulong data,
        uint dataSelection)
    {
        var count = Interlocked.Increment(ref _labelWriteFailureCount);
        if (count > 16 && (count & (count - 1)) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][ERROR] agc.label_write_failed packet={packet} " +
            $"dst=0x{destinationAddress:X16} data=0x{data:X16} " +
            $"data_sel={dataSelection} count={count} — a suspended WAIT_REG_MEM " +
            $"on this label can no longer be satisfied or deadlock-broken.");
    }

    private static (uint Destination, uint DataSelection)
        DecodeStandardReleaseMemControl(uint control) =>
        (
            Destination: (control >> 16) & 0x3u,
            DataSelection: (control >> 29) & 0x7u);

    private static void ApplySubmittedReleaseMem(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out _) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi))
        {
            return;
        }

        var dataSelection = (control >> 16) & 0xFFu;
        var interrupt = (control >> 24) & 0xFFu;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                var wroteData = dataSelection switch
                {
                    1 => TryWriteUInt32(ctx, destinationAddress, dataLo),
                    2 => ctx.TryWriteUInt64(destinationAddress, data),
                    // Data selection 3 samples the GPU clock at the release
                    // point. The packet payload is ignored by hardware; Unity
                    // uses the nonzero timestamp as submit-completion state.
                    3 => ctx.TryWriteUInt64(
                        destinationAddress,
                        unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp())),
                    _ => false,
                };

                // Latch waiters against the value we just wrote: the guest reuses
                // these labels and can reset them to 0 before the wake pass reads
                // memory, which otherwise loses the wakeup and stalls at a black
                // screen (Astro Bot: graphics queue waiting on a compute EOP label).
                if (wroteData && dataSelection is 1 or 2)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory, destinationAddress, dataSelection == 1 ? dataLo : data);
                }
                else if (!wroteData && dataSelection is 1 or 2)
                {
                    // A label write that fails is not a benign miss: this packet
                    // is the producer a suspended WAIT_REG_MEM is waiting for, and
                    // RecordProduced above is skipped, so the deadlock breaker has
                    // no value to replay either. The queue then never resumes.
                    // Never let that happen quietly.
                    ReportLabelWriteFailure("release_mem", destinationAddress, data, dataSelection);
                }

                // Same interrupt gating as the standard form above.
                var wokenQueues = interrupt != 0
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        data)
                    : 0;

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem dst=0x{destinationAddress:X16} " +
                        $"data_sel={dataSelection} data=0x{data:X16} wrote={wroteData} " +
                        $"int={interrupt} woken={wokenQueues}");
                }
            },
            $"release_mem dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            dataSelection is 1 or 2 or 3 ? destinationAddress : 0,
            writeLength);
    }

    private static void ApplySubmittedRegisters(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        uint register)
    {
        if (op is ItSetShReg or ItSetContextReg or ItSetUconfigReg)
        {
            if (packetLength < 3 ||
                !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var startRegister))
            {
                return;
            }

            var directDestination = op switch
            {
                ItSetShReg => state.ShRegisters,
                ItSetContextReg => state.CxRegisters,
                _ => state.UcRegisters,
            };
            for (uint index = 0; index < packetLength - 2; index++)
            {
                if (!TryReadUInt32(
                        ctx,
                        packetAddress + 8 + ((ulong)index * sizeof(uint)),
                        out var value))
                {
                    return;
                }

                directDestination[startRegister + index] = value;
                if (op == ItSetUconfigReg)
                {
                    ApplyUcIndexTypeIfNeeded(state, startRegister + index, value);
                }
            }

            return;
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            packetLength < 4 ||
            !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var destination = register switch
        {
            RCxRegsIndirect => state.CxRegisters,
            RShRegsIndirect => state.ShRegisters,
            _ => state.UcRegisters,
        };
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return;
            }

            // The indirect table has an explicit count; offset zero is a real
            // context-register index (DB_RENDER_CONTROL), not a terminator.
            // Dropping it leaves stale depth/render-control state active in
            // later passes.
            destination[registerOffset] = value;
            if (register == RUcRegsIndirect)
            {
                ApplyUcIndexTypeIfNeeded(state, registerOffset, value);
            }
        }
    }

    /// <summary>
    /// Test-only view of a parsed graphics context register. False when the
    /// register was never written.
    /// </summary>
    internal static bool TryGetGraphicsContextRegisterForTests(
        CpuContext ctx,
        uint registerOffset,
        out uint value)
    {
        value = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.CxRegisters.TryGetValue(registerOffset, out value);
        }
    }

    /// <summary>
    /// SH-register counterpart of <see cref="TryGetGraphicsContextRegisterForTests"/>;
    /// the shader stage addresses live here.
    /// </summary>
    internal static bool TryGetGraphicsShRegisterForTests(
        CpuContext ctx,
        uint registerOffset,
        out uint value)
    {
        value = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.ShRegisters.TryGetValue(registerOffset, out value);
        }
    }

    internal static bool TryGetGraphicsIndexStateForTests(
        CpuContext ctx,
        out ulong address,
        out uint count,
        out uint offset)
    {
        address = 0;
        count = 0;
        offset = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            address = gpuState.Graphics.IndexBufferAddress;
            count = gpuState.Graphics.IndexBufferCount;
            offset = gpuState.Graphics.DrawIndexOffset;
            return true;
        }
    }

    /// <summary>
    /// GraphicsDcbSetIndexSize writes VGT_INDEX_TYPE via SET_UCONFIG_REG.
    /// Mirror that into <see cref="SubmittedDcbState.IndexSize"/>.
    /// </summary>
    private static void ApplyUcIndexTypeIfNeeded(
        SubmittedDcbState state,
        uint registerOffset,
        uint value)
    {
        if (registerOffset == VgtIndexType)
        {
            state.IndexSize = value & 0x3;
        }
    }

    private const int IndirectArgsFlushTimeoutMilliseconds = 250;

    private static void FlushGpuWorkForIndirectArgs(SubmittedGpuState gpuState)
    {
        var pending = gpuState.WorkSequence;
        if (pending == 0 || pending > long.MaxValue)
        {
            return;
        }

        GuestGpu.Current.WaitForGuestWork(
            (long)pending,
            IndirectArgsFlushTimeoutMilliseconds);
    }

    private static bool TryReadSubmittedDrawCount(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        out uint drawCount)
    {
        drawCount = 0;
        state.CurrentVertexSnapshot = null;
        if (state.ActiveVertexSnapshots is not null &&
            state.ActiveVertexSnapshots.TryGetValue(packetAddress, out var vertexSnapshot))
        {
            state.CurrentVertexSnapshot = vertexSnapshot;
        }

        switch (op)
        {
            case ItDrawIndexAuto when packetLength >= 3:
                return TryReadUInt32(ctx, packetAddress + 4, out drawCount);
            case ItDrawIndex2 when packetLength >= 6:
                if (!TryReadUInt32(ctx, packetAddress + 4, out var maximumIndexCount) ||
                    !TryReadUInt32(ctx, packetAddress + 8, out var indexBaseLo) ||
                    !TryReadUInt32(ctx, packetAddress + 12, out var indexBaseHi) ||
                    !TryReadUInt32(ctx, packetAddress + 16, out drawCount))
                {
                    return false;
                }

                state.IndexBufferAddress = indexBaseLo | ((ulong)indexBaseHi << 32);
                state.IndexBufferCount = maximumIndexCount;
                state.DrawIndexOffset = 0;
                state.CurrentIndexSnapshot = null;
                if (state.ActiveIndexSnapshots is not null &&
                    state.ActiveIndexSnapshots.TryGetValue(packetAddress, out var snapshot) &&
                    snapshot.SourceAddress == state.IndexBufferAddress &&
                    snapshot.IndexCount == drawCount)
                {
                    state.CurrentIndexSnapshot = snapshot;
                }

                return true;
            case ItDrawIndexOffset2 when packetLength >= 5:
                if (!TryReadUInt32(ctx, packetAddress + 8, out var indexOffset))
                {
                    return false;
                }

                state.DrawIndexOffset = indexOffset;
                return TryReadUInt32(ctx, packetAddress + 12, out drawCount);
            case ItDrawIndexMultiAuto when packetLength >= 4:
                if (!TryReadUInt32(ctx, packetAddress + 12, out var control))
                {
                    return false;
                }

                drawCount = (control >> 21) & 0x7FFu;
                return true;
            case ItDrawIndexIndirectMulti when packetLength >= 8 &&
                state.IndirectArgsAddress != 0:
                if (!TryReadUInt32(ctx, packetAddress + 4, out var multiOffset) ||
                    !TryReadUInt32(ctx, packetAddress + 20, out var multiDraws) ||
                    !TryReadUInt32(ctx, packetAddress + 24, out var multiStride))
                {
                    return false;
                }


                if (multiStride < DrawIndexedIndirectArgsSize)
                {
                    multiStride = DrawIndexedIndirectArgsSize;
                }

                var multiTotal = 0UL;
                var multiCapped = multiDraws == 0
                    ? DrawIndexedIndirectMaxScan
                    : Math.Min(multiDraws, 4096u);
                for (var draw = 0u; draw < multiCapped; draw++)
                {
                    if (!TryReadUInt32(
                            ctx,
                            state.IndirectArgsAddress + multiOffset + ((ulong)draw * multiStride),
                            out var subCount))
                    {
                        break;
                    }

                    if (subCount == 0 && multiDraws == 0)
                    {
                        break;
                    }

                    multiTotal += subCount;
                }

                var multiProbe = Interlocked.Increment(ref _indirectMultiProbeCount);
                if (multiProbe <= 12 || multiProbe % 250 == 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.draw_multi#{multiProbe} args=0x{state.IndirectArgsAddress:X} " +
                        $"off=0x{multiOffset:X} draws={multiDraws} stride={multiStride} " +
                        $"total={multiTotal}");
                }

                drawCount = (uint)Math.Min(multiTotal, uint.MaxValue);
                return drawCount != 0;
            case ItDrawIndirect or ItDrawIndexIndirect:
                var probe = Interlocked.Increment(ref _indirectDrawProbeCount);
                if (!TryReadUInt32(ctx, packetAddress + 4, out var dataOffset))
                {
                    dataOffset = 0xFFFFFFFFu;
                }

                var readable = packetLength >= 5 &&
                    state.IndirectArgsAddress != 0 &&
                    dataOffset != 0xFFFFFFFFu;
                var resolved = readable &&
                    TryReadUInt32(
                        ctx,
                        state.IndirectArgsAddress + dataOffset,
                        out drawCount);
                if (probe <= 12 || probe % 100 == 0)
                {
                    var dump = string.Empty;
                    for (var word = 0; word < 8; word++)
                    {
                        dump += TryReadUInt32(
                            ctx,
                            state.IndirectArgsAddress + dataOffset + ((ulong)word * 4),
                            out var raw)
                            ? $" {raw}"
                            : " ?";
                    }

                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.draw_indirect#{probe} op=0x{op:X} len={packetLength} " +
                        $"args=0x{state.IndirectArgsAddress:X} off=0x{dataOffset:X} " +
                        $"resolved={resolved} count={drawCount} words:{dump}");
                }

                return resolved;
            default:
                return false;
        }
    }

    private static void TryTranslateGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        uint vertexCount,
        bool indexed)
    {
        var hasExportShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoEs,
            SpiShaderPgmHiEs,
            out var exportShaderAddress);
        var hasPixelShader = TryGetShaderAddress(
            state.ShRegisters,
            SpiShaderPgmLoPs,
            SpiShaderPgmHiPs,
            out var pixelShaderAddress);
        var hasPsInputEna = state.CxRegisters.TryGetValue(SpiPsInputEna, out var psInputEna);
        var hasPsInputAddr = state.CxRegisters.TryGetValue(SpiPsInputAddr, out var psInputAddr);
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
var renderTargets = GetRenderTargets(state.CxRegisters);
        TrackCmaskAddresses(state.CxRegisters, renderTargets);
        var drawSequence = ++gpuState.WorkSequence;
        if (state.PendingTargetlessDraw is { } stalePendingDraw)
        {
            ReturnPooledDrawArrays(
                stalePendingDraw,
                globals: true,
                vertex: true,
                index: true);
            state.PendingTargetlessDraw = null;
        }
        state.TranslatedDraw = null;
        state.GuestDrawKind = GuestDrawKind.None;

        // CB modes EliminateFastClear / FmaskDecompress / DccDecompress run
        // colour-buffer metadata ops. The bound shader is only a vehicle and
        // must not be applied as a normal colour draw.
        if (TryGetCbColorControlMode(state.CxRegisters, out var cbMode) &&
            IsCbMetadataColorMode(cbMode))
        {
            // EliminateFastClear: the game explicitly asks the CB to clear
            // the fast-clear metadata and the colour buffer.
            if (cbMode == (uint)CbColorMode.EliminateFastClear &&
                renderTargets.Count > 0 &&
                renderTargets[0].Address != 0)
            {
                var targetAddr = renderTargets[0].Address;
                bool requestClear;
                lock (_metaSurfaceGate)
                {
                    requestClear =
                        _metaSurfaces.TryGetValue(targetAddr, out var meta) &&
                        meta.IsCleared;
                    if (requestClear)
                    {
                        _metaSurfaces[targetAddr] = meta with { IsCleared = false };
                    }
                }

                if (requestClear)
                {
                    VulkanVideoPresenter.RequestGuestColorClear(targetAddr);
                }
            }

            if (_traceAgcShader || ShouldTraceHotPath(ref _cbMetadataSkipTraceCount))
            {
                TraceAgcShader(
                    $"agc.cb_metadata_skip seq={drawSequence} mode={cbMode} " +
                    $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                    $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16} " +
                    $"vertices={vertexCount}");
            }

            return;
        }

        foreach (var target in renderTargets)
        {
            state.KnownRenderTargets[target.Address] = target;
            // Colour exports originate in the pixel stage.  A depth-only draw
            // can leave old CB registers bound, but it must not become the
            // advertised writer of those surfaces merely because they remain
            // in state.
            if (hasPixelShader)
            {
                state.RenderTargetWriters[target.Address] = new RenderTargetWriter(
                    drawSequence,
                    hasExportShader ? exportShaderAddress : 0,
                    pixelShaderAddress,
                    vertexCount,
                    primitiveType);
            }

            if (_traceAgcShader ||
                _tracePixelShaderAddress == pixelShaderAddress ||
                _traceRenderTargetAddress == target.Address)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"agc.rt_writer seq={drawSequence} target=0x{target.Address:X16} " +
                    $"fmt={target.Format} tile={target.TileMode} " +
                    $"size={target.Width}x{target.Height} vertices={vertexCount} " +
                    $"prim=0x{primitiveType:X} indexed={indexed} " +
                    $"es=0x{(hasExportShader ? exportShaderAddress : 0):X16} " +
                    $"ps=0x{(hasPixelShader ? pixelShaderAddress : 0):X16} " +
                    $"color_write={(hasPixelShader ? 1 : 0)}");
            }
        }

        if (vertexCount == 0 || vertexCount > 1_048_576)
        {
            return;
        }

        var translationError = string.Empty;
        var depthState = DecodeDepthState(state.CxRegisters);
        var depthTarget = DecodeDepthTarget(state.CxRegisters);
        var hasDepthOnlyCandidate = hasExportShader &&
            !hasPixelShader &&
            depthTarget is not null &&
            (depthState.TestEnable || depthState.WriteEnable || depthState.ClearEnable);
        if (hasDepthOnlyCandidate &&
            TryCreateTranslatedDepthOnlyGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                vertexCount,
                indexed,
                depthTarget!,
                out var depthOnlyDraw,
                out translationError))
        {
            depthOnlyDraw = ApplyHtileMetadataClear(
                gpuState,
                drawSequence,
                depthOnlyDraw);
            state.TranslatedDraw = depthOnlyDraw;
            var activeDepthTarget = depthOnlyDraw.DepthTarget!;
            var textures = CreateGuestDrawTextures(
                ctx,
                depthOnlyDraw.Textures,
                out _);
            var globalMemoryBuffers =
                CreateTranslatedDrawGlobalBuffers(depthOnlyDraw);
            var vertexBuffers =
                CreateGuestVertexBuffers(
                    depthOnlyDraw.VertexInputs,
                    depthOnlyDraw.VertexBufferBaseVertex);
            var renderState = depthOnlyDraw.RenderState;
            if (activeDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
            {
                renderState = renderState with
                {
                    Depth = renderState.Depth with { WriteEnable = false },
                };
            }

            TraceDrawCompact(
                drawSequence,
                depthOnlyDraw,
                textures,
                vertexBuffers);
            GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                depthOnlyDraw.PixelShader,
                textures,
                globalMemoryBuffers,
                depthOnlyDraw.AttributeCount,
                activeDepthTarget,
                depthOnlyDraw.VertexShader,
                depthOnlyDraw.VertexCount,
                depthOnlyDraw.InstanceCount,
                depthOnlyDraw.PrimitiveType,
                depthOnlyDraw.IndexBuffer,
                vertexBuffers,
                renderState,
                depthOnlyDraw.PixelShaderAddress,
                depthOnlyDraw.BaseVertex);

            if (_traceAgcShader)
            {
                TraceAgcShader(
                    $"agc.depth_only_draw seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} " +
                    $"depth=0x{activeDepthTarget.Address:X16}:" +
                    $"{activeDepthTarget.Width}x{activeDepthTarget.Height}:" +
                    $"fmt{activeDepthTarget.GuestFormat}/sw{activeDepthTarget.SwizzleMode} " +
                    $"test={(renderState.Depth.TestEnable ? 1 : 0)} " +
                    $"write={(renderState.Depth.WriteEnable ? 1 : 0)} " +
                    $"func={renderState.Depth.CompareOp} ro={(activeDepthTarget.ReadOnly ? 1 : 0)}");
            }

            return;
        }

        if (hasExportShader &&
            hasPixelShader &&
            hasPsInputEna &&
            hasPsInputAddr &&
            TryCreateTranslatedGuestDraw(
                ctx,
                state,
                exportShaderAddress,
                pixelShaderAddress,
                psInputEna,
                psInputAddr,
                vertexCount,
                indexed,
                out var translatedDraw,
                out translationError))
        {
            state.TranslatedDraw = translatedDraw;
            if (TryGetHardwareColorResolveTargets(
                    state.CxRegisters,
                    out var resolveSource,
                    out var resolveDestination))
            {
                state.KnownRenderTargets[resolveSource.Address] = resolveSource;
                state.KnownRenderTargets[resolveDestination.Address] = resolveDestination;
                ProvideRenderTargetInitialData(ctx, resolveSource);
                if (GuestGpu.Current.TrySubmitGuestImageBlit(
                        resolveSource.Address,
                        resolveSource.Width,
                        resolveSource.Height,
                        resolveSource.Format,
                        resolveSource.NumberType,
                        resolveDestination.Address,
                        resolveDestination.Width,
                        resolveDestination.Height,
                        resolveDestination.Format,
                        resolveDestination.NumberType))
                {
                    state.RenderTargetWriters[resolveDestination.Address] =
                        new RenderTargetWriter(
                            drawSequence,
                            exportShaderAddress,
                            pixelShaderAddress,
                            vertexCount,
                            primitiveType);
                    TraceAgcShader(
                        $"agc.hardware_color_resolve seq={drawSequence} " +
                        $"src=0x{resolveSource.Address:X16}:" +
                        $"{resolveSource.Width}x{resolveSource.Height}:" +
                        $"fmt{resolveSource.Format}/num{resolveSource.NumberType} " +
                        $"dst=0x{resolveDestination.Address:X16}:" +
                        $"{resolveDestination.Width}x{resolveDestination.Height}:" +
                        $"fmt{resolveDestination.Format}/num{resolveDestination.NumberType}");
                    ReturnPooledDrawArrays(
                        translatedDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.TranslatedDraw = null;
                    return;
                }

                TraceAgcShader(
                    $"agc.hardware_color_resolve_unavailable seq={drawSequence} " +
                    $"src=0x{resolveSource.Address:X16} " +
                    $"dst=0x{resolveDestination.Address:X16}");
            }

            // A DCC fast clear writes metadata only; the colour block discards
            // the quad's shaded output. Reset the attachment and drop the draw,
            // which reproduces the observable effect of a clear to zero without
            // modelling DCC block state.
            if (translatedDraw.IsDccFastClear)
            {
                foreach (var target in translatedDraw.GuestTargets)
                {
                    if (target.Address != 0)
                    {
                        VulkanVideoPresenter.RequestGuestColorClear(target.Address);
                    }
                }

                ReturnPooledDrawArrays(
                    translatedDraw,
                    globals: true,
                    vertex: true,
                    index: true);
                state.TranslatedDraw = null;
                return;
            }

            // DbRenderControl CLEARON (bit0): when set, the CB clears color
            // targets on first draw. Handle color targets (depth is already
            // handled by DecodeDepthState).
            if (state.CxRegisters.TryGetValue(DbRenderControl, out var rc) && (rc & 0x1u) != 0)
            {
                foreach (var rt in translatedDraw.RenderTargets)
                {
                    if (rt.Address != 0)
                    {
                        VulkanVideoPresenter.RequestGuestColorClear(rt.Address);
                    }
                }
            }

            // CMASK fast clear: CB_COLORn_INFO.FAST_CLEAR (bit12) set on
            // one or more targets. The CB clears via CMASK before the draw
            // writes; mark targets for clear-on-first-use.
            if (IsCmaskFastClearDraw(state.CxRegisters, translatedDraw.RenderTargets))
            {
                foreach (var rt in translatedDraw.RenderTargets)
                {
                    if (rt.Address != 0)
                    {
                        VulkanVideoPresenter.RequestGuestColorClear(rt.Address);
                    }
                }
            }

            var firstTarget = translatedDraw.RenderTargets.FirstOrDefault();
            if (firstTarget.Address != 0)
            {
                if (!translatedDraw.IsFullscreenColorClear)
                {
                    translatedDraw = ApplyHtileMetadataClear(
                        gpuState,
                        drawSequence,
                        translatedDraw);
                    state.TranslatedDraw = translatedDraw;
                }

                // Submit all color targets in one host MRT pass. The depth
                // attachment is therefore loaded or cleared once for this
                // guest draw.
                var drawRenderTargets = translatedDraw.RenderTargets;
                var lastTargetIndex = 0;
                for (var targetIndex = 1; targetIndex < drawRenderTargets.Count; targetIndex++)
                {
                    if (drawRenderTargets[targetIndex].Address != 0)
                    {
                        lastTargetIndex = targetIndex;
                    }
                }

                var sharedTextures = CreateGuestDrawTextures(
                    ctx,
                    translatedDraw.Textures,
                    out _);
                var sharedGlobalMemoryBuffers =
                    CreateTranslatedDrawGlobalBuffers(translatedDraw);
                var sharedVertexBuffers =
                    CreateGuestVertexBuffers(
                        translatedDraw.VertexInputs,
                        translatedDraw.VertexBufferBaseVertex);
                TraceRectListVertices(translatedDraw, sharedVertexBuffers);
                TraceGrassDrawVertices(translatedDraw, sharedTextures, sharedVertexBuffers);
                TraceDrawCompact(
                    drawSequence,
                    translatedDraw,
                    sharedTextures,
                    sharedVertexBuffers);
                foreach (var renderTarget in drawRenderTargets)
                {
                    if (renderTarget.Address != 0)
                    {
                        ProvideRenderTargetInitialData(ctx, renderTarget);
                    }
                }

                if (translatedDraw.IsFullscreenColorClear)
                {
                    VulkanVideoPresenter.SubmitOffscreenColorClear(
                        translatedDraw.GuestTargets,
                        translatedDraw.ClearRed,
                        translatedDraw.ClearGreen,
                        translatedDraw.ClearBlue,
                        translatedDraw.ClearAlpha,
                        translatedDraw.PixelShaderAddress);
                }
                else
                {
                    GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                        translatedDraw.PixelShader,
                        sharedTextures,
                        sharedGlobalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        translatedDraw.GuestTargets,
                        translatedDraw.VertexShader,
                        translatedDraw.VertexCount,
                        translatedDraw.InstanceCount,
                        translatedDraw.PrimitiveType,
                        translatedDraw.IndexBuffer,
                        sharedVertexBuffers,
                        translatedDraw.RenderState,
                        translatedDraw.DepthTarget,
                        translatedDraw.PixelShaderAddress,
                        translatedDraw.BaseVertex);
                }
            }
            else
            {
                if (translatedDraw.DepthTarget is { } translatedDepthTarget)
                {
                    translatedDraw = ApplyHtileMetadataClear(
                        gpuState,
                        drawSequence,
                        translatedDraw);
                    state.TranslatedDraw = translatedDraw;
                    translatedDepthTarget = translatedDraw.DepthTarget!;
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        translatedDraw.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(translatedDraw);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(
                            translatedDraw.VertexInputs,
                            translatedDraw.VertexBufferBaseVertex);
                    var renderState = translatedDraw.RenderState;
                    if (translatedDepthTarget.ReadOnly && renderState.Depth.WriteEnable)
                    {
                        renderState = renderState with
                        {
                            Depth = renderState.Depth with { WriteEnable = false },
                        };
                    }

                    TraceDrawCompact(
                        drawSequence,
                        translatedDraw,
                        textures,
                        vertexBuffers);
                    GuestGpu.Current.SubmitDepthOnlyTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDraw.AttributeCount,
                        translatedDepthTarget,
                        translatedDraw.VertexShader,
                        translatedDraw.VertexCount,
                        translatedDraw.InstanceCount,
                        translatedDraw.PrimitiveType,
                        translatedDraw.IndexBuffer,
                        vertexBuffers,
                        renderState,
                        translatedDraw.PixelShaderAddress,
                        translatedDraw.BaseVertex);
                }
                else
                {
                    var storageTarget = translatedDraw.Textures
                        .FirstOrDefault(binding => binding.IsStorage);
                    if (storageTarget is not null)
                    {
                        var textures = CreateGuestDrawTextures(
                            ctx,
                            translatedDraw.Textures,
                            out _);
                        var globalMemoryBuffers =
                            CreateTranslatedDrawGlobalBuffers(translatedDraw);
                        TraceDrawCompact(drawSequence, translatedDraw, textures, []);
                        GuestGpu.Current.SubmitStorageTranslatedDraw(
                            translatedDraw.PixelShader,
                            textures,
                            globalMemoryBuffers,
                            translatedDraw.AttributeCount,
                            storageTarget.Descriptor.Width,
                            storageTarget.Descriptor.Height,
                            translatedDraw.PixelShaderAddress);
                        // The storage submit consumes the global buffers (the
                        // presenter returns them) but never the vertex/index
                        // arrays; return those here so they don't leak the pool.
                        ReturnPooledDrawArrays(
                            translatedDraw,
                            globals: false,
                            vertex: true,
                            index: true);
                    }
                    else
                    {
                        if (translatedDraw.Textures.Count != 0)
                        {
                            // Unity's PS5 final blit can omit CB registers and
                            // rely on the following AGC flip to name the scanout
                            // target. Retain that sampled draw until RFlip, then
                            // enqueue it against the known display surface before
                            // the ordered capture.
                            state.PendingTargetlessDraw = translatedDraw;
                        }
                        else
                        {
                            // No render target, storage sink or sampled source:
                            // nothing can consume this draw.
                            ReturnPooledDrawArrays(
                                translatedDraw,
                                globals: true,
                                vertex: true,
                                index: true);
                        }
                    }
                }
            }

            if (ShouldTraceHotPath(ref _translatedDrawTraceCount))
            {
                TraceAgcShader(
                    $"agc.shader_draw_seen seq={drawSequence} " +
                    $"es=0x{exportShaderAddress:X16} ps=0x{pixelShaderAddress:X16} " +
                    $"target=0x{firstTarget.Address:X16}:{firstTarget.Width}x{firstTarget.Height}:fmt{firstTarget.Format}/tile{firstTarget.TileMode} " +
                    $"textures={translatedDraw.Textures.Count}");
            }

            // Trace-only: gated on the flag so the dedup set and the dump —
            // which reads pooled buffer data the presenter may already have
            // recycled (harmless for diagnostics, garbage bytes at worst) —
            // cost nothing in normal runs.
            if (_traceAgcShader)
            {
                lock (_submitTraceGate)
                {
                    var firstTextureAddress = translatedDraw.Textures.FirstOrDefault()?.Descriptor.Address ?? 0;
                    if (_tracedShaderDraws.Add(
                            (exportShaderAddress, pixelShaderAddress, firstTarget.Address, firstTextureAddress, vertexCount)))
                    {
                        TraceTranslatedGuestDraw(
                            ctx,
                            gpuState,
                            state,
                            translatedDraw,
                            psInputEna,
                            psInputAddr);
                    }
                }
            }

            return;
        }

        TraceDrawCompactMiss(
            drawSequence,
            vertexCount,
            hasExportShader && hasPixelShader
                ? translationError
                : hasDepthOnlyCandidate && !string.IsNullOrEmpty(translationError)
                    ? $"depth-only: {translationError}"
                : $"missing-shaders es={hasExportShader} ps={hasPixelShader} ena={hasPsInputEna} addr={hasPsInputAddr}");
        TraceShaderTranslationMiss(
            ctx,
            state,
            vertexCount,
            hasExportShader,
            exportShaderAddress,
            hasPixelShader,
            pixelShaderAddress,
            hasPsInputEna,
            psInputEna,
            hasPsInputAddr,
            psInputAddr,
            hasExportShader && hasPixelShader || hasDepthOnlyCandidate
                ? translationError
                : null);
    }

    private static bool TryCreateTranslatedDepthOnlyGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        uint vertexCount,
        bool indexed,
        GuestDepthTarget depthTarget,
        out TranslatedGuestDraw draw,
        out string error)
    {
        draw = default!;
        error = string.Empty;
        ulong exportShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
        }

        TryRegisterEmbeddedFusedProgram(ctx, exportShaderAddress, exportShaderHeader);

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out error,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase))
        {
            return false;
        }

        // Embedded fetch prologs add their own base to gl_VertexID.  Keep the
        // host draw offset at zero in that case; otherwise Vulkan applies the
        // same first-vertex adjustment a second time.
        var baseVertex = GetBaseVertex(state, exportState);
        var vertexBufferBaseVertex = GetVertexBufferBaseVertex(exportState);
        var recordBaseVertex = GetVertexRecordBaseVertex(state, exportState);
        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    recordBaseVertex,
                    out var depthVertexRecords)
                        ? depthVertexRecords
                        : null))
        {
            return false;
        }

        var exportFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var cacheKey = (
            exportShaderAddress,
            exportFingerprint,
            _storageBufferOffsetAlignment);
        _depthOnlyVertexShaderCache.TryGetValue(cacheKey, out var vertexShader);

        if (vertexShader is null)
        {
            var guestGlobalBufferCount = exportEvaluation.GlobalMemoryBindings.Count;
            // CreateTranslatedDrawGlobalBuffers appends both stage scalar
            // blocks.  The pixel block is unused by the fixed fragment stage;
            // the vertex block remains at guestCount+1, matching this layout.
            var totalGlobalBufferCount = _bakeScalars
                ? guestGlobalBufferCount
                : guestGlobalBufferCount + 2;
            if (!GuestGpu.Current.TryCompileVertexShader(
                    exportState,
                    exportEvaluation,
                    out vertexShader,
                    out error,
                    globalBufferBase: 0,
                    totalGlobalBufferCount: totalGlobalBufferCount,
                    imageBindingBase: 0,
                    scalarRegisterBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount + 1,
                    requiredVertexOutputCount: 0,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                return false;
            }

            DumpCompiledShader(
                "depth-vs",
                exportShaderAddress,
                exportFingerprint,
                vertexShader!,
                exportState.Program);
            GuestGpu.Current.CountShaderCompilation();
            _depthOnlyVertexShaderCache.TryAdd(cacheKey, vertexShader!);
        }

        var textures = new List<TranslatedImageBinding>(
            exportEvaluation.ImageBindings.Count);
        foreach (var binding in exportEvaluation.ImageBindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                if (_strictShaderDescriptors)
                {
                    error = $"invalid export texture descriptor at pc=0x{binding.Pc:X}";
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    return false;
                }

                texture = CreateFallbackTextureDescriptor(
                    binding.ResourceDescriptor,
                    binding.Control.Dimension);
            }

            textures.Add(new TranslatedImageBinding(
                texture,
                Gen5ShaderTranslator.RequiresStorageImage(
                    binding,
                    exportEvaluation.ImageBindings),
                binding.MipLevel ?? 0,
                NormalizeSamplerDescriptorForImageOperation(
                    binding.SamplerDescriptor),
                Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
        }

        IReadOnlyList<Gen5VertexInputBinding> vertexInputs =
            exportEvaluation.VertexInputs ?? [];
        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var syntheticTarget = new RenderTargetDescriptor(
            Slot: 0,
            Address: 0,
            depthTarget.Width,
            depthTarget.Height,
            Format: 0,
            NumberType: 0,
            ComponentSwap: 0,
            TileMode: 0);
        var renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
        {
            // A guest pass without a pixel shader has no colour exports.  The
            // presenter uses a private compatibility attachment, so disable
            // all writes to it and expose only the persistent DB result.
            Blends = [GuestBlendState.Default with { WriteMask = 0 }],
        };
        if (depthTarget.Width == 1 &&
            depthTarget.Height == 1 &&
            renderState.Viewport is { } depthViewport)
        {
            var inferredWidth = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Width)),
                1f,
                16384f);
            var inferredHeight = (uint)Math.Clamp(
                MathF.Ceiling(MathF.Abs(depthViewport.Height)),
                1f,
                16384f);
            if (inferredWidth > 1 || inferredHeight > 1)
            {
                depthTarget = depthTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                syntheticTarget = syntheticTarget with
                {
                    Width = inferredWidth,
                    Height = inferredHeight,
                };
                renderState = CreateRenderState(state.CxRegisters, syntheticTarget) with
                {
                    Blends = [GuestBlendState.Default with { WriteMask = 0 }],
                };
            }
        }
        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            PixelShaderAddress: 0,
            primitiveType,
            vertexShader!,
            GuestGpu.Current.GetDepthOnlyFragmentShader(),
            AttributeCount: 0,
            vertexCount,
            state.InstanceCount,
            baseVertex,
            vertexBufferBaseVertex,
            indexed ? CreateGuestIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            exportEvaluation.GlobalMemoryBindings,
            vertexInputs,
            RenderTargets: [],
            depthTarget,
            GuestTargets: [],
            renderState,
            PixelUserData: [],
            RawBlendControl: 0,
            RawColorInfo: 0,
            PixelInitialScalars: [],
            exportEvaluation.InitialScalarRegisters);
        return true;
    }

    private static bool TryCreateTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ulong pixelShaderAddress,
        uint psInputEna,
        uint psInputAddr,
        uint vertexCount,
        bool indexed,
        out TranslatedGuestDraw draw,
        out string error)
    {
        draw = default!;
        error = string.Empty;
        ulong exportShaderHeader;
        ulong pixelShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
            _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
        }

        TryRegisterEmbeddedFusedProgram(ctx, exportShaderAddress, exportShaderHeader);

        // Sequential (not short-circuited into one condition) so a failure
        // after an evaluation succeeded can return that evaluation's pooled
        // buffer arrays to the pool instead of leaking them.
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out error,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase))
        {
            return false;
        }

        // Embedded fetch prologs add their own base to gl_VertexID.  Keep the
        // host draw offset at zero in that case; otherwise Vulkan applies the
        // same first-vertex adjustment a second time.
        var baseVertex = GetBaseVertex(state, exportState);
        var vertexBufferBaseVertex = GetVertexBufferBaseVertex(exportState);
        var recordBaseVertex = GetVertexRecordBaseVertex(state, exportState);

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var exportEvaluation,
                out error,
                resolveVertexInputs: true,
                requiredVertexRecordCount: TryGetRequiredVertexRecordCount(
                    ctx,
                    state,
                    vertexCount,
                    indexed,
                    recordBaseVertex,
                    out var vertexRecords)
                        ? vertexRecords
                        : null))
        {
            return false;
        }

        ApplySubmittedVertexSnapshot(
            state,
            exportShaderAddress,
            ref exportEvaluation);

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                pixelShaderAddress,
                pixelShaderHeader,
                state.ShRegisters,
                PsTextureUserDataRegister,
                out var pixelState,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        if (!Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                pixelState,
                out var pixelEvaluation,
                out error))
        {
            ReturnPooledEvaluationArrays(exportEvaluation);
            return false;
        }

        // Empty SRT/EUD is fine for clears/passthroughs that bind nothing
        // (Astro title PS 0x808E88000 is a procedural fullscreen clear).
        // Reject only when evaluation produced image/global slots that
        // collapsed to Address-0 — that layout mismatches SPIR-V and loses
        // the device on QueueSubmit.
        if (pixelState.Metadata is
            {
                ShaderResourceTableSizeDwords: 0,
                ExtendedUserDataSizeDwords: 0,
            } ||
            Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(
                pixelShaderAddress))
        {
            var hasAnyImageSlot = pixelEvaluation.ImageBindings.Count > 0;
            var hasUsablePixelImage = false;
            foreach (var binding in pixelEvaluation.ImageBindings)
            {
                if (TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture) &&
                    texture.Address != 0)
                {
                    hasUsablePixelImage = true;
                    break;
                }
            }

            var hasUsablePixelGlobal = pixelEvaluation.GlobalMemoryBindings.Any(
                static binding => binding.BaseAddress != 0);
            var hasPoisonImageSlots = hasAnyImageSlot && !hasUsablePixelImage;
            if (hasPoisonImageSlots && !hasUsablePixelGlobal)
            {
                error = Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(
                    pixelShaderAddress)
                    ? "empty-srt-scalar-pointer-fallback"
                    : "empty-srt-no-usable-resources";
                lock (_submitTraceGate)
                {
                    if (_tracedEmptySrtDrawRejects.Add(pixelShaderAddress))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject ps=0x{pixelShaderAddress:X16} " +
                            $"es=0x{exportShaderAddress:X16} reason={error}");
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject_state ps=0x{pixelShaderAddress:X16} " +
                            $"header=0x{pixelShaderHeader:X16} " +
                            Gen5ShaderTranslator.DescribeState(pixelState));
                        var shDump = new List<string>(16);
                        for (uint reg = 0x8; reg <= 0x1C; reg++)
                        {
                            if (state.ShRegisters.TryGetValue(reg, out var value))
                            {
                                shDump.Add($"0x{reg:X}={value:X8}");
                            }
                        }

                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.draw_reject_sh ps=0x{pixelShaderAddress:X16} " +
                            $"[{string.Join(',', shDump)}]");
                        var bindingIndex = 0;
                        foreach (var binding in pixelEvaluation.ImageBindings)
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] agc.draw_reject_binding ps=0x{pixelShaderAddress:X16} " +
                                $"[{bindingIndex++}] pc=0x{binding.Pc:X} op={binding.Opcode} " +
                                $"resource={FormatShaderDwords(binding.ResourceDescriptor)} " +
                                $"sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
                        }

                        foreach (var binding in pixelEvaluation.GlobalMemoryBindings)
                        {
                            Console.Error.WriteLine(
                                $"[LOADER][WARN] agc.draw_reject_global ps=0x{pixelShaderAddress:X16} " +
                                $"s{binding.ScalarAddress} base=0x{binding.BaseAddress:X16} " +
                                $"bytes={binding.DataLength}");
                        }
                    }
                }

                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS") == "1")
        {
            TraceAstroTitlePixelGlobals(pixelEvaluation);
        }

        if (pixelShaderAddress == 0x0000000500781200 &&
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_GLOBALS_LIVE") == "1")
        {
            TraceAstroTitlePixelGlobalProbe(pixelEvaluation);
        }

        // Patch BufferFormat from the attrib table onto the V# before host
        // vertex input. IR discovery often keeps a stale float format from the
        // unpatched sharp — that turns UI glyphs into gradient triangles.
        // Match by stride+offset (not bare base address) so interleaved streams
        // keep loading-video bindings intact.
        if (exportEvaluation.VertexInputs is { Count: > 0 } discoveredInputs &&
            AgcVertexMetadata.TryGetVertexTableRegisters(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                out var vertexTables))
        {
            var resolvedVertexTables =
                AgcVertexMetadata.AddUserDataScalarRegisterBase(
                    vertexTables,
                    exportState.UserDataScalarRegisterBase);
            var merged = AgcVertexMetadata.MergeVertexInputsFromMetadata(
                ctx,
                exportEvaluation.InitialScalarRegisters,
                resolvedVertexTables,
                exportState.Program,
                discoveredInputs);
            if (!ReferenceEquals(merged, discoveredInputs))
            {
                TraceAgcShader(
                    $"agc.vertex_metadata_format es=0x{exportShaderAddress:X16} " +
                    $"count={merged.Count}");
                exportEvaluation = exportEvaluation with { VertexInputs = merged };
            }
        }

        // Every bound color target the shader exports to. Deferred renderers
        // draw a multi-render-target G-buffer (up to eight slots) in one pass.
        // Fall back to slot 0 if we cannot match any export to a bound target.
        var pixelColorExportMasks = pixelState.Program.PixelColorExportMasks;
        var allBoundTargets = GetRenderTargets(state.CxRegisters);
        // At most 8 slots; a manual filter avoids the per-draw LINQ iterator/
        // closure allocations. Slots are distinct, so sorting by slot is stable.
        var selectedTargets = new List<RenderTargetDescriptor>(allBoundTargets.Count);
        foreach (var target in allBoundTargets)
        {
            if (GetPixelColorExportMask(pixelColorExportMasks, target.Slot) != 0)
            {
                selectedTargets.Add(target);
            }
        }

        if (selectedTargets.Count == 0)
        {
            foreach (var target in allBoundTargets)
            {
                if (target.Slot == 0)
                {
                    selectedTargets.Add(target);
                }
            }
        }

        selectedTargets.Sort(static (left, right) => left.Slot.CompareTo(right.Slot));
        var renderTargets = selectedTargets.ToArray();
        if (_traceAgcShader && allBoundTargets.Count > 1)
        {
            TraceAgcShader(
                $"agc.mrt_filter ps=0x{pixelShaderAddress:X16} " +
                $"bound=[{string.Join(",", allBoundTargets.Select(t => $"s{t.Slot}:0x{t.Address:X}:exp{(GetPixelColorExportMask(pixelColorExportMasks, t.Slot) != 0 ? 1 : 0)}"))}] " +
                 $"kept={renderTargets.Length}");
        }

        var renderTargetOutputKinds = new Gen5PixelOutputKind[renderTargets.Length];
        var renderTargetOutputMappings = new Gen5ColorComponentMapping[renderTargets.Length];
        for (var index = 0; index < renderTargets.Length; index++)
        {
            var target = renderTargets[index];
            if (!GuestGpu.Current.TryGetRenderTargetOutputInfo(
                    target.Format,
                    target.NumberType,
                    target.ComponentSwap,
                    out renderTargetOutputKinds[index],
                    out renderTargetOutputMappings[index]))
            {
                error =
                    $"unsupported color target format={target.Format} " +
                    $"number_type={target.NumberType} component_swap={target.ComponentSwap}";
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }
        }

        // Exact packed encoding of the output layout — guest slot (6 bits, CB targets are
        // 0-7) plus output kind (2 bits) per target, host locations being the sequential
        // byte positions. Replaces a per-draw LINQ + string build that allocated on every
        // draw, cache hit or not; the target count disambiguates trailing zero bytes.
        var outputLayout = 0UL;
        for (var index = 0; index < renderTargets.Length; index++)
        {
            outputLayout |= (ulong)(((renderTargets[index].Slot & 0x3Fu) << 2) |
                (uint)renderTargetOutputKinds[index]) << (index * 8);
        }
        var outputMappings = PackPixelOutputMappings(renderTargetOutputMappings);

        var attributeCount = GetInterpolatedAttributeCount(pixelState);
        var exportStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(exportEvaluation)
            : ComputeShaderStructuralFingerprint(exportEvaluation);
        var pixelStateFingerprint = _bakeScalars
            ? ComputeShaderStateFingerprint(pixelEvaluation)
            : ComputeShaderStructuralFingerprint(pixelEvaluation);
        var psInputCntl = ReadPsInputCntlRegisters(state.CxRegisters);
        var psInputCntlFingerprint = ComputePsInputCntlFingerprint(psInputCntl);
        var shaderKey = (
            exportShaderAddress,
            exportStateFingerprint,
            pixelShaderAddress,
            pixelStateFingerprint,
            outputLayout,
            outputMappings,
            (uint)renderTargets.Length,
            attributeCount,
            psInputEna,
            psInputAddr,
            psInputCntlFingerprint,
            _storageBufferOffsetAlignment);

        var guestGlobalBuffers =
            pixelEvaluation.GlobalMemoryBindings.Count +
            exportEvaluation.GlobalMemoryBindings.Count;
        // Two per-draw initial-scalar buffers ride after the guest buffers:
        // [pixel guest][vertex guest][pixel sgprs][vertex sgprs].
        var totalGlobalBuffers = _bakeScalars
            ? guestGlobalBuffers
            : guestGlobalBuffers + 2;
        _graphicsShaderCache.TryGetValue(shaderKey, out var compiled);
        var usedFixedFullscreenClear = false;
        (float Red, float Green, float Blue, float Alpha) fullscreenClearColor = default;

        if (compiled.Vertex is null || compiled.Pixel is null)
        {
            if (IsProceduralFullscreenClearPair(
                    exportState,
                    exportEvaluation,
                    pixelState,
                    pixelEvaluation))
            {
                // Title ES/PS clear (0x808E88D00/0x808E88000): empty SRT/EUD.
                // Gen5→SPIR-V and even fixed fragment pipelines have lost the
                // device on the 2432x1368 offscreen submit. Apply the solid
                // clear via CmdClearColorImage so the pass still runs without
                // Address-0 descriptors or a graphics pipeline.
                usedFixedFullscreenClear = true;
                fullscreenClearColor = DecodeSolidClearColor(pixelEvaluation);
                lock (_submitTraceGate)
                {
                    if (_tracedFixedFullscreenClears.Add(
                            (exportShaderAddress, pixelShaderAddress)))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] agc.shader_color_clear " +
                            $"es=0x{exportShaderAddress:X16} " +
                            $"ps=0x{pixelShaderAddress:X16} " +
                            $"rgba=({fullscreenClearColor.Red:0.###}," +
                            $"{fullscreenClearColor.Green:0.###}," +
                            $"{fullscreenClearColor.Blue:0.###}," +
                            $"{fullscreenClearColor.Alpha:0.###})");
                    }
                }

                compiled = (
                    GuestGpu.Current.GetDepthOnlyFragmentShader(),
                    GuestGpu.Current.GetDepthOnlyFragmentShader());
                _graphicsShaderCache.TryAdd(shaderKey, compiled);
            }
            else
            {
                var pixelOutputs = new Gen5PixelOutputBinding[renderTargets.Length];
                for (var location = 0; location < renderTargets.Length; location++)
                {
                    pixelOutputs[location] = new Gen5PixelOutputBinding(
                        renderTargets[location].Slot,
                        (uint)location,
                        renderTargetOutputKinds[location],
                        renderTargetOutputMappings[location]);
                }

                if (!GuestGpu.Current.TryCompilePixelShader(
                        pixelState,
                        pixelEvaluation,
                        pixelOutputs,
                        out var pixelShader,
                        out error,
                        globalBufferBase: 0,
                        totalGlobalBufferCount: totalGlobalBuffers,
                        imageBindingBase: 0,
                        scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers,
                        pixelInputEnable: psInputEna,
                        pixelInputAddress: psInputAddr,
                        pixelInputCntl: psInputCntl,
                        storageBufferOffsetAlignment:
                            _storageBufferOffsetAlignment) ||
                    !GuestGpu.Current.TryCompileVertexShader(
                        exportState,
                        exportEvaluation,
                        out var vertexShader,
                        out error,
                        globalBufferBase: pixelEvaluation.GlobalMemoryBindings.Count,
                        totalGlobalBufferCount: totalGlobalBuffers,
                        imageBindingBase: pixelEvaluation.ImageBindings.Count,
                        scalarRegisterBufferIndex: _bakeScalars ? -1 : guestGlobalBuffers + 1,
                        requiredVertexOutputCount: (int)GetInterpolatedAttributeCount(pixelState),
                        storageBufferOffsetAlignment:
                            _storageBufferOffsetAlignment))
                {
                    ReturnPooledEvaluationArrays(exportEvaluation);
                    ReturnPooledEvaluationArrays(pixelEvaluation);
                    return false;
                }

                compiled = (vertexShader!, pixelShader!);
                DumpCompiledShader(
                    "vs",
                    exportShaderAddress,
                    exportStateFingerprint,
                    compiled.Vertex,
                    exportState.Program);
                DumpCompiledShader(
                    "ps",
                    pixelShaderAddress,
                    pixelStateFingerprint,
                    compiled.Pixel,
                    pixelState.Program);
                GuestGpu.Current.CountShaderCompilation();
                _graphicsShaderCache.TryAdd(shaderKey, compiled);
            }
        }
        else if (IsCachedFixedFullscreenClearPair(
                     exportState,
                     exportEvaluation,
                     pixelState,
                     pixelEvaluation))
        {
            usedFixedFullscreenClear = true;
            fullscreenClearColor = DecodeSolidClearColor(pixelEvaluation);
        }

        var useFixedFullscreenClear = usedFixedFullscreenClear;

        List<TranslatedImageBinding> textures;
        Gen5GlobalMemoryBinding[] globalMemoryBindings;
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs;
        if (useFixedFullscreenClear)
        {
            textures = [];
            globalMemoryBindings = [];
            vertexInputs = [];
        }
        else
        {
            var imageBindings = pixelEvaluation.ImageBindings
                .Concat(exportEvaluation.ImageBindings)
                .ToArray();
            textures = new List<TranslatedImageBinding>(
                pixelEvaluation.ImageBindings.Count +
                exportEvaluation.ImageBindings.Count);
            if (!TryAppendTranslatedImageBindings(
                    pixelEvaluation.ImageBindings,
                    imageBindings,
                    textures,
                    pixelShaderAddress,
                    exportShaderAddress,
                    out error) ||
                !TryAppendTranslatedImageBindings(
                    exportEvaluation.ImageBindings,
                    imageBindings,
                    textures,
                    pixelShaderAddress,
                    exportShaderAddress,
                    out error))
            {
                ReturnPooledEvaluationArrays(exportEvaluation);
                ReturnPooledEvaluationArrays(pixelEvaluation);
                return false;
            }

            globalMemoryBindings = new Gen5GlobalMemoryBinding[
                pixelEvaluation.GlobalMemoryBindings.Count +
                exportEvaluation.GlobalMemoryBindings.Count];
            for (var index = 0; index < pixelEvaluation.GlobalMemoryBindings.Count; index++)
            {
                globalMemoryBindings[index] = pixelEvaluation.GlobalMemoryBindings[index];
            }
            for (var index = 0; index < exportEvaluation.GlobalMemoryBindings.Count; index++)
            {
                globalMemoryBindings[pixelEvaluation.GlobalMemoryBindings.Count + index] =
                    exportEvaluation.GlobalMemoryBindings[index];
            }

            vertexInputs = exportEvaluation.VertexInputs ?? [];
        }

        state.UcRegisters.TryGetValue(VgtPrimitiveType, out var primitiveType);
        var guestTargets = new GuestRenderTarget[renderTargets.Length];
        for (var index = 0; index < renderTargets.Length; index++)
        {
            guestTargets[index] = CreateGuestRenderTarget(renderTargets[index]);
        }

        var pixelUserDataCount = Math.Min(pixelEvaluation.InitialScalarRegisters.Count, 8);
        var pixelUserData = new uint[pixelUserDataCount];
        for (var index = 0; index < pixelUserDataCount; index++)
        {
            pixelUserData[index] = pixelEvaluation.InitialScalarRegisters[index];
        }

        var renderState = ApplyTransparentPremultipliedFillClear(
            CreateRenderState(
                state.CxRegisters,
                renderTargets,
                pixelColorExportMasks,
                renderTargetOutputMappings),
            textures,
            vertexInputs,
            pixelEvaluation.InitialScalarRegisters);

        draw = new TranslatedGuestDraw(
            exportShaderAddress,
            pixelShaderAddress,
            primitiveType,
            compiled.Vertex,
            compiled.Pixel,
            GetInterpolatedAttributeCount(pixelState),
            vertexCount,
            state.InstanceCount,
            baseVertex,
            vertexBufferBaseVertex,
            indexed ? CreateGuestIndexBuffer(ctx, state, vertexCount) : null,
            textures,
            globalMemoryBindings,
            vertexInputs,
            renderTargets,
            DecodeDepthTarget(state.CxRegisters),
            guestTargets,
            renderState,
            pixelUserData,
            state.CxRegisters.TryGetValue(CbBlend0Control, out var rawBlend) ? rawBlend : 0,
            state.CxRegisters.TryGetValue(
                CbColor0Info + renderTargets.FirstOrDefault().Slot * CbColorRegisterStride,
                out var rawInfo)
                ? rawInfo
                : 0,
            pixelEvaluation.InitialScalarRegisters,
            exportEvaluation.InitialScalarRegisters,
            useFixedFullscreenClear,
            fullscreenClearColor.Red,
            fullscreenClearColor.Green,
            fullscreenClearColor.Blue,
            fullscreenClearColor.Alpha,
            IsDccFastClearDraw(
                state.CxRegisters,
                renderTargets,
                textures,
                vertexInputs,
                renderState,
                primitiveType,
                vertexCount));
        return true;
    }

    private static bool TryAppendTranslatedImageBindings(
        IReadOnlyList<Gen5ImageBinding> bindings,
        IReadOnlyList<Gen5ImageBinding> stageBindings,
        List<TranslatedImageBinding> textures,
        ulong pixelShaderAddress,
        ulong exportShaderAddress,
        out string error)
    {
        foreach (var binding in bindings)
        {
            if (!TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture))
            {
                // A garbage/zeroed texture descriptor (from a per-draw descriptor
                // setup race — the same root as scalar-load-failed) would drop
                // the whole draw, so deferred-lighting/composite passes that
                // produce the composite's feeder targets never run. Keep the
                // existing 1x1 fallback unless strict diagnostics are requested.
                if (_strictShaderDescriptors)
                {
                    error = $"invalid texture descriptor at pc=0x{binding.Pc:X}";
                    return false;
                }

                texture = CreateFallbackTextureDescriptor(
                    binding.ResourceDescriptor,
                    binding.Control.Dimension);
            }

            var isStorage = Gen5ShaderTranslator.RequiresStorageImage(
                binding,
                stageBindings);
            if (_traceAgcShader || _tracePixelShaderAddress == pixelShaderAddress)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"agc.texture_binding ps=0x{pixelShaderAddress:X16} es=0x{exportShaderAddress:X16} " +
                    $"pc=0x{binding.Pc:X} op={binding.Opcode} storage={(isStorage ? 1 : 0)} " +
                    $"decoded={FormatTextureDescriptor(texture)} " +
                    $"raw={FormatShaderDwords(binding.ResourceDescriptor)} sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
            }
            textures.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    binding.MipLevel ?? 0,
                    NormalizeSamplerDescriptorForImageOperation(
                        binding.SamplerDescriptor),
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
        }

        error = string.Empty;
        return true;
    }

    private static int _tracedAstroTitlePixelGlobals;
    private static int _tracedAstroTitlePixelGlobalProbe;

    private static void TraceAstroTitlePixelGlobalProbe(Gen5ShaderEvaluation evaluation)
    {
        const int probeOffset = 17216;
        var draw = Interlocked.Increment(ref _tracedAstroTitlePixelGlobalProbe);
        foreach (var (binding, index) in evaluation.GlobalMemoryBindings.Select((value, index) => (value, index)))
        {
            if (probeOffset + 16 > binding.DataLength)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"[TITLE-GLOBALS-LIVE] draw={draw} binding={index} " +
                $"base=0x{binding.BaseAddress:X16} offset=0x{probeOffset:X} " +
                $"bytes={Convert.ToHexString(binding.Data.AsSpan(probeOffset, 16))}");
        }
    }

    private static void TraceAstroTitlePixelGlobals(Gen5ShaderEvaluation evaluation)
    {
        if (Interlocked.Exchange(ref _tracedAstroTitlePixelGlobals, 1) != 0)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[TITLE-GLOBALS] initial_s0_31=" +
            string.Join(',', evaluation.InitialScalarRegisters
                .Take(32)
                .Select((value, index) => $"s{index}={value:X8}")));

        var probeOffsets = new[]
        {
            0, 16, 24, 32, 48,
            192, 256, 400, 432,
            17100, 17104, 17136, 17168, 17184, 17200, 17216,
        };
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Console.Error.WriteLine(
                $"[TITLE-GLOBALS] binding s{binding.ScalarAddress} " +
                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
            foreach (var offset in probeOffsets)
            {
                if (offset < 0 || offset + 16 > binding.DataLength)
                {
                    continue;
                }

                Console.Error.WriteLine(
                    $"[TITLE-GLOBALS] s{binding.ScalarAddress}+0x{offset:X}=" +
                    Convert.ToHexString(binding.Data.AsSpan(offset, 16)));
            }
        }
    }

    private static bool IsCachedFixedFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation) =>
        IsProceduralFullscreenClearPair(
            exportState,
            exportEvaluation,
            pixelState,
            pixelEvaluation);

    private static bool IsProceduralFullscreenClearPair(
        Gen5ShaderState exportState,
        Gen5ShaderEvaluation exportEvaluation,
        Gen5ShaderState pixelState,
        Gen5ShaderEvaluation pixelEvaluation)
    {
        if ((exportEvaluation.VertexInputs?.Count ?? 0) != 0 ||
            exportEvaluation.ImageBindings.Count != 0 ||
            pixelEvaluation.ImageBindings.Count != 0 ||
            exportEvaluation.GlobalMemoryBindings.Count != 0 ||
            pixelEvaluation.GlobalMemoryBindings.Count != 0)
        {
            return false;
        }

        if (!HasExportTarget(exportState, target: 12) ||
            !HasExportTarget(pixelState, target: 0))
        {
            return false;
        }

        if (pixelState.Program.Instructions.Count is 0 or > 8 ||
            exportState.Program.Instructions.Count is 0 or > 48)
        {
            return false;
        }

        return pixelState.Program.Instructions.All(IsBenignClearPixelInstruction) &&
               exportState.Program.Instructions.All(IsBenignProceduralVertexInstruction);
    }

    private static bool HasExportTarget(Gen5ShaderState state, uint target) =>
        state.Program.Instructions.Any(instruction =>
            instruction.Control is Gen5ExportControl export &&
            export.Target == target);

    private static bool IsBenignClearPixelInstruction(Gen5ShaderInstruction instruction) =>
        instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "VMovB32" ||
        instruction.Control is Gen5ExportControl { Target: 0 };

    private static bool IsBenignProceduralVertexInstruction(Gen5ShaderInstruction instruction)
    {
        if (instruction.Control is Gen5BufferMemoryControl or
            Gen5ImageControl or
            Gen5GlobalMemoryControl or
            Gen5ScalarMemoryControl)
        {
            return false;
        }

        if (instruction.Control is Gen5ExportControl export)
        {
            // Position (12) plus ignored NGG/param exports.
            return export.Target is 12 or (>= 13 and < 32) or 20;
        }

        return instruction.Opcode is
            "SNop" or
            "SWaitcnt" or
            "SInstPrefetch" or
            "SEndpgm" or
            "SSendmsg" or
            "VMovB32" or
            "VAndB32" or
            "VAddI32" or
            "VLshlrevB32" or
            "VCvtF32I32" or
            "VCvtF32U32" ||
            instruction.Encoding is
                Gen5ShaderEncoding.Sop1 or
                Gen5ShaderEncoding.Sop2 or
                Gen5ShaderEncoding.Sopc or
                Gen5ShaderEncoding.Sopk or
                Gen5ShaderEncoding.Sopp;
    }

    private static (float Red, float Green, float Blue, float Alpha) DecodeSolidClearColor(
        Gen5ShaderEvaluation pixelEvaluation)
    {
        // Default opaque white; guest clear shaders often mov a 1.0 literal into v0.
        float red = 1f, green = 1f, blue = 1f, alpha = 1f;
        if (pixelEvaluation.InitialScalarRegisters.Count > 0)
        {
            var bits = pixelEvaluation.InitialScalarRegisters[0];
            if (bits != 0)
            {
                red = green = blue = alpha = BitConverter.UInt32BitsToSingle(bits);
                if (!float.IsFinite(red) || red < 0f || red > 4f)
                {
                    red = green = blue = alpha = 1f;
                }
            }
        }

        return (red, green, blue, alpha);
    }

    private static readonly bool _fillClearHack = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DISABLE_FILL_CLEAR"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Treat an untextured fill that outputs pure transparent black through
    /// premultiplied blending as an overwrite. Chowdren issues exactly this
    /// draw once per frame to reset its effect layers (fog smoke, vignette
    /// masks); under the blend factors it sets (One, OneMinusSrcAlpha) a
    /// (0,0,0,0) source is a mathematical no-op, so without this the layers
    /// accumulate until they saturate and the fog composites as a flat veil
    /// over the whole scene. The workaround applies only when every MRT
    /// attachment uses the same blend pattern. Disable with
    /// SHARPEMU_DISABLE_FILL_CLEAR=1.
    /// </summary>
    private static GuestRenderState ApplyTransparentPremultipliedFillClear(
        GuestRenderState renderState,
        IReadOnlyList<TranslatedImageBinding> textures,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        IReadOnlyList<uint> pixelUserData)
    {
        if (!_fillClearHack ||
            textures.Count != 0 ||
            vertexInputs.Count != 0 ||
            pixelUserData.Count < 4 ||
            !renderState.Blends.All(IsTransparentPremultipliedFillBlend))
        {
            return renderState;
        }

        for (var index = 0; index < 4; index++)
        {
            // Positive or negative zero.
            if ((pixelUserData[index] & 0x7FFF_FFFFu) != 0)
            {
                return renderState;
            }
        }

        return renderState with
        {
            Blends = renderState.Blends
                .Select(blend => blend with { Enable = false })
                .ToArray(),
        };
    }

    /// <summary>
    /// Recognises the covering quad a GFX10 driver issues to clear a
    /// DCC-compressed colour target. There is no clear packet: the driver
    /// programs CB_COLORn_CLEAR_WORD0/1 and draws a quad that the colour block
    /// turns into DCC clear codes, discarding whatever the pixel shader
    /// exported. Executing it as an ordinary draw writes the shaded output
    /// instead, and because the blend it uses computes
    /// <c>a &lt;- a_src + a_dst * (1 - a_src)</c> - fixed point 1 - the target's
    /// alpha then climbs every frame and saturates.
    ///
    /// Restricted to clear-to-zero. The reset performed for a match clears the
    /// attachment to zero, so a nonzero CLEAR_WORD would be cleared to the
    /// wrong colour; those fall through and are drawn. Zero is zero under every
    /// encoding the register can carry, so the pair needs no format handling.
    ///
    /// The clip-space test is load-bearing rather than belt-and-braces: fills
    /// sharing the vertex count, topology and blend outnumber the clears by two
    /// orders of magnitude and sit at coordinates well outside the frame.
    /// </summary>
    private const uint TriangleStripPrimitive = 6;

    // A float32x3 vertex position stream (BUF_DATA_FORMAT_32_32_32 / FLOAT).
    private const uint PositionDataFormat = 13;
    private const uint PositionNumberFormat = 7;

    private static bool IsDccFastClearDraw(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> renderTargets,
        IReadOnlyList<TranslatedImageBinding> textures,
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        GuestRenderState renderState,
        uint primitiveType,
        uint vertexCount)
    {
        if (textures.Count != 0 ||
            vertexCount != 4 ||
            primitiveType != TriangleStripPrimitive ||
            renderTargets.Count == 0 ||
            renderState.Blends.Count == 0 ||
            !renderState.Blends.All(IsTransparentPremultipliedFillBlend))
        {
            return false;
        }

        var slotStride = renderTargets[0].Slot * CbColorRegisterStride;
        return registers.TryGetValue(CbColor0Info + slotStride, out var info) &&
            (info & CbColorInfoDccEnableMask) != 0 &&
            registers.TryGetValue(CbColor0ClearWord0 + slotStride, out var clearWord0) &&
            registers.TryGetValue(CbColor0ClearWord1 + slotStride, out var clearWord1) &&
            clearWord0 == 0 &&
            clearWord1 == 0 &&
            CoversClipSpace(vertexInputs, vertexCount);
    }

    /// <summary>
    /// GFX10 CMASK fast clear: CB_COLORn_INFO.FAST_CLEAR (bit 12) set on
    /// one or more targets. The CB clears via CMASK before the draw writes;
    /// mark targets for clear-on-first-use. Unlike DCC, the draw content
    /// IS written (not dropped). Dead Cells uses DbRenderControl CLEARON
    /// instead (bit0), not this mechanism.
    /// </summary>
    private static bool IsCmaskFastClearDraw(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> renderTargets)
    {
        foreach (var rt in renderTargets)
        {
            var stride = rt.Slot * CbColorRegisterStride;
            if (registers.TryGetValue(CbColor0Info + stride, out var info) &&
                (info & CbColorInfoFastClearEnableMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Registers the CMASK metadata mapping for each colour buffer.
    /// Does NOT mark as cleared — clearing only happens on actual clear
    /// events (DMA fill, compute write, EFC draw).
    /// </summary>
    private static void TrackCmaskAddresses(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> renderTargets)
    {
        foreach (var rt in renderTargets)
        {
            var stride = rt.Slot * CbColorRegisterStride;

            // CMASK metadata address (legacy GCN path).
            var cmaskRegAddr = CbColor0Cmask + stride;
            registers.TryGetValue(cmaskRegAddr, out var cmaskLow);
            var cmaskExtAddr = CbColor0CmaskBaseExt + rt.Slot;
            registers.TryGetValue(cmaskExtAddr, out var cmaskExt);
            var cmaskAddress = ((ulong)(cmaskExt & 0xFFu) << 40) |
                               ((ulong)(cmaskLow & 0x1FFFFFFFu) << 8);

            // DCC metadata address (GFX10+ primary path).
            var dccRegAddr = CbColor0DccBase + stride;
            registers.TryGetValue(dccRegAddr, out var dccLow);
            var dccExtAddr = CbColor0DccBaseExt + rt.Slot;
            registers.TryGetValue(dccExtAddr, out var dccExt);
            var dccAddress = ((ulong)(dccExt & 0xFFu) << 40) |
                             ((ulong)(dccLow & 0x1FFFFFFFu) << 8);

            // Prefer CMASK if present; fall back to DCC.
            var metaAddress = cmaskAddress != 0 ? cmaskAddress : dccAddress;

            var cw0Addr = CbColor0ClearWord0 + stride;
            var cw1Addr = CbColor0ClearWord1 + stride;
            registers.TryGetValue(cw0Addr, out var cw0);
            registers.TryGetValue(cw1Addr, out var cw1);

            lock (_metaSurfaceGate)
            {
                _metaSurfaces[rt.Address] = new MetaSurfaceInfo(
                    metaAddress, cw0, cw1,
                    // Re-registration runs on every draw; keep the cleared state
                    // so a mark-clear event survives until the pass consumes it.
                    // If the metadata binding changed, the old state refers to
                    // the old metadata and must be reset.
                    IsCleared: _metaSurfaces.TryGetValue(rt.Address, out var prev) &&
                               prev.IsCleared &&
                               prev.CmaskAddress == metaAddress);
                if (metaAddress != 0)
                {
                    _cmaskToColorBuffer[metaAddress] = rt.Address;
                }
            }
        }
    }

    /// <summary>
    /// Checks if a write targets a registered CMASK address. If so,
    /// marks the owning colour buffer's metadata as "all clear".
    /// </summary>
    private static void CheckCmaskWrite(
        ulong writeAddress,
        SubmittedGpuState? gpuState)
    {
        if (writeAddress == 0)
        {
            return;
        }

        lock (_metaSurfaceGate)
        {
            // Exact match: write directly to a registered CMASK address.
            if (_cmaskToColorBuffer.TryGetValue(writeAddress, out var cbAddr))
            {
                if (_metaSurfaces.TryGetValue(cbAddr, out var meta))
                {
                    _metaSurfaces[cbAddr] = meta with { IsCleared = true };
                }

                return;
            }

            // CMASK surfaces are small (typically ≤ 4 KiB).  Check the ±1024
            // window around each registered address to catch partial writes.
            foreach (var (cmaskAddr, colorBufAddr) in _cmaskToColorBuffer)
            {
                if (writeAddress >= cmaskAddr && writeAddress < cmaskAddr + 1024)
                {
                    if (_metaSurfaces.TryGetValue(colorBufAddr, out var meta))
                    {
                        _metaSurfaces[colorBufAddr] = meta with { IsCleared = true };
                    }

                    return;
                }
            }
        }
    }

    /// <summary>
    /// Returns true if the colour buffer at <paramref name="colorBufferAddress"/>
    /// has pending CMASK "all clear" metadata — i.e. the surface was fast-cleared
    /// but not yet rendered into.
    /// </summary>
    internal static bool IsMetaClearedForSurface(ulong colorBufferAddress)
    {
        lock (_metaSurfaceGate)
        {
            return _metaSurfaces.TryGetValue(colorBufferAddress, out var meta) &&
                   meta.IsCleared;
        }
    }

    /// <summary>
    /// Consumes the "all clear" state for the given surface, marking it dirty.
    /// Called after the first render pass uses LoadOp.Clear.
    /// </summary>
    internal static void ConsumeMetaClear(ulong colorBufferAddress)
    {
        lock (_metaSurfaceGate)
        {
            if (_metaSurfaces.TryGetValue(colorBufferAddress, out var meta))
            {
                _metaSurfaces[colorBufferAddress] = meta with { IsCleared = false };
            }
        }
    }

    /// <summary>
    /// Returns the CB clear word values for the given colour buffer.
    /// </summary>
    internal static (uint Cw0, uint Cw1) GetMetaClearValue(ulong colorBufferAddress)
    {
        lock (_metaSurfaceGate)
        {
            if (_metaSurfaces.TryGetValue(colorBufferAddress, out var meta))
            {
                return (meta.ClearWord0, meta.ClearWord1);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// Marks all registered surfaces as "all clear".  Called at guest flip
    /// (frame boundary).  Real hardware applies a fast clear / load-clear to
    /// its per-frame surfaces every frame; the emulator restores that
    /// per-frame clear here, per surface, at flip time.  This is the
    /// per-surface successor of the removed flip-arm heuristic (which reset
    /// only the first multi-attachment group).
    /// </summary>
    internal static void MarkAllSurfacesCleared()
    {
        lock (_metaSurfaceGate)
        {
            foreach (var (addr, meta) in _metaSurfaces)
            {
                _metaSurfaces[addr] = meta with { IsCleared = true };
            }
        }
    }

    /// <summary>
    /// True when the draw's float32x3 position stream spans the full clip
    /// rectangle, i.e. x and y both reach -1 and +1.
    /// </summary>
    private static bool CoversClipSpace(
        IReadOnlyList<Gen5VertexInputBinding> vertexInputs,
        uint vertexCount)
    {
        const float Tolerance = 0.001f;
        foreach (var input in vertexInputs)
        {
            if (input.DataFormat != PositionDataFormat ||
                input.NumberFormat != PositionNumberFormat)
            {
                continue;
            }

            var stride = input.Stride == 0 ? 12u : input.Stride;
            var available = Math.Min(input.DataLength, input.Data.Length);
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            var seen = 0;
            for (var vertex = 0u; vertex < vertexCount; vertex++)
            {
                var at = (int)(input.OffsetBytes + (vertex * stride));
                if (at + 12 > available)
                {
                    break;
                }

                var position = input.Data.AsSpan(at);
                var x = BitConverter.ToSingle(position);
                var y = BitConverter.ToSingle(position[4..]);
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    return false;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                seen++;
            }

            return seen >= 3 &&
                minX <= -1f + Tolerance && maxX >= 1f - Tolerance &&
                minY <= -1f + Tolerance && maxY >= 1f - Tolerance;
        }

        return false;
    }

    private static bool IsTransparentPremultipliedFillBlend(GuestBlendState blend) =>
        blend is
        {
            Enable: true,
            ColorSrcFactor: 1,
            ColorDstFactor: 5,
            ColorFunc: 0,
        };

    private static AgcIndexHelpers.ProsperoIndexType GetProsperoIndexType(SubmittedDcbState state) =>
        // IndexSize is latched from ItIndexType and from UC VGT_INDEX_TYPE
        // writes. Do not fall back to a stale UC value when IndexSize is 0 —
        // that mis-classified 16-bit draws as index8 and blanked meshes.
        AgcIndexHelpers.Decode(state.IndexSize);

    /// <summary>
    /// Resolve the Vulkan host vertex offset. GE_INDX_OFFSET is the
    /// DrawIndexed vertexOffset / DrawAuto firstVertex when the translated
    /// shader does not contain an embedded fetch prolog. Embedded prologs add
    /// their user-SGPR offset to gl_VertexID themselves, so the host offset
    /// must remain zero in that case.
    /// </summary>
    private static int GetBaseVertex(
        SubmittedDcbState state,
        Gen5ShaderState exportState)
    {
        if (Gen5ShaderTranslator.TryGetEmbeddedFetchVertexOffset(
                exportState,
                out _))
        {
            // The translated vertex shader already adds this value to
            // gl_VertexID, so passing it through CmdDrawIndexed would double it.
            return 0;
        }

        if (state.UcRegisters.TryGetValue(GeIndxOffset, out var indexOffset))
        {
            return unchecked((int)indexOffset);
        }

        return 0;
    }

    private static int GetVertexRecordBaseVertex(
        SubmittedDcbState state,
        Gen5ShaderState exportState)
    {
        if (Gen5ShaderTranslator.TryGetEmbeddedFetchVertexOffset(
                exportState,
                out var embeddedOffset))
        {
            return embeddedOffset;
        }

        if (state.UcRegisters.TryGetValue(GeIndxOffset, out var indexOffset))
        {
            return unchecked((int)indexOffset);
        }

        return 0;
    }

    /// <summary>
    /// Resolve the record offset for host vertex attributes when the guest
    /// shader applies its first-vertex value itself. The host draw stays at
    /// vertex zero so gl_VertexID remains relative, while the buffer binding
    /// starts at the guest record selected by the fetch prolog.
    /// </summary>
    private static int GetVertexBufferBaseVertex(Gen5ShaderState exportState) =>
        Gen5ShaderTranslator.TryGetEmbeddedFetchVertexOffset(
            exportState,
            out var embeddedOffset)
                ? embeddedOffset
                : 0;

    private static GuestIndexBuffer? CreateGuestIndexBuffer(
        CpuContext ctx,
        SubmittedDcbState state,
        uint indexCount)
    {
        if (state.IndexBufferAddress == 0 || indexCount == 0)
        {
            return null;
        }

        var indexType = GetProsperoIndexType(state);
        var guestBytesPerIndex = AgcIndexHelpers.GetGuestStrideBytes(indexType);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)guestBytesPerIndex);
        var guestByteCount = checked((int)(indexCount * (uint)guestBytesPerIndex));
        var address = state.IndexBufferAddress + byteOffset;
        var retained = state.CurrentIndexSnapshot;
        if (retained is not null &&
            retained.SourceAddress == address &&
            retained.IndexCount == indexCount &&
            retained.IndexStride == guestBytesPerIndex &&
            retained.Data.Length >= guestByteCount)
        {
            if (indexType == AgcIndexHelpers.ProsperoIndexType.Index8)
            {
                var expanded = new byte[checked((int)(indexCount * sizeof(ushort)))];
                AgcIndexHelpers.ExpandIndex8ToU16(
                    retained.Data.AsSpan(0, guestByteCount),
                    expanded);
                return new GuestIndexBuffer(
                    expanded,
                    expanded.Length,
                    Is32Bit: false,
                    Pooled: false);
            }

            return new GuestIndexBuffer(
                retained.Data,
                guestByteCount,
                indexType == AgcIndexHelpers.ProsperoIndexType.Index32,
                Pooled: false);
        }

        // Host backends only bind u16/u32. Expand kIndex8 -> u16.
        if (indexType == AgcIndexHelpers.ProsperoIndexType.Index8)
        {
            var guestData = GuestDataPool.Shared.Rent(guestByteCount);
            var guestSpan = guestData.AsSpan(0, guestByteCount);
            if (!ctx.Memory.TryRead(address, guestSpan) &&
                !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, guestSpan))
            {
                GuestDataPool.Shared.Return(guestData);
                return null;
            }

            var hostByteCount = checked((int)(indexCount * sizeof(ushort)));
            var hostData = GuestDataPool.Shared.Rent(hostByteCount);
            AgcIndexHelpers.ExpandIndex8ToU16(
                guestSpan,
                hostData.AsSpan(0, hostByteCount));
            GuestDataPool.Shared.Return(guestData);
            return CreatePooledGuestIndexBuffer(
                hostData,
                hostByteCount,
                is32Bit: false);
        }

        var is32Bit = indexType == AgcIndexHelpers.ProsperoIndexType.Index32;
        var data = GuestDataPool.Shared.Rent(guestByteCount);
        var span = data.AsSpan(0, guestByteCount);
        if (ctx.Memory.TryRead(address, span) ||
            KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
        {
            return CreatePooledGuestIndexBuffer(data, guestByteCount, is32Bit);
        }

        GuestDataPool.Shared.Return(data);
        return null;
    }

    private static GuestIndexBuffer CreatePooledGuestIndexBuffer(
        byte[] data,
        int length,
        bool is32Bit) =>
        new(
            data,
            length,
            is32Bit,
            Pooled: true,
            new GuestIndexBufferLease(data));

    private static bool TryGetRequiredVertexRecordCount(
        CpuContext ctx,
        SubmittedDcbState state,
        uint drawCount,
        bool indexed,
        int resolvedBaseVertex,
        out uint recordCount)
    {
        var baseVertex = (uint)Math.Max(resolvedBaseVertex, 0);
        recordCount = Math.Max(
            baseVertex + drawCount,
            Math.Max(state.InstanceCount, 1u));
        if (!indexed)
        {
            return true;
        }

        if (state.IndexBufferAddress == 0 || drawCount == 0)
        {
            return false;
        }

        var indexType = GetProsperoIndexType(state);
        var bytesPerIndex = AgcIndexHelpers.GetGuestStrideBytes(indexType);
        var byteOffset = checked((ulong)state.DrawIndexOffset * (uint)bytesPerIndex);
        var address = state.IndexBufferAddress + byteOffset;
        var retained = state.CurrentIndexSnapshot;
        var retainedByteCount = checked((int)(drawCount * (uint)bytesPerIndex));
        if (retained is not null &&
            retained.SourceAddress == address &&
            retained.IndexCount == drawCount &&
            retained.IndexStride == bytesPerIndex &&
            retained.Data.Length >= retainedByteCount)
        {
            var retainedMaxIndex = 0u;
            var retainedSawIndex = false;
            var retainedSpan = retained.Data.AsSpan(0, retainedByteCount);
            for (var index = 0; index < drawCount; index++)
            {
                var offset = checked((int)index * bytesPerIndex);
                uint value = indexType switch
                {
                    AgcIndexHelpers.ProsperoIndexType.Index32 =>
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            retainedSpan.Slice(offset, sizeof(uint))),
                    AgcIndexHelpers.ProsperoIndexType.Index8 => retainedSpan[offset],
                    _ => BinaryPrimitives.ReadUInt16LittleEndian(
                        retainedSpan.Slice(offset, sizeof(ushort))),
                };
                var restart = indexType switch
                {
                    AgcIndexHelpers.ProsperoIndexType.Index32 => uint.MaxValue,
                    AgcIndexHelpers.ProsperoIndexType.Index8 => 0xFFu,
                    _ => ushort.MaxValue,
                };
                if (value == restart)
                {
                    continue;
                }

                retainedMaxIndex = Math.Max(retainedMaxIndex, value);
                retainedSawIndex = true;
            }

            var retainedRecords = retainedSawIndex
                ? baseVertex + retainedMaxIndex + 1
                : Math.Max(baseVertex + 1, 1u);
            recordCount = Math.Max(retainedRecords, Math.Max(state.InstanceCount, 1u));
            return true;
        }

        const int chunkBytes = 64 * 1024;
        var scratch = GuestDataPool.Shared.Rent(chunkBytes);
        var remaining = drawCount;
        var maxIndex = 0u;
        var sawIndex = false;
        try
        {
            while (remaining != 0)
            {
                var chunkIndices = (int)Math.Min(
                    remaining,
                    (uint)(chunkBytes / bytesPerIndex));
                var bytes = chunkIndices * bytesPerIndex;
                var span = scratch.AsSpan(0, bytes);
                if (!ctx.Memory.TryRead(address, span) &&
                    !KernelMemoryCompatExports.TryReadTrackedLibcHeap(address, span))
                {
                    return false;
                }

                for (var index = 0; index < chunkIndices; index++)
                {
                    uint value = indexType switch
                    {
                        AgcIndexHelpers.ProsperoIndexType.Index32 =>
                            BinaryPrimitives.ReadUInt32LittleEndian(
                                span.Slice(index * sizeof(uint), sizeof(uint))),
                        AgcIndexHelpers.ProsperoIndexType.Index8 => span[index],
                        _ => BinaryPrimitives.ReadUInt16LittleEndian(
                            span.Slice(index * sizeof(ushort), sizeof(ushort))),
                    };
                    var restart = indexType switch
                    {
                        AgcIndexHelpers.ProsperoIndexType.Index32 => uint.MaxValue,
                        AgcIndexHelpers.ProsperoIndexType.Index8 => 0xFFu,
                        _ => ushort.MaxValue,
                    };
                    if (value == restart)
                    {
                        // Primitive-restart markers do not address vertex data.
                        continue;
                    }

                    maxIndex = Math.Max(maxIndex, value);
                    sawIndex = true;
                }

                address += (uint)bytes;
                remaining -= (uint)chunkIndices;
            }
        }
        finally
        {
            GuestDataPool.Shared.Return(scratch);
        }

        var indexedRecords = sawIndex && maxIndex != uint.MaxValue
            ? baseVertex + maxIndex + 1
            : Math.Max(baseVertex + 1, 1u);
        recordCount = Math.Max(indexedRecords, Math.Max(state.InstanceCount, 1u));
        if (_traceVertexRanges &&
            Interlocked.Increment(ref _tracedVertexRangeCount) <= 512)
        {
            var indexBits = indexType switch
            {
                AgcIndexHelpers.ProsperoIndexType.Index32 => 32,
                AgcIndexHelpers.ProsperoIndexType.Index8 => 8,
                _ => 16,
            };
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.vertex_range indexed=1 draw_count={drawCount} " +
                $"max_index={(sawIndex ? maxIndex : 0)} base_vertex={baseVertex} " +
                $"records={recordCount} instances={state.InstanceCount} " +
                $"index_size={indexBits} index_addr=0x{state.IndexBufferAddress:X16} " +
                $"offset={state.DrawIndexOffset}");
        }
        return true;
    }

    private static uint GetPixelColorExportMask(uint packedMasks, uint target) =>
        target < ColorTargetCount
            ? (packedMasks >> (int)(target * 4)) & 0xFu
            : 0;

    internal static ulong PackPixelOutputMappings(
        IReadOnlyList<Gen5ColorComponentMapping> mappings)
    {
        if (mappings.Count > ColorTargetCount)
        {
            throw new ArgumentOutOfRangeException(nameof(mappings));
        }

        var packed = 0UL;
        for (var index = 0; index < mappings.Count; index++)
        {
            packed |= (ulong)mappings[index].Packed << (index * 8);
        }

        return packed;
    }

    private static uint GetInterpolatedAttributeCount(Gen5ShaderState state)
    {
        var maxAttribute = -1;
        foreach (var instruction in state.Program.Instructions)
        {
            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                maxAttribute = Math.Max(maxAttribute, (int)interpolation.Attribute);
            }
        }

        return (uint)(maxAttribute + 1);
    }

    private static readonly bool _bakeScalars = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_BAKE_SGPRS"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Fingerprint of everything that shapes the translated SPIR-V besides
    /// scalar register values (those arrive in a per-draw buffer): the
    /// resolved binding set with its format-shaping descriptor words, vertex
    /// input layouts, and compute system registers. Value churn in user data
    /// no longer forces a new translation and pipeline.
    /// </summary>
    internal static ulong ComputeShaderStructuralFingerprint(Gen5ShaderEvaluation evaluation)
    {
        const ulong prime = 1099511628211UL;
        var hash = 14695981039346656037UL;
        void Mix(ulong value) => hash = (hash ^ value) * prime;

        foreach (var binding in evaluation.ImageBindings)
        {
            Mix(binding.Pc);
            Mix((ulong)(uint)binding.Opcode.GetHashCode());
            if (binding.ResourceDescriptor.Count > 1)
            {
                // The unified format selects the generated image type.
                Mix(binding.ResourceDescriptor[1] & 0x1FF0_0000u);
            }

            if (binding.ResourceDescriptor.Count > 3 &&
                binding.Opcode.StartsWith("ImageStore", StringComparison.Ordinal))
            {
                // A storage write applies this value in generated shader code.
                Mix(Gen5ShaderTranslator.GetImageDescriptorDstSelect(
                    binding.ResourceDescriptor));
            }

            Mix(binding.MipLevel ?? 0xFFFF_FFFFUL);
        }

        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            Mix(binding.ScalarAddress);
            Mix((ulong)binding.InstructionPcs.Count);
            foreach (var pc in binding.InstructionPcs)
            {
                Mix(pc);
            }
        }

        if (evaluation.VertexInputs is { } vertexInputs)
        {
            foreach (var input in vertexInputs)
            {
                Mix(input.Pc);
                Mix(input.Location);
                Mix(input.ComponentCount);
                Mix(input.DataFormat);
                Mix(input.NumberFormat);
                Mix(input.Stride);
                Mix(input.OffsetBytes);
                Mix(input.PerInstance ? 1u : 0u);
            }
        }

        if (evaluation.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            Mix(computeSystemRegisters.WorkGroupXRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.WorkGroupYRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.WorkGroupZRegister ?? uint.MaxValue);
            Mix(computeSystemRegisters.ThreadGroupSizeRegister ?? uint.MaxValue);
        }

        return hash;
    }

    private static ulong ComputeShaderStateFingerprint(Gen5ShaderEvaluation evaluation)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var value in evaluation.ScalarRegisters)
        {
            hash = (hash ^ value) * prime;
        }

        // Baked-scalar mode has no runtime state block from which the shader
        // can load descriptor-alignment biases, so the low guest address bits
        // remain part of the generated module and must participate in its key.
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            hash = (hash ^ (
                binding.BaseAddress &
                (_storageBufferOffsetAlignment - 1))) * prime;
        }

        if (evaluation.ComputeSystemRegisters is { } computeSystemRegisters)
        {
            hash = (hash ^ (computeSystemRegisters.WorkGroupXRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.WorkGroupYRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.WorkGroupZRegister ?? uint.MaxValue)) * prime;
            hash = (hash ^ (computeSystemRegisters.ThreadGroupSizeRegister ?? uint.MaxValue)) * prime;
        }

        return hash;
    }

    private enum CbColorMode : byte
    {
        Disable = 0,
        Normal = 1,
        EliminateFastClear = 2,
        Resolve = 3,
        FmaskDecompress = 5,
        DccDecompress = 6,
    }

    private static bool TryGetCbColorControlMode(
        IReadOnlyDictionary<uint, uint> registers,
        out uint mode)
    {
        mode = 0;
        if (!registers.TryGetValue(CbColorControl, out var colorControl))
        {
            return false;
        }

        mode = (colorControl >> 4) & 0x7u;
        return true;
    }

    private static bool IsCbMetadataColorMode(uint mode) =>
        mode is (uint)CbColorMode.EliminateFastClear or
            (uint)CbColorMode.FmaskDecompress or
            (uint)CbColorMode.DccDecompress;

    private static bool TryGetHardwareColorResolveTargets(
        IReadOnlyDictionary<uint, uint> registers,
        out RenderTargetDescriptor source,
        out RenderTargetDescriptor destination)
    {
        source = default;
        destination = default;
        if (!TryGetCbColorControlMode(registers, out var mode) ||
            mode != (uint)CbColorMode.Resolve)
        {
            return false;
        }

        // CB_COLOR_CONTROL.MODE=RESOLVE uses color slot 0 as the multisampled
        // source and slot 1 as the single-sample destination. CB_TARGET_MASK
        // still enables only slot 0, so treating this like a normal MRT draw
        // rewrites the source and leaves the following composite's input blank.
        var boundTargets = GetRenderTargets(registers, includeMaskedTargets: true);
        source = boundTargets.FirstOrDefault(target => target.Slot == 0);
        destination = boundTargets.FirstOrDefault(target => target.Slot == 1);
        return source.Address != 0 &&
            destination.Address != 0 &&
            source.Width == destination.Width &&
            source.Height == destination.Height &&
            source.Format == destination.Format;
    }

    private static readonly HashSet<ulong> _renderTargetAddresses = new();
    private static readonly HashSet<ulong> _sampledRenderTargets = new();
    private static readonly object _renderTargetProbeGate = new();
    private static long _renderTargetSampleTraceCount;
private static long _indirectDrawProbeCount;
    private static long _indirectDrawEmitCount;
    private static long _indirectDrawEmitRejectCount;
    private static long _indirectMultiProbeCount;

    private static void NoteRenderTargetAddress(ulong address)
    {
        if (address == 0)
        {
            return;
        }

        lock (_renderTargetProbeGate)
        {
            if (_renderTargetAddresses.Count < 512)
            {
                _renderTargetAddresses.Add(address);
            }
        }
    }

    private static void NoteSampledAddress(ulong address, uint format = 0, uint numberType = 0)
    {
        if (address == 0)
        {
            return;
        }

        bool firstTime;
        int distinctTargets;
        lock (_renderTargetProbeGate)
        {
            if (!_renderTargetAddresses.Contains(address))
            {
                return;
            }

            firstTime = _sampledRenderTargets.Add(address);
            distinctTargets = _renderTargetAddresses.Count;
        }

        var count = Interlocked.Increment(ref _renderTargetSampleTraceCount);
        if (firstTime || count % 2000 == 0)
        {
            var gpuResident = GuestGpu.Current.IsGpuGuestImageAvailable(address, format, numberType);
            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.rt_sampled#{count} addr=0x{address:X} first={firstTime} " +
                $"gpu_resident={gpuResident} fmt={format}/{numberType} known_targets={distinctTargets}");
        }
    }

    private static IReadOnlyList<RenderTargetDescriptor> GetRenderTargets(
        IReadOnlyDictionary<uint, uint> registers,
        bool includeMaskedTargets = false)
    {
        var hasTargetMask = registers.TryGetValue(CbTargetMask, out var targetMask);
        var targets = new List<RenderTargetDescriptor>(ColorTargetCount);
        for (uint slot = 0; slot < ColorTargetCount; slot++)
        {
            var baseRegister = CbColor0Base + slot * CbColorRegisterStride;
            if (!registers.TryGetValue(baseRegister, out var baseLow) ||
                !registers.TryGetValue(CbColor0BaseExt + slot, out var baseHigh) ||
                !registers.TryGetValue(CbColor0Attrib2 + slot, out var attrib2) ||
                !registers.TryGetValue(CbColor0Attrib3 + slot, out var attrib3) ||
                !registers.TryGetValue(CbColor0Info + slot * CbColorRegisterStride, out var info))
            {
                continue;
            }

            var address = ((ulong)(baseHigh & 0xFFu) << 40) | ((ulong)baseLow << 8);
            var writeMask = (targetMask >> ((int)slot * 4)) & 0xFu;
            if (address == 0 ||
                (!includeMaskedTargets && hasTargetMask && writeMask == 0))
            {
                continue;
            }

            if (targets.Exists(existing => existing.Address == address))
            {
                continue;
            }

            NoteRenderTargetAddress(address);

            targets.Add(new RenderTargetDescriptor(
                slot,
                address,
                ((attrib2 >> 14) & 0x3FFFu) + 1,
                (attrib2 & 0x3FFFu) + 1,
                (info >> 2) & 0x1Fu,
                (info >> 8) & 0x7u,
                ExtractRenderTargetComponentSwap(info),
                ExtractRenderTargetTileMode(attrib3)));
        }

        if (targets.Count > 1 &&
            targets.Select(t => t.Address).Distinct().Count() != targets.Count)
        {
            var dupCount = Interlocked.Increment(ref _duplicateTargetTraceCount);
            if (dupCount <= 12 || dupCount % 500 == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.rt_duplicate#{dupCount} has_mask={hasTargetMask} " +
                    $"mask=0x{targetMask:X8} slots=[" +
                    string.Join(",", targets.Select(t =>
                        $"{t.Slot}:0x{t.Address:X}:m{(targetMask >> ((int)t.Slot * 4)) & 0xFu}")) +
                    "]");
            }
        }

        return targets;
    }

    internal static uint ExtractRenderTargetComponentSwap(uint colorInfo) =>
        (colorInfo >> 11) & 0x3u;

    internal static uint ExtractRenderTargetTileMode(uint colorAttrib3) =>
        (colorAttrib3 >> 14) & 0x1Fu;

    private static GuestRenderTarget CreateGuestRenderTarget(
        RenderTargetDescriptor target) =>
        new(
            target.Address,
            target.Width,
            target.Height,
            target.Format,
            target.NumberType,
            MipLevels: 1,
            ComponentSwap: target.ComponentSwap,
            TileMode: target.TileMode);

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        RenderTargetDescriptor target)
    {
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        return new GuestRenderState(
            [DecodeBlendState(registers, target.Slot)],
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers),
            DecodeBlendConstant(registers));
    }

    private static GuestRenderState CreateRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        IReadOnlyList<RenderTargetDescriptor> targets,
        uint pixelColorExportMasks,
        IReadOnlyList<Gen5ColorComponentMapping> outputMappings)
    {
        if (targets.Count == 0)
        {
            return GuestRenderState.Default;
        }

        var target = targets[0];
        var scissor = DecodeScissor(registers, target.Width, target.Height);
        var blends = new GuestBlendState[targets.Count];
        for (var index = 0; index < targets.Count; index++)
        {
            var blend = DecodeBlendState(registers, targets[index].Slot);
            blends[index] = blend with
            {
                WriteMask = outputMappings[index].ApplyMask(
                    blend.WriteMask &
                        GetPixelColorExportMask(
                            pixelColorExportMasks,
                            targets[index].Slot)),
            };
        }

        return new GuestRenderState(
            blends,
            scissor,
            DecodeViewport(registers, target.Width, target.Height, scissor),
            DecodeRasterState(registers),
            DecodeDepthState(registers),
            DecodeBlendConstant(registers));
    }

    // DB_DEPTH_CONTROL (context register 0x200): Z_ENABLE bit1, Z_WRITE_ENABLE
    // bit2, ZFUNC bits[6:4] (GCN compare, matches Vulkan CompareOp ordering).
    // DB_RENDER_CONTROL (context register 0x000): DEPTH_CLEAR_ENABLE bit0.
    private const uint DbDepthControl = 0x200;

    internal static GuestDepthState DecodeDepthState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var hasDepthControl = registers.TryGetValue(DbDepthControl, out var control);
        registers.TryGetValue(DbRenderControl, out var renderControl);
        var testEnable = (control & 0x2u) != 0;
        var writeEnable = (control & 0x4u) != 0;
        var compareOp = hasDepthControl
            ? (control >> 4) & 0x7u
            : GuestDepthState.Default.CompareOp;
        var clearEnable = (renderControl & 0x1u) != 0;
        return new GuestDepthState(testEnable, writeEnable, compareOp, clearEnable);
    }

    internal static bool TryDecodeHtileMetadataBinding(
        IReadOnlyDictionary<uint, uint> registers,
        out ulong address,
        out uint baseLayer)
    {
        address = 0;
        baseLayer = 0;
        if (!registers.TryGetValue(DbZInfo, out var zInfo) ||
            (zInfo & 0x20000000u) == 0 ||
            !registers.TryGetValue(DbHtileDataBase, out var htileBase))
        {
            return false;
        }

        registers.TryGetValue(DbHtileDataBaseHi, out var htileBaseHi);
        registers.TryGetValue(DbDepthView, out var depthView);
        address =
            ((ulong)(htileBaseHi & 0xFFu) << 40) |
            ((ulong)htileBase << 8);
        baseLayer = depthView & 0x1FFFu;
        return address != 0;
    }

    private static void RegisterActiveHtile(
        SubmittedGpuState gpuState,
        IReadOnlyDictionary<uint, uint> registers)
    {
        if (TryDecodeHtileMetadataBinding(registers, out var address, out _))
        {
            gpuState.HtileMetadata.Register(address);
        }
    }

    private static void MarkHtileMetadataClear(
        SubmittedGpuState gpuState,
        ulong address,
        string source)
    {
        if (!gpuState.HtileMetadata.TryMarkAllLayersCleared(address))
        {
            return;
        }

        if (_traceDepthMetadata &&
            _tracedHtileMetadataMarks.TryAdd((address, source), 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.htile_metadata_mark " +
                $"htile=0x{address:X16} source={source}");
        }
    }

    private static TranslatedGuestDraw ApplyHtileMetadataClear(
        SubmittedGpuState gpuState,
        ulong drawSequence,
        TranslatedGuestDraw draw)
    {
        if (draw.DepthTarget is not { HtileAcceleration: true } depthTarget)
        {
            return draw;
        }

        gpuState.HtileMetadata.Register(depthTarget.HtileAddress);
        if (draw.RenderState.Depth.ClearEnable)
        {
            MarkHtileMetadataClear(
                gpuState,
                depthTarget.HtileAddress,
                "direct-depth-clear");
        }

        if (!gpuState.HtileMetadata.TryConsumeClearedLayer(
                depthTarget.HtileAddress,
                depthTarget.HtileBaseLayer))
        {
            return draw;
        }

        if (_traceDepthMetadata &&
            _tracedHtileMetadataConsumes.TryAdd(
                (depthTarget.Address,
                 depthTarget.HtileAddress,
                 depthTarget.HtileBaseLayer,
                 BitConverter.SingleToUInt32Bits(depthTarget.ClearDepth)),
                0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.htile_metadata_consume " +
                $"seq={drawSequence} depth=0x{depthTarget.Address:X16} " +
                $"htile=0x{depthTarget.HtileAddress:X16} " +
                $"layer={depthTarget.HtileBaseLayer} clear={depthTarget.ClearDepth:R}");
        }

        return draw with
        {
            DepthTarget = depthTarget with { MetadataClear = true },
        };
    }

    private static GuestDepthTarget? DecodeDepthTarget(
        IReadOnlyDictionary<uint, uint> registers)
    {
        var depthState = DecodeDepthState(registers);
        if (!depthState.TestEnable &&
            !depthState.WriteEnable &&
            !depthState.ClearEnable)
        {
            return null;
        }

        if (!registers.TryGetValue(DbZInfo, out var zInfo) ||
            !registers.TryGetValue(DbDepthSizeXy, out var sizeXy))
        {
            return null;
        }

        var guestFormat = zInfo & 0x3u;
        if (guestFormat == 0)
        {
            return null;
        }

        registers.TryGetValue(DbZReadBase, out var readBase);
        registers.TryGetValue(DbZWriteBase, out var writeBase);
        registers.TryGetValue(DbZReadBaseHi, out var readBaseHi);
        registers.TryGetValue(DbZWriteBaseHi, out var writeBaseHi);
        var readAddress = ((ulong)(readBaseHi & 0xFFu) << 40) | ((ulong)readBase << 8);
        var writeAddress = ((ulong)(writeBaseHi & 0xFFu) << 40) | ((ulong)writeBase << 8);
        if (readAddress == 0 && writeAddress == 0)
        {
            return null;
        }

        var width = (sizeXy & 0x3FFFu) + 1;
        var height = ((sizeXy >> 16) & 0x3FFFu) + 1;
        if (width == 0 || height == 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        registers.TryGetValue(DbDepthView, out var depthView);
        registers.TryGetValue(DbHtileDataBase, out var htileBase);
        registers.TryGetValue(DbHtileDataBaseHi, out var htileBaseHi);
        var htileAddress =
            ((ulong)(htileBaseHi & 0xFFu) << 40) |
            ((ulong)htileBase << 8);
        var htileAcceleration =
            htileAddress != 0 && (zInfo & 0x20000000u) != 0;
        var htileBaseLayer = depthView & 0x1FFFu;
        var clearDepth = registers.TryGetValue(DbDepthClear, out var clearBits)
            ? BitConverter.UInt32BitsToSingle(clearBits)
            : 1f;
        if (!float.IsFinite(clearDepth) || clearDepth < 0f || clearDepth > 1f)
        {
            clearDepth = 1f;
        }

        if (_traceDepthMetadata)
        {
            registers.TryGetValue(DbHtileSurface, out var htileSurface);
            registers.TryGetValue(DbDepthControl, out var depthControl);
            registers.TryGetValue(DbRenderControl, out var renderControl);
            var depthAddress = writeAddress != 0 ? writeAddress : readAddress;
            if (_tracedDepthMetadataStates.TryAdd(
                    (depthAddress,
                     htileAddress,
                     zInfo,
                     htileSurface,
                     depthControl,
                     renderControl),
                    0) &&
                Interlocked.Increment(ref _depthMetadataTraceCount) <= 128)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.depth_metadata " +
                    $"depth=0x{depthAddress:X16} htile=0x{htileAddress:X16} " +
                    $"size={width}x{height} z_info=0x{zInfo:X8} " +
                    $"htile_accel={((zInfo & 0x20000000u) != 0 ? 1 : 0)} " +
                    $"surface=0x{htileSurface:X8} control=0x{depthControl:X8} " +
                    $"render_control=0x{renderControl:X8} " +
                    $"direct_clear={(depthState.ClearEnable ? 1 : 0)} " +
                    $"clear={clearDepth:R}");
            }
        }

        return new GuestDepthTarget(
            readAddress,
            writeAddress,
            width,
            height,
            guestFormat,
            (zInfo >> 4) & 0x1Fu,
            clearDepth,
            ReadOnly: (depthView & (1u << 24)) != 0 || writeAddress == 0,
            HtileAddress: htileAddress,
            HtileBaseLayer: htileBaseLayer,
            HtileAcceleration: htileAcceleration);
    }

    // PA_SU_SC_MODE_CNTL (context register 0x205) carries face culling, the
    // front-face winding and polygon (wireframe) mode.
    private const uint PaSuScModeCntl = 0x205;
    private const uint PaSuPolyOffsetDbFmtCntl = 0x2DE;
    private const uint PaSuPolyOffsetClamp = 0x2DF;
    private const uint PaSuPolyOffsetFrontScale = 0x2E0;
    private const uint PaSuPolyOffsetFrontOffset = 0x2E1;
    private const uint PaSuPolyOffsetBackScale = 0x2E2;
    private const uint PaSuPolyOffsetBackOffset = 0x2E3;

    internal static GuestRasterState DecodeRasterState(
        IReadOnlyDictionary<uint, uint> registers)
    {
        if (!registers.TryGetValue(PaSuScModeCntl, out var mode))
        {
            return GuestRasterState.Default;
        }

        var cullFront = (mode & 0x1u) != 0;
        var cullBack = (mode & 0x2u) != 0;
        var frontFaceClockwise = (mode & 0x4u) != 0;
        var polyMode = (mode >> 3) & 0x3u;
        var frontPtype = (mode >> 5) & 0x7u;
        // POLY_MODE != 0 with a line front primitive type renders wireframe.
        var wireframe = polyMode != 0 && frontPtype == 1;
        var useFrontBias = (mode & (1u << 11)) != 0 && !cullFront;
        var useBackBias = (mode & (1u << 12)) != 0 && !cullBack;
        var depthBiasEnable = useFrontBias || useBackBias;
        var scaleRegister = useFrontBias
            ? PaSuPolyOffsetFrontScale
            : PaSuPolyOffsetBackScale;
        var offsetRegister = useFrontBias
            ? PaSuPolyOffsetFrontOffset
            : PaSuPolyOffsetBackOffset;
        registers.TryGetValue(scaleRegister, out var rawScale);
        registers.TryGetValue(offsetRegister, out var rawOffset);
        registers.TryGetValue(PaSuPolyOffsetClamp, out var rawClamp);
        var negNumDbBits = (sbyte)-23;
        var isFloatFormat = true;
        if (registers.TryGetValue(PaSuPolyOffsetDbFmtCntl, out var formatControl))
        {
            negNumDbBits = unchecked((sbyte)(formatControl & 0xFFu));
            isFloatFormat = (formatControl & (1u << 8)) != 0;
        }

        return new GuestRasterState(
            cullFront,
            cullBack,
            frontFaceClockwise,
            wireframe,
            depthBiasEnable,
            BitConverter.Int32BitsToSingle(unchecked((int)rawOffset)),
            BitConverter.Int32BitsToSingle(unchecked((int)rawClamp)),
            BitConverter.Int32BitsToSingle(unchecked((int)rawScale)) / 16f,
            negNumDbBits,
            isFloatFormat);
    }

    /// <summary>CB_BLEND_RED..ALPHA carry the constant blend color as raw
    /// float bits; unwritten registers read as the reset value (0.0).</summary>
    private static GuestBlendConstant DecodeBlendConstant(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(CbBlendRed, out var red);
        registers.TryGetValue(CbBlendGreen, out var green);
        registers.TryGetValue(CbBlendBlue, out var blue);
        registers.TryGetValue(CbBlendAlpha, out var alpha);
        return new GuestBlendConstant(
            BitConverter.Int32BitsToSingle(unchecked((int)red)),
            BitConverter.Int32BitsToSingle(unchecked((int)green)),
            BitConverter.Int32BitsToSingle(unchecked((int)blue)),
            BitConverter.Int32BitsToSingle(unchecked((int)alpha)));
    }

    private static GuestBlendState DecodeBlendState(
        IReadOnlyDictionary<uint, uint> registers,
        uint slot)
    {
        var writeMask = 0xFu;
        if (registers.TryGetValue(CbTargetMask, out var targetMask))
        {
            writeMask = (targetMask >> checked((int)(slot * 4))) & 0xFu;
        }

        registers.TryGetValue(CbBlend0Control + slot, out var control);
        return new GuestBlendState(
            ((control >> 30) & 1u) != 0,
            control & 0x1Fu,
            (control >> 8) & 0x1Fu,
            (control >> 5) & 0x7u,
            (control >> 16) & 0x1Fu,
            (control >> 24) & 0x1Fu,
            (control >> 21) & 0x7u,
            ((control >> 29) & 1u) != 0,
            writeMask);
    }

    private static GuestRect? DecodeScissor(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestRect(0, 0, 0, 0);
        }

        var left = 0;
        var top = 0;
        var right = checked((int)Math.Min(targetWidth, int.MaxValue));
        var bottom = checked((int)Math.Min(targetHeight, int.MaxValue));

        var windowOffsetX = 0;
        var windowOffsetY = 0;
        var enableWindowOffset = true;
        if (registers.TryGetValue(PaScWindowScissorTl, out var windowScissorTl))
        {
            enableWindowOffset = (windowScissorTl & 0x80000000u) == 0;
        }

        if (enableWindowOffset &&
            registers.TryGetValue(PaScWindowOffset, out var windowOffset))
        {
            windowOffsetX = (short)(windowOffset & 0xFFFFu);
            windowOffsetY = (short)(windowOffset >> 16);
        }

        // AGC reset-state blocks can carry an all-zero screen-scissor pair as
        // an unpatched placeholder while the generic/viewport scissors hold
        // the active bounds. Treat only that exact reset value as absent. A
        // nonzero empty rectangle remains meaningful and still clips the draw.
        IntersectScissorPair(
            registers,
            PaScScreenScissorTl,
            PaScScreenScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            ignoreAllZeroPair: true);
        IntersectScissorPair(
            registers,
            PaScWindowScissorTl,
            PaScWindowScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        IntersectScissorPair(
            registers,
            PaScGenericScissorTl,
            PaScGenericScissorBr,
            ref left,
            ref top,
            ref right,
            ref bottom,
            windowOffsetX,
            windowOffsetY);
        var vportScissorEnabled =
            !registers.TryGetValue(PaScModeCntl0, out var modeControl) ||
            ((modeControl >> 1) & 1u) != 0;
        if (vportScissorEnabled)
        {
            IntersectScissorPair(registers, PaScVportScissor0Tl, PaScVportScissor0Br, ref left, ref top, ref right, ref bottom);
        }

        left = Math.Clamp(left, 0, checked((int)targetWidth));
        top = Math.Clamp(top, 0, checked((int)targetHeight));
        right = Math.Clamp(right, left, checked((int)targetWidth));
        bottom = Math.Clamp(bottom, top, checked((int)targetHeight));

        if (left == 0 &&
            top == 0 &&
            right == (int)targetWidth &&
            bottom == (int)targetHeight)
        {
            return null;
        }

        return new GuestRect(
            left,
            top,
            checked((uint)(right - left)),
            checked((uint)(bottom - top)));
    }

    private static GuestViewport? DecodeViewport(
        IReadOnlyDictionary<uint, uint> registers,
        uint targetWidth,
        uint targetHeight,
        GuestRect? scissor)
    {
        if (targetWidth == 0 || targetHeight == 0)
        {
            return new GuestViewport(0, 0, 0, 0, 0, 1);
        }

        var minDepth = 0f;
        var maxDepth = 1f;
        if (registers.TryGetValue(PaScVportZMin0, out var zMinBits) &&
            registers.TryGetValue(PaScVportZMax0, out var zMaxBits))
        {
            var decodedMin = BitConverter.UInt32BitsToSingle(zMinBits);
            var decodedMax = BitConverter.UInt32BitsToSingle(zMaxBits);
            if (float.IsFinite(decodedMin) &&
                float.IsFinite(decodedMax) &&
                decodedMax > decodedMin)
            {
                minDepth = decodedMin;
                maxDepth = decodedMax;
            }
        }

        if (TryDecodeFiniteFloat(registers, PaClVportXScale, out var xScale) &&
            TryDecodeFiniteFloat(registers, PaClVportXOffset, out var xOffset) &&
            TryDecodeFiniteFloat(registers, PaClVportYScale, out var yScale) &&
            TryDecodeFiniteFloat(registers, PaClVportYOffset, out var yOffset) &&
            xScale > 0f &&
            yScale != 0f)
        {
            return new GuestViewport(
                xOffset - xScale,
                yOffset - yScale,
                xScale * 2f,
                yScale * 2f,
                minDepth,
                maxDepth);
        }

        if (scissor is not { } rect)
        {
            return minDepth == 0f && maxDepth == 1f
                ? null
                : new GuestViewport(0, 0, targetWidth, targetHeight, minDepth, maxDepth);
        }

        return new GuestViewport(
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            minDepth,
            maxDepth);
    }

    private static bool TryDecodeFiniteFloat(
        IReadOnlyDictionary<uint, uint> registers,
        uint register,
        out float value)
    {
        value = 0;
        if (!registers.TryGetValue(register, out var bits))
        {
            return false;
        }

        value = BitConverter.UInt32BitsToSingle(bits);
        return float.IsFinite(value);
    }

    private static void IntersectScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        ref int left,
        ref int top,
        ref int right,
        ref int bottom,
        int offsetX = 0,
        int offsetY = 0,
        bool ignoreAllZeroPair = false)
    {
        if (!TryDecodeScissorPair(
                registers,
                tlRegister,
                brRegister,
                out var pairLeft,
                out var pairTop,
                out var pairRight,
                out var pairBottom,
                out var allZero) ||
            (ignoreAllZeroPair && allZero))
        {
            return;
        }

        pairLeft += offsetX;
        pairTop += offsetY;
        pairRight += offsetX;
        pairBottom += offsetY;

        left = Math.Max(left, pairLeft);
        top = Math.Max(top, pairTop);
        right = Math.Min(right, pairRight);
        bottom = Math.Min(bottom, pairBottom);
    }

    private static bool TryDecodeScissorPair(
        IReadOnlyDictionary<uint, uint> registers,
        uint tlRegister,
        uint brRegister,
        out int left,
        out int top,
        out int right,
        out int bottom,
        out bool allZero)
    {
        left = 0;
        top = 0;
        right = 0;
        bottom = 0;
        allZero = false;
        if (!registers.TryGetValue(tlRegister, out var tl) ||
            !registers.TryGetValue(brRegister, out var br))
        {
            return false;
        }

        allZero = tl == 0 && br == 0;
        left = (int)(tl & 0x7FFFu);
        top = (int)((tl >> 16) & 0x7FFFu);
        right = (int)(br & 0x7FFFu);
        bottom = (int)((br >> 16) & 0x7FFFu);
        return true;
    }

    private static void TraceTranslatedGuestDraw(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        TranslatedGuestDraw draw,
        uint psInputEna,
        uint psInputAddr)
    {
        var targets = draw.RenderTargets.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.RenderTargets.Select(target =>
                    $"{target.Slot}:0x{target.Address:X16}:{target.Width}x{target.Height}:" +
                    $"fmt{target.Format}/num{target.NumberType}/tile{target.TileMode}"));
        var depthTarget = draw.DepthTarget is { } depth
            ? $"0x{depth.Address:X16}:{depth.Width}x{depth.Height}:" +
              $"fmt{depth.GuestFormat}/sw{depth.SwizzleMode}:" +
              $"read=0x{depth.ReadAddress:X16}/write=0x{depth.WriteAddress:X16}:" +
              $"clear={depth.ClearDepth:0.######}/ro={(depth.ReadOnly ? 1 : 0)}"
            : "none";
        var probes = new Dictionary<ulong, string>();
        var textures = string.Join(
            ',',
            draw.Textures.Select(binding =>
            {
                var texture = binding.Descriptor;
                var targetSlot = draw.RenderTargets
                    .FirstOrDefault(target => target.Address == texture.Address)
                    .Slot;
                var target = draw.RenderTargets.Any(candidate => candidate.Address == texture.Address)
                    ? $"/rt{targetSlot}"
                    : string.Empty;
                if (!probes.TryGetValue(texture.Address, out var probe))
                {
                    probe = ProbeTexture(ctx, texture);
                    probes.Add(texture.Address, probe);
                }

                state.RenderTargetWriters.TryGetValue(texture.Address, out var sourceWriter);
                gpuState.ComputeImageWriters.TryGetValue(texture.Address, out var computeWriter);
                var writer = sourceWriter.Sequence >= computeWriter.Sequence && sourceWriter.Sequence != 0
                    ? $"/writer={sourceWriter.Sequence}:" +
                      $"es0x{sourceWriter.ExportShaderAddress:X}:" +
                      $"ps0x{sourceWriter.PixelShaderAddress:X}:" +
                      $"v{sourceWriter.VertexCount}:prim0x{sourceWriter.PrimitiveType:X}"
                    : computeWriter.Sequence != 0
                        ? $"/compute={computeWriter.Sequence}:" +
                          $"cs0x{computeWriter.ShaderAddress:X}:{computeWriter.Opcode}"
                        : "/writer=none";
                return
                    $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                    $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                    $"/storage={binding.IsStorage}{target}/{probe}{writer}";
            }));
        var buffers = string.Join(
            ',',
            draw.GlobalMemoryBindings.Select((binding, index) =>
                $"{index}:0x{binding.BaseAddress:X16}:{binding.DataLength}:" +
                Convert.ToHexString(binding.Data.AsSpan(0, Math.Min(binding.DataLength, 256)))));
        var indices = draw.IndexBuffer is { } indexBuffer
            ? $"{(indexBuffer.Is32Bit ? 32 : 16)}:" +
              Convert.ToHexString(indexBuffer.Data.AsSpan(0, Math.Min(indexBuffer.Length, 32)))
            : "none";
        var vertexInputs = draw.VertexInputs.Count == 0
            ? "none"
            : string.Join(
                ',',
                draw.VertexInputs.Select(input =>
                    $"{input.Location}:pc=0x{input.Pc:X}:0x{input.BaseAddress:X16}" +
                    $":stride{input.Stride}:off{input.OffsetBytes}:c{input.ComponentCount}" +
                    $":fmt{input.DataFormat}/num{input.NumberFormat}"));
        var scissor = draw.RenderState.Scissor is { } drawScissor
            ? $"{drawScissor.X},{drawScissor.Y},{drawScissor.Width}x{drawScissor.Height}"
            : "full";
        var viewport = draw.RenderState.Viewport is { } drawViewport
            ? $"{drawViewport.X:0.###},{drawViewport.Y:0.###}," +
              $"{drawViewport.Width:0.###}x{drawViewport.Height:0.###}:" +
              $"{drawViewport.MinDepth:0.###}-{drawViewport.MaxDepth:0.###}"
            : "full";
        var rasterRegisters = new (string Name, uint Offset)[]
        {
            ("screen_tl", PaScScreenScissorTl),
            ("screen_br", PaScScreenScissorBr),
            ("window_off", PaScWindowOffset),
            ("window_tl", PaScWindowScissorTl),
            ("window_br", PaScWindowScissorBr),
            ("generic_tl", PaScGenericScissorTl),
            ("generic_br", PaScGenericScissorBr),
            ("vport_tl", PaScVportScissor0Tl),
            ("vport_br", PaScVportScissor0Br),
            ("mode", PaScModeCntl0),
            ("xscale", PaClVportXScale),
            ("xoffset", PaClVportXOffset),
            ("yscale", PaClVportYScale),
            ("yoffset", PaClVportYOffset),
        };
        var raster = string.Join(
            ',',
            rasterRegisters.Select(entry =>
                state.CxRegisters.TryGetValue(entry.Offset, out var value)
                    ? $"{entry.Name}=0x{value:X8}"
                    : $"{entry.Name}=missing"));
        var blend = draw.RenderState.Blend;
        var rectExpanded = AgcPrimitiveHelpers.GetRectListDrawVertexCount(
            draw.PrimitiveType,
            draw.VertexCount,
            indexed: draw.IndexBuffer is not null,
            hasVertexBuffers: draw.VertexInputs.Count > 0);
        TraceAgcShader(
            $"agc.shader_draw es=0x{draw.ExportShaderAddress:X16} " +
            $"ps=0x{draw.PixelShaderAddress:X16} spirv={draw.PixelShader.Payload.Length} " +
            $"primitive=0x{draw.PrimitiveType:X} verts={draw.VertexCount}->{rectExpanded} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc} " +
            $"write_mask=0x{blend.WriteMask:X} scissor={scissor} viewport={viewport} " +
            $"raster=[{raster}] " +
            $"ps_ena=0x{psInputEna:X8} ps_addr=0x{psInputAddr:X8} " +
            $"targets=[{targets}] depth=[{depthTarget}] textures=[{textures}] " +
            $"buffers=[{buffers}] vertex=[{vertexInputs}] indices=[{indices}]");
    }

    private static IReadOnlyList<GuestDrawTexture> CreateGuestDrawTextures(
        CpuContext ctx,
        IReadOnlyList<TranslatedImageBinding> bindings,
        out int fallbackTextureCount)
    {
        var textures = new List<GuestDrawTexture>(bindings.Count);
        Dictionary<GuestTextureSnapshotReuseKey, GuestDrawTexture>? snapshots = null;
        if (_reuseGuestTextureSnapshots)
        {
            snapshots = new Dictionary<GuestTextureSnapshotReuseKey, GuestDrawTexture>();
            if (Interlocked.Exchange(ref _guestTextureSnapshotReuseLogged, 1) == 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] Per-work guest texture snapshot reuse enabled.");
            }
        }
        fallbackTextureCount = 0;
        foreach (var binding in bindings)
        {
            var reuseKey = new GuestTextureSnapshotReuseKey(
                binding.Descriptor,
                binding.IsStorage,
                binding.MipLevel,
                binding.IsArrayed);
            GuestDrawTexture texture;
            if (snapshots?.TryGetValue(reuseKey, out var snapshot) == true)
            {
                // The sampling state is unique to each binding. Multiple bindings
                // can share the decoded texture data. Keep one record for each binding.
                // Share the unchanged snapshot only during this translation.
                texture = snapshot with
                {
                    Sampler = ToGuestSampler(binding.SamplerDescriptor),
                };
            }
            else if (TryCreateGuestDrawTexture(
                    ctx,
                    binding.Descriptor,
                    binding.IsStorage,
                    binding.MipLevel,
                    binding.SamplerDescriptor,
                    binding.IsArrayed,
                    out texture))
            {
                // An empty non-fallback snapshot shows that this sampler-specific
                // texture is in the presenter cache. Another sampler can require
                // a different cache entry. Reuse only snapshots that contain
                // decoded pixels.
                if (!texture.IsFallback && texture.RgbaPixels.Length != 0)
                {
                    snapshots?.Add(reuseKey, texture);
                }
            }
            else
            {
                continue;
            }

            textures.Add(texture);
            if (texture.IsFallback)
            {
                fallbackTextureCount++;
            }
        }

        return textures;
    }

    /// <summary>
    /// Guest storage buffers for a translated draw, followed by the per-draw
    /// initial scalar registers of each stage (pixel then vertex), matching
    /// the binding layout the shaders were compiled against.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffers(
        TranslatedGuestDraw translatedDraw)
    {
        var buffers = CreateGuestMemoryBuffers(translatedDraw.GlobalMemoryBindings);
        if (_bakeScalars)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(buffers.Count + 2);
        combined.AddRange(buffers);
        var runtimeStateLength = GetRuntimeScalarBufferLength(
            translatedDraw.GlobalMemoryBindings.Count);
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                translatedDraw.PixelInitialScalars,
                translatedDraw.GlobalMemoryBindings),
            runtimeStateLength,
            Pooled: true));
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                translatedDraw.VertexInitialScalars,
                translatedDraw.GlobalMemoryBindings),
            runtimeStateLength,
            Pooled: true));
        return combined;
    }

    private static IReadOnlyList<GuestMemoryBuffer>
        CreateGlobalBufferOwnershipView(
            IReadOnlyList<GuestMemoryBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestMemoryBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    /// <summary>
    /// Present-time variant: the flip path can reuse the same translated
    /// draw across several flips and swapchain retries, so it must not wrap
    /// the (pooled, single-consumption) binding arrays. Buffer contents are
    /// re-read from guest memory instead, which also presents current data.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedDrawGlobalBuffersForPresent(
        CpuContext ctx,
        TranslatedGuestDraw translatedDraw)
    {
        var bindings = translatedDraw.GlobalMemoryBindings;
        var combined = new List<GuestMemoryBuffer>(bindings.Count + 2);
        foreach (var binding in bindings)
        {
            var data = new byte[Math.Max(binding.DataLength, sizeof(uint))];
            var guestMemoryBacked = binding.BaseAddress != 0 &&
                (ctx.Memory.TryRead(binding.BaseAddress, data) ||
                 KernelMemoryCompatExports.TryReadTrackedLibcHeap(binding.BaseAddress, data));
            if (!guestMemoryBacked)
            {
                // Keep the zero-filled buffer; layout must match the shader.
            }

            combined.Add(new GuestMemoryBuffer(
                binding.BaseAddress,
                data,
                data.Length,
                Pooled: false,
                Writable: binding.Writable,
                WriteBackToGuest: binding.WriteBackToGuest && guestMemoryBacked));
        }

        if (!_bakeScalars)
        {
            var runtimeStateLength = GetRuntimeScalarBufferLength(bindings.Count);
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.PixelInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
            combined.Add(new GuestMemoryBuffer(
                0,
                PackRuntimeScalarStateUnpooled(
                    translatedDraw.VertexInitialScalars,
                    bindings),
                runtimeStateLength,
                Pooled: false));
        }

        return combined;
    }

    private static int GetRuntimeScalarBufferLength(int bindingCount) =>
        checked((256 + bindingCount) * sizeof(uint));

    private static byte[] PackRuntimeScalarState(
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var bytes = GuestDataPool.Shared.Rent(
            GetRuntimeScalarBufferLength(bindings.Count));
        PackRuntimeScalarStateInto(bytes, registers, bindings);
        return bytes;
    }

    private static byte[] PackRuntimeScalarStateUnpooled(
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var bytes = new byte[GetRuntimeScalarBufferLength(bindings.Count)];
        PackRuntimeScalarStateInto(bytes, registers, bindings);
        return bytes;
    }

    private static void PackRuntimeScalarStateInto(
        byte[] bytes,
        IReadOnlyList<uint> registers,
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        PackScalarRegistersInto(bytes, registers);
        var biasOffset = 256 * sizeof(uint);
        for (var index = 0; index < bindings.Count; index++)
        {
            var byteBias = checked((uint)(
                bindings[index].BaseAddress &
                (_storageBufferOffsetAlignment - 1)));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(biasOffset + index * sizeof(uint), sizeof(uint)),
                byteBias);
        }
    }

    private static void PackScalarRegistersInto(byte[] bytes, IReadOnlyList<uint> registers)
    {
        if (registers is uint[] { Length: >= 256 } array)
        {
            // Guest scalar registers are little-endian dwords and the host
            // is x86-64, so a bulk copy replaces 256 per-element writes.
            System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(array.AsSpan(0, 256))
                .CopyTo(bytes);
            return;
        }

        // Rented arrays carry stale bytes; clear the packed window first.
        Array.Clear(bytes, 0, 256 * sizeof(uint));
        var count = Math.Min(registers.Count, 256);
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                registers[index]);
        }
    }

    /// <summary>
    /// Returns the pooled buffer arrays an evaluation produced. Called only
    /// on translation-failure paths, where no <see cref="TranslatedGuestDraw"/>
    /// is built to take ownership; on success the draw's consumers return them.
    /// </summary>
    private static void ReturnPooledEvaluationArrays(Gen5ShaderEvaluation evaluation)
    {
        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var binding in evaluation.GlobalMemoryBindings)
        {
            if (binding.DataPooled && returned.Add(binding.Data))
            {
                GuestDataPool.Shared.Return(binding.Data);
            }
        }

        if (evaluation.VertexInputs is { } vertexInputs)
        {
            foreach (var binding in vertexInputs)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }
    }

    /// <summary>
    /// Returns pooled data arrays a translated draw owns but did not hand to
    /// a presenter consumer. The offscreen path hands globals, vertex and
    /// index buffers to the presenter (which returns them), so it passes all
    /// three false; other draw sinks pass true for whatever they dropped.
    /// </summary>
    private static void ReturnPooledDrawArrays(
        TranslatedGuestDraw draw,
        bool globals,
        bool vertex,
        bool index)
    {
        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        if (globals)
        {
            foreach (var binding in draw.GlobalMemoryBindings)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }

        if (vertex)
        {
            foreach (var binding in draw.VertexInputs)
            {
                if (binding.DataPooled && returned.Add(binding.Data))
                {
                    GuestDataPool.Shared.Return(binding.Data);
                }
            }
        }

        if (index && draw.IndexBuffer is { Pooled: true } indexBuffer &&
            returned.Add(indexBuffer.Data))
        {
            indexBuffer.TryReturnPooledData();
        }
    }

    private static IReadOnlyList<GuestMemoryBuffer> CreateGuestMemoryBuffers(
        IReadOnlyList<Gen5GlobalMemoryBinding> bindings)
    {
        var buffers = new GuestMemoryBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            buffers[index] = new GuestMemoryBuffer(
                bindings[index].BaseAddress,
                bindings[index].Data,
                bindings[index].DataLength,
                bindings[index].DataPooled,
                bindings[index].Writable,
                bindings[index].WriteBackToGuest);
        }

        return buffers;
    }

    /// <summary>
    /// Guest storage buffers for a compute dispatch followed by its initial
    /// scalar registers. Dispatch-specific SGPR values remain runtime data so
    /// one translated pipeline serves every matching shader/resource shape.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedComputeGlobalBuffers(
        Gen5ShaderEvaluation evaluation)
    {
        var buffers = CreateGuestMemoryBuffers(evaluation.GlobalMemoryBindings);
        if (_bakeScalars)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(buffers.Count + 1);
        combined.AddRange(buffers);
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                evaluation.InitialScalarRegisters,
                evaluation.GlobalMemoryBindings),
            GetRuntimeScalarBufferLength(evaluation.GlobalMemoryBindings.Count),
            Pooled: true));
        return combined;
    }

    private static IReadOnlyList<GuestVertexBuffer> CreateGuestVertexBuffers(
        IReadOnlyList<Gen5VertexInputBinding> bindings,
        int baseVertex)
    {
        var baseRecord = baseVertex > 0 ? checked((uint)baseVertex) : 0;
        var buffers = new GuestVertexBuffer[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            buffers[index] = new GuestVertexBuffer(
                binding.Location,
                binding.ComponentCount,
                binding.DataFormat,
                binding.NumberFormat,
                binding.BaseAddress,
                binding.Stride,
                binding.OffsetBytes,
                binding.Data,
                binding.DataLength,
                binding.DataPooled,
                binding.PerInstance,
                baseRecord);
        }

        return buffers;
    }

    private static IReadOnlyList<GuestVertexBuffer>
        CreateVertexBufferOwnershipView(
            IReadOnlyList<GuestVertexBuffer> buffers,
            bool ownsPooledData)
    {
        var view = new GuestVertexBuffer[buffers.Count];
        for (var index = 0; index < buffers.Count; index++)
        {
            var buffer = buffers[index];
            view[index] = buffer with
            {
                Pooled = ownsPooledData && buffer.Pooled,
            };
        }

        return view;
    }

    private static GuestIndexBuffer? CreateIndexBufferOwnershipView(
        GuestIndexBuffer? buffer,
        bool ownsPooledData) =>
        buffer is null
            ? null
            : buffer with { Pooled = ownsPooledData && buffer.Pooled };

    // BCn block-compressed guest formats and the bytes per 4x4 block.
    private static int GetBlockCompressedBlockBytes(uint format) => format switch
    {
        169 or 170 or 175 or 176 => 8,
        171 or 172 or 173 or 174 or 177 or 178 or 179 or 180 or 181 or 182 => 16,
        _ => 0,
    };

    /// <summary>
    /// Deswizzles a tiled texture source into linear layout when tiling is
    /// enabled and the format is understood; returns null to keep the raw
    /// bytes (linear surfaces, unknown modes, or non-power-of-two elements).
    /// </summary>
    // The GPU detile kernel implements these two equation families at 4/8/16 bpp
    // (one/two/four 32-bit words per element; 1/2 bpp are sub-word and stay on the
    // CPU). Keep in lockstep with VulkanDetilePass.Supports / MetalDetilePass.Supports.
    private static bool IsGpuDetileEquation(DetileEquation equation) =>
        equation == DetileEquation.ExactXor || equation == DetileEquation.BlockTable;

    private static bool IsGpuDetileBytesPerElement(int bytesPerElement) =>
        bytesPerElement is 4 or 8 or 16;

    private static bool IsGpuDetileTextureType(uint type) =>
        type != Gen5TextureType3D;

    private static bool TryGetTextureElementLayout(
        TextureDescriptor descriptor,
        uint sourceWidth,
        out int elementsWide,
        out int elementsHigh,
        out int bytesPerElement)
    {
        var blockBytes = GetBlockCompressedBlockBytes(descriptor.Format);
        if (blockBytes != 0)
        {
            bytesPerElement = blockBytes;
            elementsWide = (int)((sourceWidth + 3) / 4);
            elementsHigh = (int)((descriptor.Height + 3) / 4);
        }
        else
        {
            bytesPerElement = (int)GetTextureBytesPerTexel(descriptor.Format);
            if (bytesPerElement == 0)
            {
                elementsWide = 0;
                elementsHigh = 0;
                return false;
            }

            elementsWide = (int)sourceWidth;
            elementsHigh = (int)descriptor.Height;
        }

        return true;
    }

    private static byte[]? TryDetileTextureSource(
        TextureDescriptor descriptor,
        uint sourceWidth,
        int logicalByteCount,
        byte[] source,
        bool baseMipInTail = false,
        int tailElementX = 0,
        int tailElementY = 0)
    {
        if (!GnmTiling.NeedsDetile(descriptor.TileMode) ||
            !TryGetTextureElementLayout(
                descriptor,
                sourceWidth,
                out var elementsWide,
                out var elementsHigh,
                out var bytesPerElement))
        {
            return null;
        }

        if (baseMipInTail)
        {
            if (!GnmTiling.TryGetBlockElementDimensions(
                    descriptor.TileMode,
                    bytesPerElement,
                    out var blockWidth,
                    out var blockHeight))
            {
                return null;
            }

            var blockByteCount = (long)blockWidth * blockHeight * bytesPerElement;
            if (source.Length < blockByteCount ||
                (long)elementsWide * elementsHigh * bytesPerElement > logicalByteCount)
            {
                return null;
            }

            var blockLinear = new byte[blockByteCount];
            if (!GnmTiling.TryDetile(
                    source,
                    blockLinear,
                    descriptor.TileMode,
                    blockWidth,
                    blockHeight,
                    bytesPerElement))
            {
                return null;
            }

            var tailLinear = new byte[logicalByteCount];
            var rowBytes = elementsWide * bytesPerElement;
            for (var y = 0; y < elementsHigh; y++)
            {
                var sourceOffset = (((long)tailElementY + y) * blockWidth + tailElementX) * bytesPerElement;
                blockLinear.AsSpan((int)sourceOffset, rowBytes)
                    .CopyTo(tailLinear.AsSpan(y * rowBytes, rowBytes));
            }

            return tailLinear;
        }

        var volumeDepth = checked((int)GetTextureVolumeDepth(
            descriptor.Type,
            descriptor.Depth));
        if (logicalByteCount % volumeDepth != 0 ||
            source.Length % volumeDepth != 0)
        {
            return null;
        }

        var logicalSliceByteCount = logicalByteCount / volumeDepth;
        var physicalSliceByteCount = source.Length / volumeDepth;
        var linear = new byte[logicalByteCount];
        for (var slice = 0; slice < volumeDepth; slice++)
        {
            if (!GnmTiling.TryDetile(
                    source.AsSpan(slice * physicalSliceByteCount, physicalSliceByteCount),
                    linear.AsSpan(slice * logicalSliceByteCount, logicalSliceByteCount),
                    descriptor.TileMode,
                    elementsWide,
                    elementsHigh,
                    bytesPerElement))
            {
                return null;
            }
        }

        return linear;
    }

    private static void TraceTextureFallback(TextureDescriptor descriptor, string reason)
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        if ((!string.Equals(mode, "1", StringComparison.Ordinal) &&
             !string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase)) ||
            Interlocked.Increment(ref _textureFallbackTraceCount) > 64)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.texture_fallback reason={reason} " +
            $"addr=0x{descriptor.Address:X16} type={descriptor.Type} " +
            $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
            $"fmt={descriptor.Format} num={descriptor.NumberType} " +
            $"tile={descriptor.TileMode} mip={descriptor.MipLevels} " +
            $"dst=0x{descriptor.DstSelect:X3}");
    }

    private static bool TryCreateGuestDrawTexture(
        CpuContext ctx,
        TextureDescriptor descriptor,
        bool isStorage,
        uint mipLevel,
        IReadOnlyList<uint> samplerDescriptor,
        bool isArrayed,
        out GuestDrawTexture texture)
    {
        texture = default!;
        var textureDepth = GetTextureVolumeDepth(
            descriptor.Type,
            descriptor.Depth);
        if ((descriptor.Type != Gen5TextureType1D &&
             descriptor.Type != Gen5TextureType2D &&
             descriptor.Type != Gen5TextureType3D &&
             descriptor.Type != Gen5TextureTypeCube &&
             descriptor.Type != Gen5TextureType1DArray &&
             descriptor.Type != Gen5TextureType2DArray) ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.Width > 8192 ||
            descriptor.Height > 8192)
        {
            TraceTextureFallback(descriptor, "invalid-descriptor");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        if (_gpuDetileLog)
        {
            lock (_seenTextureTileModes)
            {
                if (_seenTextureTileModes.Add(descriptor.TileMode))
                {
                    Console.Error.WriteLine(
                        $"[GPU-DETILE] texture tile_mode={descriptor.TileMode} fmt={descriptor.Format} " +
                        $"{descriptor.Width}x{descriptor.Height} " +
                        $"(0=linear; GPU covers exact-XOR 5/9/24/27 @ 4bpp).");
                }
            }
        }

        var sourceWidth = descriptor.TileMode == 0
            ? GetLinearTexturePitch(
                Math.Max(descriptor.Width, descriptor.Pitch),
                descriptor.Height,
                descriptor.Format)
            : descriptor.Width;
        var sourceSliceByteCount = GetTextureByteCount(
            descriptor.Format,
            sourceWidth,
            descriptor.Height);
        var sourceByteCount = GetTextureByteCount(
            descriptor.Format,
            sourceWidth,
            descriptor.Height,
            textureDepth);
        if (sourceByteCount == 0 ||
            sourceByteCount > MaxPresentedTextureBytes ||
            sourceByteCount > int.MaxValue)
        {
            TraceTextureFallback(
                descriptor,
                $"invalid-byte-count:{sourceByteCount}");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        var physicalSourceByteCount = sourceSliceByteCount;
        var elementsWide = 0;
        var elementsHigh = 0;
        var bytesPerElement = 0;
        var hasElementLayout = GnmTiling.NeedsDetile(descriptor.TileMode) &&
            TryGetTextureElementLayout(
                descriptor,
                sourceWidth,
                out elementsWide,
                out elementsHigh,
                out bytesPerElement);
        if (hasElementLayout &&
            GnmTiling.TryGetTiledByteCount(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                out var tiledByteCount))
        {
            physicalSourceByteCount = tiledByteCount;
        }

        var resourceMipLevels = descriptor.HasExtendedDescriptor
            ? descriptor.ResourceMipLevels
            : 1u;
        var baseMipByteOffset = 0UL;
        var baseMipInTail = false;
        var mipTailElementX = 0;
        var mipTailElementY = 0;
        var chainSliceBytes = physicalSourceByteCount;
        if (hasElementLayout && resourceMipLevels > 1 &&
            GnmTiling.TryGetBaseMipPlacement(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                resourceMipLevels,
                out baseMipByteOffset,
                out baseMipInTail,
                out mipTailElementX,
                out mipTailElementY,
                out var placedChainSliceBytes))
        {
            chainSliceBytes = placedChainSliceBytes;
        }

        physicalSourceByteCount = checked(physicalSourceByteCount * textureDepth);
        if (physicalSourceByteCount > MaxPresentedTextureBytes ||
            physicalSourceByteCount > int.MaxValue)
        {
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        var wantsArrayUpload = isArrayed &&
            !isStorage &&
            descriptor.Address != 0 &&
            (descriptor.Type == Gen5TextureType2DArray ||
             descriptor.Type == Gen5TextureType1DArray) &&
            descriptor.Depth > 1 &&
            !_arrayUploadUnsupported.ContainsKey(descriptor.Address);
        var arrayUploadLayers = wantsArrayUpload ? descriptor.Depth : 1u;

        // Upload-known (not plain availability): the presenter's answer goes
        // generation-stale when the guest CPU rewrites a CPU-backed image
        // (video planes, streamed font atlases), which routes this draw back
        // through the texel copy below so the refresh path re-uploads.
        // With the write tracker off (Windows default), IsGuestImageUploadKnown
        // uses a cheap guest-memory probe so static UI can still skip (Dead
        // Cells menus) while changing CPU content (GTA Bink) forces a copy.
        if (!isStorage &&
            !wantsArrayUpload &&
            descriptor.Address != 0 &&
            GuestGpu.Current.IsGuestImageUploadKnown(
                descriptor.Address,
                descriptor.Format,
                descriptor.NumberType))
        {
            NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                [],
                IsFallback: false,
                IsStorage: false,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor),
                ArrayedView: isArrayed,
                Type: descriptor.Type,
                Depth: textureDepth);
            return true;
        }

        if (isStorage)
        {
            var initialPixels = Array.Empty<byte>();
            var uploadKnown = descriptor.Address != 0 &&
                GuestGpu.Current.IsGuestImageUploadKnown(
                    descriptor.Address,
                    descriptor.Format,
                    descriptor.NumberType);
            var readSucceeded = false;
            var linearNonzero = false;
            if (descriptor.Address != 0 && !uploadKnown)
            {
                // Storage images can be pre-populated in tiled guest memory
                // just like sampled images. Reading only the logical linear
                // byte count both truncates 64 KiB swizzle blocks and uploads
                // tiled bytes as scanlines. Read the full physical footprint
                // and run the same AddrLib-derived detile path used below for
                // sampled textures before seeding the Vulkan image.
                var storageSource = new byte[(int)physicalSourceByteCount];
                if (ctx.Memory.TryRead(descriptor.Address + baseMipByteOffset, storageSource))
                {
                    readSucceeded = true;
                    var linearStorage = TryDetileTextureSource(
                        descriptor,
                        sourceWidth,
                        checked((int)sourceByteCount),
                        storageSource,
                        baseMipInTail,
                        mipTailElementX,
                        mipTailElementY) ?? storageSource
                            .AsSpan(0, checked((int)sourceByteCount))
                            .ToArray();
                    if (linearStorage.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                    {
                        linearNonzero = true;
                        initialPixels = linearStorage;
                    }
                }
            }

            if (ParseOptionalHexAddress(
                    Environment.GetEnvironmentVariable(
                        "SHARPEMU_TRACE_STORAGE_IMAGE_INIT_ADDRESS")) ==
                descriptor.Address)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.storage_initial_data " +
                    $"addr=0x{descriptor.Address:X16} op_storage={isStorage} " +
                    $"upload_known={uploadKnown} read={readSucceeded} " +
                    $"nonzero={linearNonzero} initial_bytes={initialPixels.Length} " +
                    $"logical_bytes={sourceByteCount} physical_bytes={physicalSourceByteCount} " +
                    $"size={descriptor.Width}x{descriptor.Height} pitch={sourceWidth} " +
                    $"fmt={descriptor.Format} num={descriptor.NumberType} " +
                    $"tile={descriptor.TileMode} mip={mipLevel}");
            }

            NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                initialPixels,
                IsFallback: descriptor.Address == 0,
                IsStorage: true,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: ToGuestSampler(samplerDescriptor),
                Type: descriptor.Type,
                Depth: textureDepth);
            return true;
        }

        // When the presenter already holds this exact texture identity in
        // its cache, the texel copy below would be discarded on arrival; for
        // scenes that sample large textures every draw this copy dominated
        // CPU time (Dead Cells menus). The dirty peek closes the race with
        // eviction when the write tracker is on. With the tracker off,
        // PeekDirty is always false so a cached identity keeps skipping —
        // correct for static UI atlases. CPU-updated guest Bink planes are
        // handled by the upload-known gate above (forced copies when the
        // tracker cannot invalidate), not by disabling this cache skip.
        var sampler = ToGuestSampler(samplerDescriptor);
        // Track the guest allocation before reading its texels so a CPU
        // rewrite landing after the copy still bumps the write generation.
        // The generation rides on the texture and is recorded by the
        // presenter after upload, where the upload-known skip compares it
        // against the tracker to force fresh texels for rewritten memory.
        var hasWriteGeneration =
            SharpEmu.HLE.GuestImageWriteTracker.TryGetWriteGeneration(
                descriptor.Address,
                out var writeGeneration);
        if (!_textureCopySkipDisabled &&
            descriptor.Address != 0 &&
            !SharpEmu.HLE.GuestImageWriteTracker.PeekDirty(descriptor.Address) &&
            GuestGpu.Current.IsTextureContentCached(
                new TextureContentIdentity(
                    descriptor.Address,
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.Format,
                    descriptor.NumberType,
                    descriptor.DstSelect,
                    descriptor.TileMode,
                    sourceWidth,
                    sampler,
                    isArrayed,
                    arrayUploadLayers,
                    descriptor.Type,
                    textureDepth)))
        {
            NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
            texture = new GuestDrawTexture(
                descriptor.Address,
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.NumberType,
                [],
                IsFallback: false,
                IsStorage: false,
                MipLevels: descriptor.MipLevels,
                MipLevel: mipLevel,
                BaseMipLevel: descriptor.ViewBaseLevel,
                ResourceMipLevels: descriptor.ResourceMipLevels,
                Pitch: sourceWidth,
                TileMode: descriptor.TileMode,
                DstSelect: descriptor.DstSelect,
                Sampler: sampler,
                ArrayedView: isArrayed,
                ArrayLayers: arrayUploadLayers,
                Type: descriptor.Type,
                Depth: textureDepth);
            return true;
        }

        if (wantsArrayUpload)
        {
            var arrayLayers = arrayUploadLayers;
            var layerBytes = checked((int)sourceSliceByteCount);
            var totalBytes = (long)layerBytes * arrayLayers;

            // GPU detile for arrayed exact-XOR/4bpp textures: pack the tiled array
            // slices contiguously and hand them to the GPU pass (one dispatch-Z
            // layer per slice), mirroring the single-layer gate above. The backend
            // deswizzles every layer on the GPU; only unsupported cases fall to the
            // CPU per-layer detile below. Font/text atlases uploaded as 2D arrays
            // take this path.
            if (_gpuDetileEnabled && hasElementLayout && !baseMipInTail &&
                IsGpuDetileBytesPerElement(bytesPerElement) &&
                IsGpuDetileTextureType(descriptor.Type) &&
                (long)physicalSourceByteCount * arrayLayers <= int.MaxValue)
            {
                var gpuArrayParams = GnmTiling.GetDetileParams(
                    descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh);
                if (IsGpuDetileEquation(gpuArrayParams.Equation) &&
                    (long)elementsWide * elementsHigh * bytesPerElement <= (long)physicalSourceByteCount)
                {
                    var sliceBytes = checked((int)physicalSourceByteCount);
                    var tiledLayers = new byte[(long)sliceBytes * arrayLayers];
                    var readAllLayers = true;
                    for (var layer = 0u; layer < arrayLayers; layer++)
                    {
                        if (!ctx.Memory.TryRead(
                                descriptor.Address + layer * chainSliceBytes + baseMipByteOffset,
                                tiledLayers.AsSpan(checked((int)(layer * (uint)sliceBytes)), sliceBytes)))
                        {
                            readAllLayers = false;
                            break;
                        }
                    }

                    if (readAllLayers)
                    {
                        NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
            texture = new GuestDrawTexture(
                            descriptor.Address,
                            descriptor.Width,
                            descriptor.Height,
                            descriptor.Format,
                            descriptor.NumberType,
                            [],
                            IsFallback: false,
                            IsStorage: false,
                            MipLevels: descriptor.MipLevels,
                            MipLevel: mipLevel,
                            BaseMipLevel: descriptor.ViewBaseLevel,
                            ResourceMipLevels: descriptor.ResourceMipLevels,
                            Pitch: sourceWidth,
                            TileMode: descriptor.TileMode,
                            DstSelect: descriptor.DstSelect,
                            Sampler: sampler,
                            WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                            ArrayedView: true,
                            ArrayLayers: arrayLayers,
                            // Must match the identity the CPU path below ships, or
                            // the presenter caches this texture under a different
                            // key than IsTextureContentCached queries above and the
                            // texel-copy skip never hits for non-2D descriptors.
                            Type: descriptor.Type,
                            Depth: textureDepth,
                            TiledSource: tiledLayers,
                            Detile: gpuArrayParams);
                        return true;
                    }
                }
            }

            if (totalBytes <= int.MaxValue)
            {
                var layered = new byte[totalBytes];
                var uploadedLayers = 0u;
                for (var layer = 0u; layer < arrayLayers; layer++)
                {
                    var sliceSource = new byte[(int)chainSliceBytes];
                    if (!ctx.Memory.TryRead(
                            descriptor.Address + layer * chainSliceBytes + baseMipByteOffset,
                            sliceSource))
                    {
                        break;
                    }

                    var sliceLinear = TryDetileTextureSource(
                        descriptor,
                        sourceWidth,
                        layerBytes,
                        sliceSource,
                        baseMipInTail,
                        mipTailElementX,
                        mipTailElementY) ?? sliceSource.AsSpan(0, layerBytes).ToArray();
                    sliceLinear.AsSpan(0, layerBytes)
                        .CopyTo(layered.AsSpan(checked((int)(layer * layerBytes))));
                    uploadedLayers++;
                }

                if (uploadedLayers == arrayLayers)
                {
                    NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
            texture = new GuestDrawTexture(
                        descriptor.Address,
                        descriptor.Width,
                        descriptor.Height,
                        descriptor.Format,
                        descriptor.NumberType,
                        layered,
                        IsFallback: false,
                        IsStorage: false,
                        MipLevels: descriptor.MipLevels,
                        MipLevel: mipLevel,
                        BaseMipLevel: descriptor.ViewBaseLevel,
                        ResourceMipLevels: descriptor.ResourceMipLevels,
                        Pitch: sourceWidth,
                        TileMode: descriptor.TileMode,
                        DstSelect: descriptor.DstSelect,
                        Sampler: sampler,
                        ArrayedView: true,
                        ArrayLayers: arrayLayers,
                        Type: descriptor.Type,
                        Depth: textureDepth);
                    return true;
                }
            }

            _arrayUploadUnsupported.TryAdd(descriptor.Address, 0);
        }

        var source = new byte[(int)physicalSourceByteCount];
        if (!ctx.Memory.TryRead(descriptor.Address + baseMipByteOffset, source))
        {
            TraceTextureFallback(
                descriptor,
                $"guest-read-failed:{sourceByteCount}");
            texture = CreateFallbackGuestDrawTexture(
                isStorage,
                descriptor.Format,
                descriptor.NumberType,
                isArrayed,
                descriptor.Type,
                textureDepth);
            return true;
        }

        if (_traceAgcShader)
        {
            var nonZero = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (source[i] != 0)
                {
                    nonZero++;
                    if (nonZero >= 64)
                    {
                        break;
                    }
                }
            }

            TraceAgcShader(
                $"agc.texture_source addr=0x{descriptor.Address:X16} " +
                $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
                $"size={descriptor.Width}x{descriptor.Height} pitch={descriptor.Pitch} " +
                $"dst=0x{descriptor.DstSelect:X3} " +
                $"bytes={source.Length} logical_bytes={sourceByteCount} nonzero64={nonZero}");
        }
        DumpTextureSourceIfRequested(descriptor, sourceWidth, source);

        if (_gpuDetileLog && descriptor.TileMode != 0)
        {
            lock (_gpuDetileGateDiag)
            {
                if (_gpuDetileGateDiag.Add(descriptor.TileMode))
                {
                    var eq = hasElementLayout
                        ? GnmTiling.GetDetileParams(
                            descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh).Equation
                        : DetileEquation.None;
                    Console.Error.WriteLine(
                        $"[GPU-DETILE] gate mode={descriptor.TileMode} fmt={descriptor.Format} " +
                        $"bpp={bytesPerElement} hasLayout={hasElementLayout} mipTail={baseMipInTail} " +
                        $"storage={isStorage} arrayed={isArrayed} eq={eq} -> " +
                        $"{(hasElementLayout && !baseMipInTail && IsGpuDetileBytesPerElement(bytesPerElement) && IsGpuDetileEquation(eq) ? "GPU" : "CPU")}");
                }
            }
        }

        // GPU detile: for the 4/8/16-bytes/element base-mip case the backend can
        // deswizzle on the GPU (exact-XOR and block-table equations, including
        // block-compressed formats), so ship the raw tiled bytes + params rather
        // than paying the CPU detile. Everything else keeps the CPU path below.
        //
        // Arrayed textures are handled by the arrayed branch above (they package
        // every layer's tiled slice); this branch is the single-layer case.
        if (_gpuDetileEnabled && hasElementLayout && !baseMipInTail &&
            IsGpuDetileBytesPerElement(bytesPerElement) && !isArrayed &&
            IsGpuDetileTextureType(descriptor.Type))
        {
            var gpuDetileParams = GnmTiling.GetDetileParams(
                descriptor.TileMode, bytesPerElement, elementsWide, elementsHigh);
            if (IsGpuDetileEquation(gpuDetileParams.Equation) &&
                (long)elementsWide * elementsHigh * bytesPerElement <= source.Length)
            {
                NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
            texture = new GuestDrawTexture(
                    descriptor.Address,
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.Format,
                    descriptor.NumberType,
                    [],
                    IsFallback: false,
                    IsStorage: isStorage,
                    MipLevels: descriptor.MipLevels,
                    MipLevel: mipLevel,
                    BaseMipLevel: descriptor.ViewBaseLevel,
                    ResourceMipLevels: descriptor.ResourceMipLevels,
                    Pitch: sourceWidth,
                    TileMode: descriptor.TileMode,
                    DstSelect: descriptor.DstSelect,
                    Sampler: ToGuestSampler(samplerDescriptor),
                    WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                    ArrayedView: isArrayed,
                    Type: descriptor.Type,
                    Depth: textureDepth,
                    TiledSource: source,
                    Detile: gpuDetileParams);
                return true;
            }
        }

        var rgba = TryDetileTextureSource(
            descriptor,
            sourceWidth,
            checked((int)sourceByteCount),
            source,
            baseMipInTail,
            mipTailElementX,
            mipTailElementY) ?? source.AsSpan(0, checked((int)sourceByteCount)).ToArray();
        DumpLinearTextureIfRequested(descriptor, sourceWidth, rgba);
        texture = new GuestDrawTexture(
            descriptor.Address,
            descriptor.Width,
            descriptor.Height,
            descriptor.Format,
            descriptor.NumberType,
            rgba,
            IsFallback: false,
            IsStorage: isStorage,
            MipLevels: descriptor.MipLevels,
            MipLevel: mipLevel,
            BaseMipLevel: descriptor.ViewBaseLevel,
            ResourceMipLevels: descriptor.ResourceMipLevels,
            Pitch: sourceWidth,
            TileMode: descriptor.TileMode,
            DstSelect: descriptor.DstSelect,
            Sampler: ToGuestSampler(samplerDescriptor),
            WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
            ArrayedView: isArrayed,
            Type: descriptor.Type,
            Depth: textureDepth);
        return true;
    }



    /// <summary>
    /// On PS5 render targets alias guest memory, so pixels the game wrote with
    /// the CPU are visible before the first GPU draw (Chowdren pre-fills its
    /// fog/overlay layers that way). Seed newly created Vulkan guest images
    /// with the current guest memory contents to preserve that base layer.
    /// </summary>
    private static void ProvideRenderTargetInitialData(
        CpuContext ctx,
        RenderTargetDescriptor target)
    {
        if (!GuestGpu.Current.GuestImageWantsInitialData(target.Address))
        {
            return;
        }

        var byteCount = VulkanVideoPresenter.GetGuestImageByteCount(
            target.Format,
            target.Width,
            target.Height);
        if (byteCount == 0 || byteCount > MaxPresentedTextureBytes)
        {
            return;
        }

        var initialData = new byte[byteCount];
        var readOk = ctx.Memory.TryRead(target.Address, initialData);
        var nonZero = readOk && initialData.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        if (_traceDraws && _rtSeedTraced.Add(target.Address))
        {
            Console.Error.WriteLine(
                $"[RTSEED] addr=0x{target.Address:X} {target.Width}x{target.Height} " +
                $"read={readOk} nonZero={nonZero}");
        }

        if (nonZero)
        {
            GuestGpu.Current.ProvideGuestImageInitialData(target.Address, initialData);
        }
    }

    private static readonly HashSet<ulong> _rtSeedTraced = new();

    private static void TraceDrawCompact(
        ulong sequence,
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (!_traceDraws)
        {
            return;
        }

        var target = draw.RenderTargets.FirstOrDefault();
        var blend = draw.RenderState.Blend;
        var viewport = draw.RenderState.Viewport is { } vp
            ? $"{vp.X:0.#},{vp.Y:0.#},{vp.Width:0.#}x{vp.Height:0.#}"
            : "none";
        var textureList = string.Join(
            '|',
            textures.Select(texture =>
                $"0x{texture.Address:X}:{texture.Width}x{texture.Height}" +
                $":f{texture.Format}/n{texture.NumberType}/d{texture.DstSelect:X3}" +
                (texture.IsFallback ? ":FALLBACK" : string.Empty)));
        var positions = string.Empty;
        var positionBuffer = vertexBuffers.FirstOrDefault(buffer => buffer.Location == 0);
        if (positionBuffer is { Length: >= 8 })
        {
            var stride = Math.Max(positionBuffer.Stride, 4u);
            var vertexTotal = (int)((positionBuffer.Length - positionBuffer.OffsetBytes) / stride);
            var sampled = new List<string>();
            foreach (var vertex in new[] { 0, 1, vertexTotal - 1 })
            {
                var baseOffset = (int)(positionBuffer.OffsetBytes + vertex * stride);
                if (vertex < 0 || baseOffset + 8 > positionBuffer.Length)
                {
                    continue;
                }

                sampled.Add(
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset):0.##}," +
                    $"{BitConverter.ToSingle(positionBuffer.Data, baseOffset + 4):0.##}");
            }

            positions = string.Join(';', sampled);
        }

        Console.Error.WriteLine(
            $"[DRAW] seq={sequence} es=0x{draw.ExportShaderAddress:X} ps=0x{draw.PixelShaderAddress:X} " +
            $"target=0x{target.Address:X}:{target.Width}x{target.Height}:f{target.Format}/n{target.NumberType} " +
            $"prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} indexed={draw.IndexBuffer is not null} " +
            $"blend={(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}/{blend.ColorDstFactor}/{blend.ColorFunc}" +
            $":a{blend.AlphaSrcFactor}/{blend.AlphaDstFactor}/{blend.AlphaFunc}/s{(blend.SeparateAlphaBlend ? 1 : 0)} " +
            $"mask=0x{blend.WriteMask:X} viewport={viewport} textures={textureList} pos={positions} " +
            $"ps_s0..3={string.Join(',', draw.PixelUserData.Take(4).Select(value => BitConverter.UInt32BitsToSingle(value).ToString("0.###")))} " +
            $"rawblend=0x{draw.RawBlendControl:X8} info=0x{draw.RawColorInfo:X8}");
    }

    private static void TraceDrawCompactMiss(ulong sequence, uint vertexCount, string error)
    {
        if (!_traceDraws)
        {
            return;
        }

        Console.Error.WriteLine($"[DRAW] seq={sequence} MISS verts={vertexCount} error={error}");
    }

    private static int _grassTraceCount;

    private static void TraceGrassDrawVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (_grassTraceCount >= 6 ||
            !textures.Any(texture => texture.Width == 288 && texture.Height == 160) ||
            vertexBuffers.Count == 0 ||
            Interlocked.Increment(ref _grassTraceCount) > 6)
        {
            return;
        }

        var text = new System.Text.StringBuilder();
        text.Append($"agc.grassdraw prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount} ");
        text.Append($"indexed={draw.IndexBuffer is not null} buffers={vertexBuffers.Count}");
        foreach (var buffer in vertexBuffers)
        {
            text.Append(
                $"\n  loc={buffer.Location} fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount} " +
                $"stride={buffer.Stride} offset={buffer.OffsetBytes} bytes={buffer.Length}");
            var stride = Math.Max(buffer.Stride, 4u);
            var maxVerts = Math.Min(6, (int)((buffer.Length - buffer.OffsetBytes) / stride));
            for (var vertex = 0; vertex < maxVerts; vertex++)
            {
                var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
                var components = Math.Min(4, (int)((buffer.Length - baseOffset) / 4));
                text.Append($"\n    v{vertex}:");
                for (var c = 0; c < components; c++)
                {
                    text.Append($" {BitConverter.ToSingle(buffer.Data, baseOffset + c * 4):0.#####}");
                }
            }
        }

        TraceAgcShader(text.ToString());
    }

    private static int _rectListTraceCount;

    private static void TraceRectListVertices(
        TranslatedGuestDraw draw,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers)
    {
        if (!AgcPrimitiveHelpers.IsRectListPrimitive(draw.PrimitiveType) ||
            _rectListTraceCount >= 16 ||
            Interlocked.Increment(ref _rectListTraceCount) > 16)
        {
            return;
        }

        var expanded = AgcPrimitiveHelpers.GetRectListDrawVertexCount(
            draw.PrimitiveType,
            draw.VertexCount,
            indexed: draw.IndexBuffer is not null,
            hasVertexBuffers: vertexBuffers.Count > 0);
        var text = new System.Text.StringBuilder();
        text.Append(
            $"agc.rectlist prim=0x{draw.PrimitiveType:X} verts={draw.VertexCount}->{expanded} " +
            $"indexed={(draw.IndexBuffer is not null ? 1 : 0)} vb={vertexBuffers.Count}");

        if (vertexBuffers.Count > 0)
        {
            var buffer = vertexBuffers[0];
            var stride = Math.Max(buffer.Stride, 4u);
            text.Append(
                $" stride={buffer.Stride} " +
                $"fmt={buffer.DataFormat}/{buffer.NumberFormat}x{buffer.ComponentCount}");
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var baseOffset = (int)(buffer.OffsetBytes + vertex * stride);
                if (baseOffset + 16 > buffer.Length)
                {
                    break;
                }

                var x = BitConverter.ToSingle(buffer.Data, baseOffset);
                var y = BitConverter.ToSingle(buffer.Data, baseOffset + 4);
                var z = BitConverter.ToSingle(buffer.Data, baseOffset + 8);
                var w = BitConverter.ToSingle(buffer.Data, baseOffset + 12);
                text.Append($" v{vertex}=({x:0.###},{y:0.###},{z:0.###},{w:0.###})");
            }
        }
        else
        {
            text.Append(" procedural=1");
        }

        TraceAgcShader(text.ToString());
    }

    private static int _textureDumpCount;
    private static readonly ConcurrentDictionary<string, int> _textureDumpKeys = new();

    /// <summary>
    /// Writes raw sampled-texture bytes (as read from guest memory) when
    /// SHARPEMU_TEXTURE_DUMP_DIR is set, so upload-time content can be
    /// inspected offline. File name records size and effective pitch.
    /// </summary>
    private static void DumpTextureSourceIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        // First uses plus periodic later snapshots (the game reuses the same
        // allocation for successive full-screen images).
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Writes the bytes after detiling when SHARPEMU_TEXTURE_LINEAR_DUMP_DIR is
    /// set. Keeping this separate from the raw-source dump makes AddrLib
    /// equation changes directly inspectable with ordinary image tools.
    /// </summary>
    private static void DumpLinearTextureIfRequested(
        in TextureDescriptor descriptor,
        uint sourcePitch,
        byte[] source)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_TEXTURE_LINEAR_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var key = $"linear-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}";
        var occurrence = _textureDumpKeys.AddOrUpdate(key, 1, static (_, count) => count + 1);
        if ((occurrence > 3 && occurrence % 500 >= 3) ||
            Interlocked.Increment(ref _textureDumpCount) > 200)
        {
            return;
        }

        var index = _textureDumpCount;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"{index:D3}-0x{descriptor.Address:X}-{descriptor.Width}x{descriptor.Height}" +
                $"-p{sourcePitch}-f{descriptor.Format}-t{descriptor.TileMode}.linear.bin");
            File.WriteAllBytes(path, source);
        }
        catch (IOException)
        {
        }
    }

    private static GuestDrawTexture CreateFallbackGuestDrawTexture(
        bool isStorage,
        uint format,
        uint numberType,
        bool isArrayed = false,
        uint type = Gen5TextureType2D,
        uint depth = 1)
    {
        var fallbackFormat = format == 0 ? 10u : format;
        var fallbackNumberType = numberType;
        return new(
            0,
            1,
            1,
            fallbackFormat,
            fallbackNumberType,
            [0, 0, 0, 255],
            IsFallback: true,
            IsStorage: isStorage,
            MipLevels: 1,
            MipLevel: 0,
            ArrayedView: isArrayed,
            Type: type,
            Depth: GetTextureVolumeDepth(type, depth));
    }

    private static GuestSampler ToGuestSampler(IReadOnlyList<uint> descriptor) =>
        descriptor.Count >= 4
            ? new GuestSampler(
                descriptor[0],
                descriptor[1],
                descriptor[2],
                descriptor[3])
            : default;

    internal static IReadOnlyList<uint> NormalizeSamplerDescriptorForImageOperation(
        IReadOnlyList<uint> descriptor)
    {
        const uint depthCompareMask = 0x7u << 12;
        if (descriptor.Count < 4 ||
            (descriptor[0] & depthCompareMask) == 0)
        {
            return descriptor;
        }

        // The shader translators perform guest depth comparisons after a
        // normal sample. The native sampler must not compare the value first.
        return
        [
            descriptor[0] & ~depthCompareMask,
            descriptor[1],
            descriptor[2],
            descriptor[3],
        ];
    }

    private static byte[] ConvertRgba16FloatToRgba8(ReadOnlySpan<byte> source, uint width, uint height)
    {
        var destination = new byte[checked((int)((ulong)width * height * 4))];
        var pixelCount = destination.Length / 4;
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var sourceOffset = pixel * 8;
            var destinationOffset = pixel * 4;
            destination[destinationOffset + 0] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[sourceOffset..]));
            destination[destinationOffset + 1] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 2)..]));
            destination[destinationOffset + 2] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 4)..]));
            destination[destinationOffset + 3] = HalfToByte(BinaryPrimitives.ReadUInt16LittleEndian(source[(sourceOffset + 6)..]));
        }

        return destination;
    }

    private static byte HalfToByte(ushort bits)
    {
        var value = (float)BitConverter.UInt16BitsToHalf(bits);
        if (!float.IsFinite(value))
        {
            return 0;
        }

        return (byte)Math.Clamp((int)MathF.Round(value * 255.0f), 0, 255);
    }

    private static bool TryReadComputeDispatch(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint opcode,
        out ComputeDispatch dispatch,
        out ulong indirectDimsRetryAddress)
    {
        dispatch = default;
        // Non-zero only when this is an INDIRECT dispatch whose dimensions read as
        // zero — meaning the producing GPU dispatch that computes them has not run
        // yet. The caller suspends on this address instead of dropping the work.
        indirectDimsRetryAddress = 0;
        ulong dimensionsAddress;
        uint initiator;
        string dispatchSource;
        if (opcode == ItDispatchDirect)
        {
            if (packetLength < 5 ||
                !TryReadUInt32(ctx, packetAddress + 16, out initiator))
            {
                return false;
            }

            dimensionsAddress = packetAddress + 4;
            dispatchSource = "direct";
        }
        else if (packetLength >= 4)
        {
            if (!TryReadUInt64(ctx, packetAddress + 4, out dimensionsAddress) ||
                !TryReadUInt32(ctx, packetAddress + 12, out initiator))
            {
                return false;
            }

            dispatchSource = "absolute-indirect";
        }
        else
        {
            if (packetLength < 3 ||
                state.IndirectArgsAddress == 0 ||
                !TryReadUInt32(ctx, packetAddress + 4, out var dataOffset) ||
                !TryReadUInt32(ctx, packetAddress + 8, out initiator))
            {
                return false;
            }

            dimensionsAddress = state.IndirectArgsAddress + dataOffset;
            dispatchSource = "base-indirect";
        }

        if ((initiator & 1) == 0 ||
            !TryReadUInt32(ctx, dimensionsAddress, out var dispatchEndX) ||
            !TryReadUInt32(ctx, dimensionsAddress + 4, out var dispatchEndY) ||
            !TryReadUInt32(ctx, dimensionsAddress + 8, out var dispatchEndZ))
        {
            return false;
        }

        if (dispatchEndX == 0 || dispatchEndY == 0 || dispatchEndZ == 0)
        {
            // For indirect dispatches (both absolute and base), zero dimensions are a valid outcome
            // of GPU culling passes (0 workgroups). VulkanVideoPresenter handles groupCount = 0 as a clean no-op.
            if (opcode == ItDispatchIndirect || dispatchSource is "absolute-indirect" or "base-indirect")
            {
                var waveCount = (initiator & (1u << 15)) != 0 ? 32u : 64u;
                dispatch = new ComputeDispatch(
                    0, 0, 0,
                    0, 0, 0,
                    waveCount,
                    IsIndirect: true,
                    0, 0, 0);
                return true;
            }

            return RejectComputeDispatch(
                dimensionsAddress,
                initiator,
                dispatchSource,
                dispatchEndX,
                dispatchEndY,
                dispatchEndZ,
                "zero-dimension");
        }

        // When FORCE_START_AT_000 is clear, RDNA2 interprets the three packet
        // values as end coordinates, not group counts. Vulkan expresses the
        // same operation as vkCmdDispatchBase(base, end - base). Ignoring the
        // COMPUTE_START registers turned small high-base clears into apparent
        // multi-million/billion-group dispatches and forced an unsafe cap.
        const uint forceStartAtZero = 1u << 2;
        const uint partialThreadGroupEnabled = 1u << 1;
        const uint useThreadDimensions = 1u << 5;
        uint baseGroupX = 0;
        uint baseGroupY = 0;
        uint baseGroupZ = 0;
        if ((initiator & forceStartAtZero) == 0)
        {
            state.ShRegisters.TryGetValue(ComputeStartX, out baseGroupX);
            state.ShRegisters.TryGetValue(ComputeStartY, out baseGroupY);
            state.ShRegisters.TryGetValue(ComputeStartZ, out baseGroupZ);
        }

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        uint groupCountX;
        uint groupCountY;
        uint groupCountZ;
        var threadCountX = uint.MaxValue;
        var threadCountY = uint.MaxValue;
        var threadCountZ = uint.MaxValue;
        if ((initiator & useThreadDimensions) != 0)
        {
            // In thread-dimension mode the packet contains thread counts, not
            // group end coordinates. Vulkan still dispatches whole workgroups,
            // so round up and pass the exact exclusive thread bounds to the
            // translated shader. Its entry guard disables invocations in the
            // partially populated final group before any guest instruction.
            var startThreadX = (ulong)baseGroupX * localSizeX;
            var startThreadY = (ulong)baseGroupY * localSizeY;
            var startThreadZ = (ulong)baseGroupZ * localSizeZ;
            if ((ulong)dispatchEndX <= startThreadX ||
                (ulong)dispatchEndY <= startThreadY ||
                (ulong)dispatchEndZ <= startThreadZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"thread-end-not-after-base(" +
                    $"{startThreadX}x{startThreadY}x{startThreadZ})");
            }

            groupCountX = CeilDivide((ulong)dispatchEndX - startThreadX, localSizeX);
            groupCountY = CeilDivide((ulong)dispatchEndY - startThreadY, localSizeY);
            groupCountZ = CeilDivide((ulong)dispatchEndZ - startThreadZ, localSizeZ);
            threadCountX = dispatchEndX;
            threadCountY = dispatchEndY;
            threadCountZ = dispatchEndZ;
        }
        else
        {
            if (dispatchEndX <= baseGroupX ||
                dispatchEndY <= baseGroupY ||
                dispatchEndZ <= baseGroupZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"end-not-after-base({baseGroupX}x{baseGroupY}x{baseGroupZ})");
            }

            groupCountX = dispatchEndX - baseGroupX;
            groupCountY = dispatchEndY - baseGroupY;
            groupCountZ = dispatchEndZ - baseGroupZ;
        }

        if ((initiator & partialThreadGroupEnabled) != 0)
        {
            var partialSizeX = GetComputePartialSize(state.ShRegisters, ComputeNumThreadX);
            var partialSizeY = GetComputePartialSize(state.ShRegisters, ComputeNumThreadY);
            var partialSizeZ = GetComputePartialSize(state.ShRegisters, ComputeNumThreadZ);
            if (partialSizeX == 0 || partialSizeX > localSizeX ||
                partialSizeY == 0 || partialSizeY > localSizeY ||
                partialSizeZ == 0 || partialSizeZ > localSizeZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"invalid-partial-size({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }

            if (partialSizeX != localSizeX ||
                partialSizeY != localSizeY ||
                partialSizeZ != localSizeZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"unrepresentable-partial-group({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }
        }

        var waveLaneCount = (initiator & (1u << 15)) != 0 ? 32u : 64u;

        if (_traceAgcShader &&
            ((ulong)groupCountX * groupCountY * groupCountZ >= 1_000_000UL ||
             groupCountX >= 1_000_000u))
        {
            lock (_submitTraceGate)
            {
                if (_tracedDispatchArguments.Add(
                        (dimensionsAddress, groupCountX, groupCountY, groupCountZ)))
                {
                    TraceAgcShader(
                        $"agc.dispatch_args source={dispatchSource} op=0x{opcode:X2} " +
                        $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                        $"packet=0x{packetAddress:X16} len={packetLength} " +
                        $"dims=0x{dimensionsAddress:X16} " +
                        $"raw={dispatchEndX:X8}/{dispatchEndY:X8}/{dispatchEndZ:X8} " +
                        $"base={baseGroupX:X8}/{baseGroupY:X8}/{baseGroupZ:X8} " +
                        $"count={groupCountX:X8}/{groupCountY:X8}/{groupCountZ:X8} " +
                        $"wave={waveLaneCount} " +
                        $"initiator=0x{initiator:X8} " +
                        $"indirect_base=0x{state.IndirectArgsAddress:X16}");
                }
            }
        }

        dispatch = new ComputeDispatch(
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            waveLaneCount,
            IsIndirect: opcode == ItDispatchIndirect,
            threadCountX,
            threadCountY,
            threadCountZ);
        return true;
    }

    private static uint CeilDivide(ulong value, uint divisor) =>
        checked((uint)((value + divisor - 1) / divisor));

    private static bool RejectComputeDispatch(
        ulong dimensionsAddress,
        uint initiator,
        string source,
        uint rawX,
        uint rawY,
        uint rawZ,
        string reason)
    {
        lock (_submitTraceGate)
        {
            if (_rejectedDispatchArguments.Count < 256 &&
                _rejectedDispatchArguments.Add((dimensionsAddress, initiator, reason)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.dispatch_reject source={source} " +
                    $"dims=0x{dimensionsAddress:X16} raw={rawX:X8}/{rawY:X8}/{rawZ:X8} " +
                    $"initiator=0x{initiator:X8} reason={reason}");
            }
        }

        return false;
    }

    private static void ObserveComputeDispatch(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ComputeDispatch dispatch)
    {
        if (!TryGetShaderAddress(
                state.ShRegisters,
                ComputePgmLo,
                ComputePgmHi,
                out var shaderAddress))
        {
            return;
        }

        var sequence = ++gpuState.WorkSequence;
        ulong shaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(shaderAddress, out shaderHeader);
        }

        var computeSystemRegisters = DecodeComputeSystemRegisters(state.ShRegisters);
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                shaderAddress,
                shaderHeader,
                state.ShRegisters,
                ComputeUserDataRegister,
                out var shaderState,
                out var error,
                computeSystemRegisters) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                shaderState,
                out var evaluation,
                out error))
        {
            lock (_submitTraceGate)
            {
                if (_tracedComputeShaders.Add(shaderAddress))
                {
                    TraceAgcShader(
                        $"agc.compute_shader cs=0x{shaderAddress:X16} error={error}");
                }
            }

            return;
        }

        var bindings = evaluation.ImageBindings;
        var describeBindings =
            _traceAgcShader || _traceComputeShaderAddress == shaderAddress;
        var descriptions = describeBindings
            ? new List<string>(bindings.Count)
            : null;
        var translatedBindings = new List<TranslatedImageBinding>(bindings.Count);
        var hasStorageBinding = false;
        foreach (var binding in bindings)
        {
            var isStorage = Gen5ShaderTranslator.RequiresStorageImage(binding, bindings);
            var writesStorage = Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
            var descriptorValid = TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture);
            if (!descriptorValid)
            {
                texture = CreateFallbackTextureDescriptor(
                    binding.ResourceDescriptor,
                    binding.Control.Dimension);
            }

            translatedBindings.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    binding.MipLevel ?? 0,
                    NormalizeSamplerDescriptorForImageOperation(
                        binding.SamplerDescriptor),
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding)));
            hasStorageBinding |= isStorage;

            if (descriptions is not null)
            {
                var descriptorState = descriptorValid ? string.Empty : "/invalid-desc";
                descriptions.Add(
                    $"{binding.Opcode}@0x{binding.Pc:X}:" +
                    $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                    $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                    $"{descriptorState}/{ProbeTexture(ctx, texture)}");
            }
            if (writesStorage && descriptorValid && texture.Address != 0)
            {
                gpuState.ComputeImageWriters[texture.Address] = new ComputeImageWriter(
                    sequence,
                    shaderAddress,
                    binding.Opcode);

                // Check if this compute shader writes to a CMASK address
                // (shadPS4's IsComputeMetaClear logic)
                CheckCmaskWrite(texture.Address, gpuState);

                TraceAgcShader(
                    $"agc.compute_writer addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} tile={texture.TileMode} " +
                    $"size={texture.Width}x{texture.Height} " +
                    $"cs=0x{shaderAddress:X16} op={binding.Opcode}");
            }
        }

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        if (_traceComputeShaderAddress == shaderAddress && descriptions is not null)
        {
            var globalHeads = evaluation.GlobalMemoryBindings.Count == 0
                ? string.Empty
                : $" global_heads=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                    binding =>
                        $"0x{binding.BaseAddress:X16}:{binding.DataLength}:" +
                        Convert.ToHexString(binding.Data.AsSpan(
                            0,
                            Math.Min(binding.DataLength, 512)))))}]";
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.compute_dispatch_trace seq={sequence} " +
                $"cs=0x{shaderAddress:X16} " +
                $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                $"local={localSizeX}x{localSizeY}x{localSizeZ}" +
                globalHeads +
                $" bindings=[{string.Join(',', descriptions)}]");
        }

        var writesGlobalMemory = evaluation.GlobalMemoryBindings.Any(static binding =>
            binding.Writable);
        var gpuDispatch = false;
        var evaluationHandledByCpu = false;
        var computeError = string.Empty;
        // Empty SRT/EUD with a recorded null-base scalar pointer fallback
        // produces Address-0 storage that can lose the Vulkan device on submit.
        var emptyResourceTables =
            shaderState.Metadata is
            {
                ShaderResourceTableSizeDwords: 0,
                ExtendedUserDataSizeDwords: 0,
            };
        if (emptyResourceTables &&
            (Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress) ||
             (translatedBindings.All(static binding => binding.Descriptor.Address == 0) &&
              !evaluation.GlobalMemoryBindings.Any(static binding => binding.BaseAddress != 0))))
        {
            computeError = Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress)
                ? "empty-srt-scalar-pointer-fallback"
                : "empty-srt-no-usable-resources";
            lock (_submitTraceGate)
            {
                if (_tracedComputeShaders.Add(shaderAddress))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.compute_reject cs=0x{shaderAddress:X16} " +
                        $"source={(dispatch.IsIndirect ? "indirect" : "direct")} " +
                        $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                        $"reason={computeError}");
                }
            }
        }
        else if (!hasStorageBinding &&
            writesGlobalMemory &&
            TrySubmitMaskedDwordCopyKernel(
                ctx,
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var semanticCopySequence,
                out var copyDescription))
        {
            gpuDispatch = true;
            evaluationHandledByCpu = true;
            TraceAgcShader(
                $"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                copyDescription);
            // The scalar evaluator snapshots guest buffers while parsing the
            // command stream.  Do not let another submission (or the CPU)
            // observe that snapshot until the semantic replacement has
            // reached the same CPU-visible completion point as a translated
            // writable-buffer dispatch below.  Returning early here allowed
            // the guest to reuse a transient heap while its delayed clear was
            // still queued, so the clear could erase newly constructed CPU
            // objects.  Waiting on the work sequence also retires preceding
            // Vulkan writes before the next evaluator snapshot is captured.
            if (!GuestGpu.Current.WaitForGuestWork(semanticCopySequence))
            {
                computeError =
                    $"semantic-global-write-sync-timeout sequence={semanticCopySequence}";
            }
        }
        else if (!hasStorageBinding &&
            writesGlobalMemory &&
            TrySubmitConstantFillKernel(
                ctx,
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var semanticFillSequence,
                out var fillDescription))
        {
            gpuDispatch = true;
            evaluationHandledByCpu = true;
            TraceAgcShader(
                $"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                fillDescription);
            // Same CPU-visibility ordering requirement as the masked-copy
            // replacement above.
            if (!VulkanVideoPresenter.WaitForGuestWork(semanticFillSequence))
            {
                computeError =
                    $"semantic-global-write-sync-timeout sequence={semanticFillSequence}";
            }
        }
        else if ((hasStorageBinding || writesGlobalMemory) &&
            (ulong)localSizeX * localSizeY * localSizeZ <= 1024)
        {
            var shaderKey = (
                shaderAddress,
                _bakeScalars
                    ? ComputeShaderStateFingerprint(evaluation)
                    : ComputeShaderStructuralFingerprint(evaluation),
                localSizeX,
                localSizeY,
                localSizeZ,
                dispatch.WaveLaneCount,
                _storageBufferOffsetAlignment);
            var guestGlobalBufferCount = evaluation.GlobalMemoryBindings.Count;
            var totalGlobalBufferCount = _bakeScalars
                ? guestGlobalBufferCount
                : guestGlobalBufferCount + 1;
            _computeShaderCache.TryGetValue(shaderKey, out var computeShader);

            if (computeShader is null &&
                GuestGpu.Current.TryCompileComputeShader(
                    shaderState,
                    evaluation,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    out computeShader,
                    out computeError,
                    totalGlobalBufferCount,
                    initialScalarBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount,
                    waveLaneCount: dispatch.WaveLaneCount,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment))
            {
                DumpCompiledShader(
                    "cs",
                    shaderAddress,
                    shaderKey.Item2,
                    computeShader!,
                    shaderState.Program);
            }

            if (computeShader is not null)
            {
                _computeShaderCache.TryAdd(shaderKey, computeShader);

                var textures = CreateGuestDrawTextures(
                    ctx,
                    translatedBindings,
                    out _);
                var globalMemoryBuffers =
                    CreateTranslatedComputeGlobalBuffers(evaluation);
                GuestGpu.Current.SubmitComputeDispatch(
                    shaderAddress,
                    computeShader,
                    textures,
                    globalMemoryBuffers,
                    dispatch.GroupCountX,
                    dispatch.GroupCountY,
                    dispatch.GroupCountZ,
                    dispatch.BaseGroupX,
                    dispatch.BaseGroupY,
                    dispatch.BaseGroupZ,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    dispatch.IsIndirect,
                    writesGlobalMemory,
                    dispatch.ThreadCountX,
                    dispatch.ThreadCountY,
                    dispatch.ThreadCountZ);
                // Vulkan queue order keeps dependent dispatches coherent. CPU visibility is
                // published by explicit PM4 release/write actions instead of per dispatch.
                gpuDispatch = true;
            }
        }

        const int blitCount = 0;

        if (gpuDispatch)
        {
            var metadataBindings = evaluation.GlobalMemoryBindings
                .Where(binding =>
                    gpuState.HtileMetadata.IsRegistered(binding.BaseAddress))
                .ToArray();
            var hasMetadataWrite = metadataBindings.Any(static binding => binding.Writable);
            var instructionsByPc = hasMetadataWrite
                ? shaderState.Program.Instructions.ToDictionary(
                    static instruction => instruction.Pc,
                    static instruction => instruction.Opcode)
                : null;
            var hasWriteOnlyMetadataAccess =
                instructionsByPc is not null &&
                metadataBindings.All(binding =>
                    IsHtileMetadataWriteOnlyAccess(
                        binding,
                        instructionsByPc));
            if (hasMetadataWrite &&
                hasWriteOnlyMetadataAccess &&
                !shaderState.Program.Instructions.Any(static instruction =>
                    instruction.Opcode.Contains("Xor", StringComparison.Ordinal)))
            {
                foreach (var binding in evaluation.GlobalMemoryBindings)
                {
                    if (!binding.Writable ||
                        !gpuState.HtileMetadata.IsRegistered(binding.BaseAddress))
                    {
                        continue;
                    }

                    MarkHtileMetadataClear(
                        gpuState,
                        binding.BaseAddress,
                        _traceDepthMetadata
                            ? $"compute:0x{shaderAddress:X16}"
                            : string.Empty);
                }
            }
        }

        lock (_submitTraceGate)
        {
            if (_traceAgcShader &&
                descriptions is not null &&
                _tracedComputeShaders.Add(shaderAddress))
            {
                var globalBuffers = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_buffers=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding => $"0x{binding.BaseAddress:X16}:{binding.DataLength}"))}]";
                var scalarProbe = string.Join(
                    ',',
                    evaluation.InitialScalarRegisters
                        .Take(16)
                        .Select((value, index) => $"s{index}={value:X8}"));
                var globalProbes = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_heads=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"0x{binding.BaseAddress:X16}:" +
                            Convert.ToHexString(binding.Data.AsSpan(
                                0,
                                Math.Min(binding.DataLength, 16)))))}]";
                var globalDescriptors = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_descriptors=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"s{binding.ScalarAddress}=" +
                            string.Join(':', evaluation.ScalarRegisters
                                .Skip(checked((int)binding.ScalarAddress))
                                .Take(4)
                                .Select(value => $"{value:X8}"))))}]";
                var opcodes = string.Join(
                    ',',
                    shaderState.Program.Instructions
                        .Select(instruction => instruction.Opcode)
                        .Distinct()
                        .Take(48));
                TraceAgcShader(
                    $"agc.compute_shader cs=0x{shaderAddress:X16} " +
                    $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                    $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                    $"wave={dispatch.WaveLaneCount} " +
                    $"local={localSizeX}x{localSizeY}x{localSizeZ} " +
                    $"sys={DescribeComputeSystemRegisters(computeSystemRegisters)} " +
                    $"gpu={gpuDispatch} blits={blitCount} globals={evaluation.GlobalMemoryBindings.Count} " +
                    $"global_writes={writesGlobalMemory}" +
                    (computeError.Length == 0 ? string.Empty : $" error={computeError}") +
                    $" sgprs=[{scalarProbe}]" +
                    globalBuffers +
                    globalProbes +
                    globalDescriptors +
                    $" opcodes=[{opcodes}]" +
                    $" bindings=[{string.Join(',', descriptions)}]");
            }
        }

        // Rejected/CPU-handled dispatches never hand evaluation's pooled buffers to a
        // consumer that would return them; reclaim here to keep GuestDataPool.Shared bounded.
        if (evaluationHandledByCpu || !gpuDispatch)
        {
            ReturnPooledEvaluationArrays(evaluation);
        }
    }

    private static bool IsHtileMetadataWriteOnlyAccess(
        Gen5GlobalMemoryBinding binding,
        IReadOnlyDictionary<uint, string> instructionsByPc)
    {
        if (!binding.Writable || binding.InstructionPcs.Count == 0)
        {
            return false;
        }

        foreach (var pc in binding.InstructionPcs)
        {
            if (!instructionsByPc.TryGetValue(pc, out var opcode) ||
                !IsHtileMetadataWriteOnlyOpcode(opcode))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsHtileMetadataWriteOnlyOpcode(string opcode) =>
        opcode.StartsWith("BufferStore", StringComparison.Ordinal) ||
        opcode.StartsWith("TBufferStore", StringComparison.Ordinal) ||
        opcode.StartsWith("GlobalStore", StringComparison.Ordinal) ||
        opcode.StartsWith("FlatStore", StringComparison.Ordinal);

    internal static void UnregisterHtileMetadataRange(
        ICpuMemory memory,
        ulong address,
        ulong length)
    {
        if (!_submittedGpuStates.TryGetValue(
                CanonicalMemory(memory),
                out var gpuState))
        {
            return;
        }

        gpuState.HtileMetadata.UnregisterRange(address, length);
    }

    /// <summary>
    /// Recognizes the SDK's masked-dword resource initialization kernel and
    /// executes its exact semantics over the guest-memory window that the
    /// emulator can map. The guest dispatches this kernel over multi-gigabyte
    /// virtual heaps (up to ~67 million 64-lane workgroups); translating every
    /// out-of-window invocation to Vulkan dominated startup despite those
    /// stores being bounds-discarded. This is a semantic kernel replacement,
    /// not a generic dispatch cap: the complete instruction shape and SGPR
    /// bindings must match before the ordered CPU action is used.
    /// </summary>
    private static bool TrySubmitMaskedDwordCopyKernel(
        CpuContext ctx,
        Gen5ShaderProgram program,
        Gen5ShaderEvaluation evaluation,
        ComputeDispatch dispatch,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out long workSequence,
        out string description)
    {
        workSequence = 0;
        description = string.Empty;
        var instructions = program.Instructions;
        string[] expectedOpcodes =
        [
            "SMovB32",
            "STtraceData",
            "SInstPrefetch",
            "VLshlAddU32",
            "SBufferLoadDword",
            "SWaitcnt",
            "VCmpxGtU32",
            "SCbranchExecz",
            "SBufferLoadDword",
            "SWaitcnt",
            "VAndB32",
            "BufferLoadFormatX",
            "SWaitcnt",
            "BufferStoreFormatX",
            "SEndpgm",
        ];
        if (instructions.Count != expectedOpcodes.Length ||
            !instructions.Select(static instruction => instruction.Opcode)
                .SequenceEqual(expectedOpcodes) ||
            !IsExactMaskedDwordCopyInstructionShape(instructions) ||
            dispatch.BaseGroupX != 0 ||
            dispatch.BaseGroupY != 0 ||
            dispatch.BaseGroupZ != 0 ||
            dispatch.GroupCountY != 1 ||
            dispatch.GroupCountZ != 1 ||
            localSizeX != 64 ||
            localSizeY != 1 ||
            localSizeZ != 1 ||
            evaluation.ComputeSystemRegisters?.WorkGroupXRegister != 12)
        {
            return false;
        }

        var control = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 8 && !binding.Writable);
        var source = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 0 && !binding.Writable);
        var destination = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 4 &&
                              binding.Writable &&
                              binding.WriteBackToGuest);
        if (control is null || source is null || destination is null ||
            control.DataLength < 2 * sizeof(uint) ||
            source.DataLength < sizeof(uint) ||
            destination.BaseAddress == 0 ||
            destination.DataLength < sizeof(uint) ||
            !IsExactMaskedDwordCopyDescriptor(
                evaluation.InitialScalarRegisters,
                source.ScalarAddress,
                source.BaseAddress) ||
            !IsExactMaskedDwordCopyDescriptor(
                evaluation.InitialScalarRegisters,
                destination.ScalarAddress,
                destination.BaseAddress))
        {
            return false;
        }

        var elementCount = BinaryPrimitives.ReadUInt32LittleEndian(
            control.Data.AsSpan(0, sizeof(uint)));
        var sourceMask = BinaryPrimitives.ReadUInt32LittleEndian(
            control.Data.AsSpan(sizeof(uint), sizeof(uint)));
        var dispatchedThreads = dispatch.ThreadCountX != uint.MaxValue
            ? dispatch.ThreadCountX
            : Math.Min(
                (ulong)uint.MaxValue,
                (ulong)dispatch.GroupCountX * localSizeX);
        var writableDwords = (uint)(destination.DataLength / sizeof(uint));
        var outputDwords = (uint)Math.Min(
            Math.Min((ulong)elementCount, dispatchedThreads),
            writableDwords);
        if (outputDwords == 0)
        {
            return false;
        }

        var output = new byte[checked((int)outputDwords * sizeof(uint))];
        var outputWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            output.AsSpan());
        if (sourceMask == 0)
        {
            outputWords.Fill(BinaryPrimitives.ReadUInt32LittleEndian(
                source.Data.AsSpan(0, sizeof(uint))));
        }
        else
        {
            var sourceWords = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                source.Data.AsSpan(0, source.DataLength - (source.DataLength % sizeof(uint))));
            for (uint index = 0; index < outputDwords; index++)
            {
                var sourceIndex = index & sourceMask;
                outputWords[(int)index] = sourceIndex < (uint)sourceWords.Length
                    ? sourceWords[(int)sourceIndex]
                    : 0;
            }
        }

        var destinationAddress = destination.BaseAddress;
        workSequence = GuestGpu.Current.SubmitOrderedGuestAction(
            () =>
            {
                if (!ctx.Memory.TryWrite(destinationAddress, output))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] AGC masked-copy fast path failed " +
                        $"dst=0x{destinationAddress:X16} bytes={output.Length}");
                    return;
                }

                GuestImageWriteTracker.Track(
                    destinationAddress,
                    (ulong)output.Length,
                    GuestGpu.Current.CurrentGuestWorkSequenceForDiagnostics,
                    "agc.masked-dword-copy");
            },
            $"masked_dword_copy dst=0x{destinationAddress:X16} bytes={output.Length}");
        description =
            $"dst=0x{destinationAddress:X16} bytes={output.Length} " +
            $"elements={elementCount} mask=0x{sourceMask:X8} " +
            $"dispatch={dispatch.GroupCountX}x{localSizeX}";
        return workSequence > 0;
    }

    private static bool IsExactMaskedDwordCopyInstructionShape(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        static bool IsOperand(
            Gen5Operand operand,
            Gen5OperandKind kind,
            uint value) =>
            operand.Kind == kind && operand.Value == value;

        static bool IsBufferControl(
            Gen5ShaderInstruction instruction,
            uint vectorAddress,
            uint vectorData,
            uint scalarResource) =>
            instruction.Control is Gen5BufferMemoryControl
            {
                DwordCount: 1,
                OffsetBytes: 0,
                IndexEnabled: true,
                OffsetEnabled: false,
            } control &&
            control.VectorAddress == vectorAddress &&
            control.VectorData == vectorData &&
            control.ScalarResource == scalarResource;

        static bool IsScalarLoad(
            Gen5ShaderInstruction instruction,
            int offsetBytes) =>
            instruction.Control is Gen5ScalarMemoryControl
            {
                DestinationCount: 1,
                DynamicOffsetRegister: null,
            } control &&
            control.ImmediateOffsetBytes == offsetBytes &&
            instruction.Destinations.Count == 1 &&
            IsOperand(
                instruction.Destinations[0],
                Gen5OperandKind.ScalarRegister,
                106) &&
            instruction.Sources.Count >= 1 &&
            IsOperand(
                instruction.Sources[0],
                Gen5OperandKind.ScalarRegister,
                8);

        // This replacement depends on the operands as much as the opcode
        // sequence. Reversing V_CMPX_GT or enabling offen on either MUBUF
        // operation changes the set or address of written lanes.
        var globalId = instructions[3];
        var compare = instructions[6];
        var sourceIndex = instructions[10];
        var load = instructions[11];
        var store = instructions[13];
        return
            globalId.Destinations.Count == 1 &&
            IsOperand(globalId.Destinations[0], Gen5OperandKind.VectorRegister, 0) &&
            globalId.Sources.Count == 3 &&
            IsOperand(globalId.Sources[0], Gen5OperandKind.ScalarRegister, 12) &&
            IsOperand(globalId.Sources[1], Gen5OperandKind.EncodedConstant, 134) &&
            IsOperand(globalId.Sources[2], Gen5OperandKind.VectorRegister, 0) &&
            IsScalarLoad(instructions[4], offsetBytes: 0) &&
            compare.Sources.Count == 2 &&
            IsOperand(compare.Sources[0], Gen5OperandKind.ScalarRegister, 106) &&
            IsOperand(compare.Sources[1], Gen5OperandKind.VectorRegister, 0) &&
            instructions[7].Words.Count == 1 &&
            (instructions[7].Words[0] & 0xFFFFu) == 9 &&
            IsScalarLoad(instructions[8], offsetBytes: sizeof(uint)) &&
            sourceIndex.Destinations.Count == 1 &&
            IsOperand(sourceIndex.Destinations[0], Gen5OperandKind.VectorRegister, 1) &&
            sourceIndex.Sources.Count == 2 &&
            IsOperand(sourceIndex.Sources[0], Gen5OperandKind.ScalarRegister, 106) &&
            IsOperand(sourceIndex.Sources[1], Gen5OperandKind.VectorRegister, 0) &&
            IsBufferControl(load, vectorAddress: 1, vectorData: 1, scalarResource: 0) &&
            IsBufferControl(store, vectorAddress: 0, vectorData: 1, scalarResource: 4);
    }

    /// <summary>
    /// Semantic CPU replacement for Yotei's constant-fill kernel (v4 =
    /// wgid*64 + tid; BufferStoreFormatXyzw writes s4..s7 at record v4). The
    /// translated Vulkan form measured ~2.2s per dispatch against
    /// microseconds for the CPU fill. Same discipline as the masked-dword-copy
    /// replacement above: full instruction shape and descriptor must match.
    /// </summary>
    private static bool TrySubmitConstantFillKernel(
        CpuContext ctx,
        Gen5ShaderProgram program,
        Gen5ShaderEvaluation evaluation,
        ComputeDispatch dispatch,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        out long workSequence,
        out string description)
    {
        workSequence = 0;
        description = string.Empty;
        var instructions = program.Instructions;
        string[] expectedOpcodes =
        [
            "VLshlAddU32",
            "VMovB32",
            "VMovB32",
            "VMovB32",
            "VMovB32",
            "BufferStoreFormatXyzw",
            "SEndpgm",
        ];
        if (instructions.Count != expectedOpcodes.Length ||
            !instructions.Select(static instruction => instruction.Opcode)
                .SequenceEqual(expectedOpcodes) ||
            !IsExactConstantFillInstructionShape(instructions) ||
            dispatch.BaseGroupX != 0 ||
            dispatch.BaseGroupY != 0 ||
            dispatch.BaseGroupZ != 0 ||
            dispatch.GroupCountY != 1 ||
            dispatch.GroupCountZ != 1 ||
            localSizeX != 64 ||
            localSizeY != 1 ||
            localSizeZ != 1 ||
            evaluation.ComputeSystemRegisters?.WorkGroupXRegister != 8)
        {
            return false;
        }

        var destination = evaluation.GlobalMemoryBindings.SingleOrDefault(
            static binding => binding.ScalarAddress == 0 &&
                              binding.Writable &&
                              binding.WriteBackToGuest);
        var scalars = evaluation.InitialScalarRegisters;
        if (destination is null ||
            destination.BaseAddress == 0 ||
            destination.DataLength < FillRecordBytes ||
            scalars.Count < 8 ||
            !IsExactConstantFillDescriptor(scalars, destination.BaseAddress))
        {
            return false;
        }

        var numRecords = scalars[2];
        var dispatchedThreads = dispatch.ThreadCountX != uint.MaxValue
            ? dispatch.ThreadCountX
            : Math.Min(
                (ulong)uint.MaxValue,
                (ulong)dispatch.GroupCountX * localSizeX);
        var writableRecords = (uint)(destination.DataLength / FillRecordBytes);
        var outputRecords = (uint)Math.Min(
            Math.Min((ulong)numRecords, dispatchedThreads),
            writableRecords);
        if (outputRecords == 0)
        {
            return false;
        }

        var pattern = new byte[FillRecordBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(0), scalars[4]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(4), scalars[5]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(8), scalars[6]);
        BinaryPrimitives.WriteUInt32LittleEndian(pattern.AsSpan(12), scalars[7]);
        var output = new byte[checked((int)outputRecords * FillRecordBytes)];
        var outputWindow = output.AsSpan();
        for (var offset = 0; offset < outputWindow.Length; offset += FillRecordBytes)
        {
            pattern.CopyTo(outputWindow[offset..]);
        }

        var destinationAddress = destination.BaseAddress;
        workSequence = VulkanVideoPresenter.SubmitOrderedGuestAction(
            () =>
            {
                if (!ctx.Memory.TryWrite(destinationAddress, output))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] AGC constant-fill fast path failed " +
                        $"dst=0x{destinationAddress:X16} bytes={output.Length}");
                    return;
                }

GuestImageWriteTracker.Track(
                    destinationAddress,
                    (ulong)output.Length,
                    VulkanVideoPresenter.CurrentGuestWorkSequenceForDiagnostics,
                    "agc.constant-fill");

                VulkanVideoPresenter.RequestGuestColorClear(destinationAddress);

            },
            $"constant_fill dst=0x{destinationAddress:X16} bytes={output.Length}");
        description =
            $"dst=0x{destinationAddress:X16} bytes={output.Length} " +
            $"records={outputRecords} pattern=0x{scalars[7]:X8}{scalars[6]:X8}{scalars[5]:X8}{scalars[4]:X8} " +
            $"dispatch={dispatch.GroupCountX}x{localSizeX}";
        return workSequence > 0;
    }

    private const int FillRecordBytes = 4 * sizeof(uint);

    private static bool IsExactConstantFillInstructionShape(
        IReadOnlyList<Gen5ShaderInstruction> instructions)
    {
        static bool IsOperand(
            Gen5Operand operand,
            Gen5OperandKind kind,
            uint value) =>
            operand.Kind == kind && operand.Value == value;

        var globalId = instructions[0];
        var store = instructions[5];
        if (globalId.Destinations.Count != 1 ||
            !IsOperand(globalId.Destinations[0], Gen5OperandKind.VectorRegister, 4) ||
            globalId.Sources.Count != 3 ||
            !IsOperand(globalId.Sources[0], Gen5OperandKind.ScalarRegister, 8) ||
            !IsOperand(globalId.Sources[1], Gen5OperandKind.EncodedConstant, 134) ||
            !IsOperand(globalId.Sources[2], Gen5OperandKind.VectorRegister, 0))
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            var move = instructions[1 + index];
            if (move.Destinations.Count != 1 ||
                !IsOperand(
                    move.Destinations[0],
                    Gen5OperandKind.VectorRegister,
                    (uint)index) ||
                move.Sources.Count != 1 ||
                !IsOperand(
                    move.Sources[0],
                    Gen5OperandKind.ScalarRegister,
                    (uint)(4 + index)))
            {
                return false;
            }
        }

        return store.Control is Gen5BufferMemoryControl
        {
            DwordCount: 4,
            OffsetBytes: 0,
            IndexEnabled: true,
            OffsetEnabled: false,
            Glc: false,
            Slc: false,
        } control &&
            control.VectorAddress == 4 &&
            control.VectorData == 0 &&
            control.ScalarResource == 0;
    }

    private static bool IsExactConstantFillDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        ulong expectedBaseAddress)
    {
        var word0 = scalarRegisters[0];
        var word1 = scalarRegisters[1];
        var word3 = scalarRegisters[3];
        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var cacheSwizzle = (word1 & (1u << 30)) != 0;
        var swizzleEnabled = (word1 & (1u << 31)) != 0;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var addTidEnabled = (word3 & (1u << 23)) != 0;
        var outOfBoundsSelect = (word3 >> 28) & 0x3u;
        var type = word3 >> 30;
        var dstSelectX = word3 & 0x7u;

        var matches = baseAddress == expectedBaseAddress &&
            stride == FillRecordBytes &&
            !cacheSwizzle &&
            !swizzleEnabled &&
            unifiedFormat == BufFmt32323232Uint &&
            !addTidEnabled &&
            outOfBoundsSelect == 0 &&
            type == 0 &&
            dstSelectX == 4;
        if (!matches && baseAddress == expectedBaseAddress && _traceAgcShader)
        {
            // Shape matched but the descriptor didn't: dump the raw V# so the
            // constants above can be corrected from evidence, not guessed.
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.constant_fill_descriptor_mismatch " +
                $"word1=0x{word1:X8} word3=0x{word3:X8} stride={stride} " +
                $"format={unifiedFormat} oob={outOfBoundsSelect} type={type} " +
                $"dst_sel_x={dstSelectX}");
        }

        return matches;
    }

    // RDNA2 table 37: BUF_FMT_32_32_32_32_FLOAT. Bit-preserving, so the raw
    // dword copy has identical semantics to the UINT variant.
    private const uint BufFmt32323232Uint = 75;

    private static bool IsExactMaskedDwordCopyDescriptor(
        IReadOnlyList<uint> scalarRegisters,
        uint scalarBase,
        ulong expectedBaseAddress)
    {
        if (scalarBase + 3 >= scalarRegisters.Count)
        {
            return false;
        }

        var word0 = scalarRegisters[(int)scalarBase];
        var word1 = scalarRegisters[(int)scalarBase + 1];
        var word3 = scalarRegisters[(int)scalarBase + 3];
        var baseAddress = word0 | ((ulong)(word1 & 0xFFFFu) << 32);
        var stride = (word1 >> 16) & 0x3FFFu;
        var cacheSwizzle = (word1 & (1u << 30)) != 0;
        var swizzleEnabled = (word1 & (1u << 31)) != 0;
        var unifiedFormat = (word3 >> 12) & 0x7Fu;
        var addTidEnabled = (word3 & (1u << 23)) != 0;
        var outOfBoundsSelect = (word3 >> 28) & 0x3u;
        var type = word3 >> 30;
        var dstSelectX = word3 & 0x7u;

        // RDNA2 tables 35 and 37: OOB_SELECT=0 is structured indexing, so
        // NUM_RECORDS counts stride-sized records. FORMAT=20 is 32_UINT and
        // dst_sel_x=4 selects its R component. ADD_TID and either swizzle bit
        // alter addressing and are therefore outside this replacement.
        return baseAddress == expectedBaseAddress &&
               stride == sizeof(uint) &&
               !cacheSwizzle &&
               !swizzleEnabled &&
               unifiedFormat == 20 &&
               !addTidEnabled &&
               outOfBoundsSelect == 0 &&
               type == 0 &&
               dstSelectX == 4;
    }

    private static Gen5ComputeSystemRegisters DecodeComputeSystemRegisters(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(ComputePgmRsrc2, out var rsrc2);
        var nextRegister = (rsrc2 >> 1) & 0x1Fu;
        uint? workGroupX = null;
        uint? workGroupY = null;
        uint? workGroupZ = null;
        uint? threadGroupSize = null;

        if ((rsrc2 & (1u << 7)) != 0)
        {
            workGroupX = nextRegister++;
        }

        if ((rsrc2 & (1u << 8)) != 0)
        {
            workGroupY = nextRegister++;
        }

        if ((rsrc2 & (1u << 9)) != 0)
        {
            workGroupZ = nextRegister++;
        }

        if ((rsrc2 & (1u << 10)) != 0)
        {
            threadGroupSize = nextRegister++;
        }

        return new Gen5ComputeSystemRegisters(
            workGroupX,
            workGroupY,
            workGroupZ,
            threadGroupSize);
    }

    private static string DescribeComputeSystemRegisters(Gen5ComputeSystemRegisters registers) =>
        $"x={DescribeRegister(registers.WorkGroupXRegister)}," +
        $"y={DescribeRegister(registers.WorkGroupYRegister)}," +
        $"z={DescribeRegister(registers.WorkGroupZRegister)}," +
        $"size={DescribeRegister(registers.ThreadGroupSizeRegister)}";

    private static string DescribeRegister(uint? register) =>
        register.HasValue ? $"s{register.Value}" : "-";

    private static uint SelectExportUserDataRegister(
        IReadOnlyDictionary<uint, uint> registers)
    {
        // RSRC2 is the authoritative stage selector: its USER_SGPR field
        // describes the hardware SGPR window even when the shader has zero
        // user-data dwords and therefore no USER_DATA register was written.
        // GFX10 NGG export shaders use the GS user-data bank (RSRC2 at 0x8B),
        // while their program address is carried in the ES/NGG registers.
        // Looking only for a populated USER_DATA range made those shaders
        // fall through to ES (0xCC) and reject every graphics draw because
        // the unrelated ES RSRC2 register at 0xCB was legitimately absent.
        if (HasShaderResource2(registers, GsUserDataRegister))
        {
            return GsUserDataRegister;
        }

        if (HasShaderResource2(registers, EsUserDataRegister))
        {
            return EsUserDataRegister;
        }

        if (HasShaderResource2(registers, VsUserDataRegister))
        {
            return VsUserDataRegister;
        }

        if (HasUserDataRange(registers, GsUserDataRegister))
        {
            return GsUserDataRegister;
        }

        if (HasUserDataRange(registers, EsUserDataRegister))
        {
            return EsUserDataRegister;
        }

        if (HasUserDataRange(registers, VsUserDataRegister))
        {
            return VsUserDataRegister;
        }

        var esValues = CountUserDataValues(registers, EsUserDataRegister);
        var vsValues = CountUserDataValues(registers, VsUserDataRegister);
        return esValues == 0 && vsValues != 0
            ? VsUserDataRegister
            : EsUserDataRegister;
    }

    private static bool HasShaderResource2(
        IReadOnlyDictionary<uint, uint> registers,
        uint userDataBaseRegister) =>
        registers.ContainsKey(userDataBaseRegister - 1);

    private static bool HasUserDataRange(
        IReadOnlyDictionary<uint, uint> registers,
        uint startRegister)
    {
        for (var index = 0u; index < 16; index++)
        {
            if (registers.ContainsKey(startRegister + index))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountUserDataValues(
        IReadOnlyDictionary<uint, uint> registers,
        uint startRegister)
    {
        var count = 0;
        for (var index = 0u; index < 16; index++)
        {
            count += registers.TryGetValue(startRegister + index, out var value) &&
                     value != 0
                ? 1
                : 0;
        }

        return count;
    }

    private static uint GetComputeLocalSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register)
    {
        return registers.TryGetValue(register, out var value)
            ? Math.Max(value & 0xFFFFu, 1u)
            : 1u;
    }

    private static uint GetComputePartialSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register) =>
        registers.TryGetValue(register, out var value)
            ? value >> 16
            : 0u;

    private static int TryApplySoftwareComputeBlits(
        CpuContext ctx,
        ulong shaderAddress,
        IReadOnlyList<(Gen5ImageBinding Binding, TextureDescriptor Texture)> bindings)
    {
        var blits = 0;
        TextureDescriptor? source = null;
        foreach (var (binding, texture) in bindings)
        {
            if (binding.Opcode.StartsWith("ImageStore", StringComparison.Ordinal))
            {
                if (source is { } sourceTexture &&
                    TrySoftwareTextureBlit(ctx, sourceTexture, texture, out var fingerprint))
                {
                    blits++;
                    var key = (shaderAddress, sourceTexture.Address, texture.Address);
                    lock (_softwarePresenterGate)
                    {
                        if (!_softwareComputeBlitFingerprints.TryGetValue(key, out var previous) ||
                            previous != fingerprint)
                        {
                            _softwareComputeBlitFingerprints[key] = fingerprint;
                            TraceAgcShader(
                                $"agc.compute_blit cs=0x{shaderAddress:X16} " +
                                $"src=0x{sourceTexture.Address:X16}:{sourceTexture.Width}x{sourceTexture.Height}:fmt{sourceTexture.Format}/num{sourceTexture.NumberType}/tile{sourceTexture.TileMode} " +
                                $"dst=0x{texture.Address:X16}:{texture.Width}x{texture.Height}:fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode} " +
                                $"fingerprint=0x{fingerprint:X16}");
                        }
                    }
                }
                else if (source is { } cachedSourceTexture &&
                    GuestGpu.Current.TrySubmitGuestImageBlit(
                        cachedSourceTexture.Address,
                        cachedSourceTexture.Width,
                        cachedSourceTexture.Height,
                        cachedSourceTexture.Format,
                        cachedSourceTexture.NumberType,
                        texture.Address,
                        texture.Width,
                        texture.Height,
                        texture.Format,
                        texture.NumberType))
                {
                    blits++;
                    TraceAgcShader(
                        $"agc.compute_gpu_blit cs=0x{shaderAddress:X16} " +
                        $"src=0x{cachedSourceTexture.Address:X16}:{cachedSourceTexture.Width}x{cachedSourceTexture.Height}:fmt{cachedSourceTexture.Format}/num{cachedSourceTexture.NumberType}/tile{cachedSourceTexture.TileMode} " +
                        $"dst=0x{texture.Address:X16}:{texture.Width}x{texture.Height}:fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}");
                }

                continue;
            }

            if (binding.Opcode.StartsWith("Image", StringComparison.Ordinal))
            {
                source = texture;
            }
        }

        return blits;
    }

    private static bool TrySoftwareTextureBlit(
        CpuContext ctx,
        TextureDescriptor source,
        TextureDescriptor destination,
        out ulong fingerprint)
    {
        fingerprint = 0;
        var bytesPerTexel = GetTextureBytesPerTexel(source.Format);
        if (bytesPerTexel == 0 ||
            bytesPerTexel != GetTextureBytesPerTexel(destination.Format) ||
            source.Type != Gen5TextureType2D ||
            destination.Type != Gen5TextureType2D ||
            source.Width == 0 ||
            source.Height == 0 ||
            destination.Width == 0 ||
            destination.Height == 0 ||
            source.Width > 8192 ||
            source.Height > 8192 ||
            destination.Width > 8192 ||
            destination.Height > 8192)
        {
            return false;
        }

        var sourceBytes = checked((ulong)source.Width * source.Height * bytesPerTexel);
        var destinationBytes = checked((ulong)destination.Width * destination.Height * bytesPerTexel);
        if (sourceBytes == 0 ||
            destinationBytes == 0 ||
            sourceBytes > MaxPresentedTextureBytes ||
            destinationBytes > MaxPresentedTextureBytes ||
            sourceBytes > int.MaxValue ||
            destinationBytes > int.MaxValue)
        {
            return false;
        }

        var sourceData = new byte[(int)sourceBytes];
        if (!ctx.Memory.TryRead(source.Address, sourceData))
        {
            return false;
        }

        var nonzero = 0;
        foreach (var value in sourceData)
        {
            if (value != 0)
            {
                nonzero++;
                break;
            }
        }

        if (nonzero == 0)
        {
            return false;
        }

        var destinationData = new byte[(int)destinationBytes];
        for (uint y = 0; y < destination.Height; y++)
        {
            var sourceY = (uint)(((ulong)y * source.Height) / destination.Height);
            for (uint x = 0; x < destination.Width; x++)
            {
                var sourceX = (uint)(((ulong)x * source.Width) / destination.Width);
                var sourceOffset = checked((int)(((ulong)sourceY * source.Width + sourceX) * bytesPerTexel));
                var destinationOffset = checked((int)(((ulong)y * destination.Width + x) * bytesPerTexel));
                sourceData.AsSpan(sourceOffset, (int)bytesPerTexel)
                    .CopyTo(destinationData.AsSpan(destinationOffset, (int)bytesPerTexel));
            }
        }

        if (!ctx.Memory.TryWrite(destination.Address, destinationData))
        {
            return false;
        }

        fingerprint = ComputeFingerprint(destinationData);
        return true;
    }

    private static string ProbeTexture(CpuContext ctx, TextureDescriptor texture)
    {
        if (texture.Width == 0 ||
            texture.Height == 0)
        {
            return "probe=unsupported";
        }

        var totalBytes = GetTextureByteCount(
            texture.Format,
            texture.Width,
            texture.Height,
            GetTextureVolumeDepth(texture.Type, texture.Depth));
        if (totalBytes == 0)
        {
            return "probe=unsupported";
        }

        const int sampleCount = 32;
        const int sampleSize = 256;
        var sample = new byte[sampleSize];
        var reads = 0;
        var nonzero = 0;
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        for (var index = 0; index < sampleCount; index++)
        {
            var maxOffset = totalBytes > sampleSize ? totalBytes - sampleSize : 0;
            var offset = sampleCount == 1
                ? 0
                : maxOffset * (ulong)index / (sampleCount - 1);
            if (!ctx.Memory.TryRead(texture.Address + offset, sample))
            {
                continue;
            }

            reads++;
            foreach (var value in sample)
            {
                if (value != 0)
                {
                    nonzero++;
                }

                hash = (hash ^ value) * prime;
            }
        }

        var bytesPerTexel = GetTextureBytesPerTexel(texture.Format);
        var texels = bytesPerTexel is > 0 and <= 16
            ? string.Join(
                '/',
                ProbeTextureTexel(ctx, texture.Address, (int)bytesPerTexel),
                ProbeTextureTexel(
                    ctx,
                    texture.Address +
                    (((ulong)(texture.Height / 2) * texture.Width) + (texture.Width / 2)) *
                    bytesPerTexel,
                    (int)bytesPerTexel),
                ProbeTextureTexel(
                    ctx,
                    texture.Address + totalBytes - bytesPerTexel,
                    (int)bytesPerTexel))
            : "unsupported";
        return $"probe={reads}/{sampleCount}:{nonzero}:0x{hash:X16}:texels={texels}";
    }

    private static string ProbeTextureTexel(CpuContext ctx, ulong address, int size)
    {
        var texel = new byte[size];
        return ctx.Memory.TryRead(address, texel)
            ? Convert.ToHexString(texel)
            : "unreadable";
    }

    private static ulong GetTextureBytesPerTexel(uint format) =>
        format switch
        {
            1 => 1UL,
            2 => 2UL,
            3 => 2UL,
            4 => 4UL,
            5 => 4UL,
            6 => 4UL,
            7 => 4UL,
            9 => 4UL,
            10 => 4UL,
            11 => 8UL,
            12 => 8UL,
            13 => 12UL,
            14 => 16UL,
            _ => 0UL,
        };

    internal static ulong GetTextureByteCount(
        uint format,
        uint width,
        uint height,
        uint depth = 1)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel != 0)
        {
            return checked(
                (ulong)width *
                height *
                Math.Max(depth, 1u) *
                bytesPerTexel);
        }

        var blockBytes = (ulong)GetBlockCompressedBlockBytes(format);
        return blockBytes == 0
            ? 0
            : checked(
                ((ulong)width + 3) / 4 *
                (((ulong)height + 3) / 4) *
                Math.Max(depth, 1u) *
                blockBytes);
    }

    internal static uint GetTextureVolumeDepth(uint type, uint depth) =>
        type == Gen5TextureType3D
            ? Math.Max(depth, 1u)
            : 1u;

    private static uint GetLinearTexturePitch(uint pitch, uint height, uint format)
    {
        var bytesPerTexel = GetTextureBytesPerTexel(format);
        if (bytesPerTexel == 0 || height == 0)
        {
            return pitch;
        }

        // GNM linear surfaces align the row pitch to 256 bytes, so a 32px
        // RGBA8 texture is stored with a 64px (256-byte) pitch and a 288px
        // one with 320px. Reading at the unpadded width made every padded
        // tail land on the next row, which showed as transparent gaps every
        // other row on small tiles and diagonal dashes on wider surfaces.
        var pitchBytes = AlignUp((ulong)pitch * bytesPerTexel, 256UL);
        return checked((uint)(pitchBytes / bytesPerTexel));
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        (value + alignment - 1) & ~(alignment - 1);

    private static void TraceShaderTranslationMiss(
        CpuContext ctx,
        SubmittedDcbState state,
        uint vertexCount,
        bool hasExportShader,
        ulong exportShaderAddress,
        bool hasPixelShader,
        ulong pixelShaderAddress,
        bool hasPsInputEna,
        uint psInputEna,
        bool hasPsInputAddr,
        uint psInputAddr,
        string? translationError = null)
    {
        var firstFailure = false;
        if (!string.IsNullOrEmpty(translationError))
        {
            lock (_submitTraceGate)
            {
                firstFailure = _tracedShaderFailures.Add(
                    (pixelShaderAddress, translationError));
            }
        }

        if (!firstFailure &&
            !ShouldTraceHotPath(ref _shaderTranslationMissTraceCount))
        {
            return;
        }

        // Translation failures are compatibility issues, not merely verbose
        // shader diagnostics. Report each distinct failure once even when AGC
        // tracing is disabled so normal runs preserve the missing opcode or
        // unsupported translation reason needed to fix the game.
        if (firstFailure)
        {
            Console.Error.WriteLine(
                $"[COMPAT][SHADER] ps=0x{pixelShaderAddress:X16} " +
                $"es=0x{exportShaderAddress:X16} error={translationError}");
        }

        if ((!hasPixelShader || !hasPsInputEna || !hasPsInputAddr) &&
            TryMarkMissingPixelShaderBindingsTrace())
        {
            TraceAgcShader(
                $"agc.shader_register_candidates " +
                DescribeShaderRegisterCandidates(ctx, state.ShRegisters));
        }

        if (!hasPixelShader)
        {
            state.CxRegisters.TryGetValue(DbDepthControl, out var rawDepthControl);
            state.CxRegisters.TryGetValue(DbZInfo, out var rawZInfo);
            state.CxRegisters.TryGetValue(DbDepthSizeXy, out var rawDepthSize);
            state.CxRegisters.TryGetValue(DbDepthView, out var rawDepthView);
            var depthState = DecodeDepthState(state.CxRegisters);
            var depthTarget = DecodeDepthTarget(state.CxRegisters);
            TraceAgcShader(
                $"agc.shader_depth_state control=0x{rawDepthControl:X8} " +
                $"zinfo=0x{rawZInfo:X8} size=0x{rawDepthSize:X8} " +
                $"view=0x{rawDepthView:X8} " +
                $"test={(depthState.TestEnable ? 1 : 0)} " +
                $"write={(depthState.WriteEnable ? 1 : 0)} " +
                $"func={depthState.CompareOp} " +
                (depthTarget is null
                    ? "target=none"
                    : $"target=0x{depthTarget.Address:X16}:" +
                      $"{depthTarget.Width}x{depthTarget.Height}:" +
                      $"fmt{depthTarget.GuestFormat}/sw{depthTarget.SwizzleMode}:" +
                      $"ro={(depthTarget.ReadOnly ? 1 : 0)}"));
        }

        var shaderDecode = string.Empty;
        if (hasExportShader && hasPixelShader)
        {
            var shouldDescribe = false;
            ulong exportShaderHeader;
            ulong pixelShaderHeader;
            lock (_submitTraceGate)
            {
                shouldDescribe = _tracedShaderDecodePairs.Add((exportShaderAddress, pixelShaderAddress));
                _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
                _shaderHeadersByCode.TryGetValue(pixelShaderAddress, out pixelShaderHeader);
            }

            if (shouldDescribe)
            {
                shaderDecode = $" decode={Gen5ShaderTranslator.Describe(ctx, exportShaderAddress, pixelShaderAddress)}";
                TraceAgcShader(
                    $"agc.shader_words es=0x{exportShaderAddress:X16} " +
                    Gen5ShaderTranslator.DescribeWords(ctx, exportShaderAddress));
                if (Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        exportShaderAddress,
                        exportShaderHeader,
                        state.ShRegisters,
                        SelectExportUserDataRegister(state.ShRegisters),
                        out var exportState,
                        out _,
                        userDataScalarRegisterBase: NggUserDataScalarRegisterBase) &&
                    Gen5ShaderTranslator.TryCreateState(
                        ctx,
                        pixelShaderAddress,
                        pixelShaderHeader,
                        state.ShRegisters,
                        PsTextureUserDataRegister,
                        out var pixelState,
                        out _))
                {
                    TraceAgcShader(
                        $"agc.shader_state es=0x{exportShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(exportState));
                    TraceAgcShader(
                        $"agc.shader_state ps=0x{pixelShaderAddress:X16} " +
                        Gen5ShaderTranslator.DescribeState(pixelState));
                    if (Gen5ShaderScalarEvaluator.TryEvaluate(
                            ctx,
                            pixelState,
                            out var evaluation,
                            out var bindingError))
                    {
                        foreach (var binding in evaluation.ImageBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_binding ps=0x{pixelShaderAddress:X16} " +
                                $"pc=0x{binding.Pc:X} op={binding.Opcode} " +
                                $"resource={FormatShaderDwords(binding.ResourceDescriptor)} " +
                                $"sampler={FormatShaderDwords(binding.SamplerDescriptor)}");
                        }

                        foreach (var binding in evaluation.GlobalMemoryBindings)
                        {
                            TraceAgcShader(
                                $"agc.shader_global_binding ps=0x{pixelShaderAddress:X16} " +
                                $"saddr=s{binding.ScalarAddress} " +
                                $"base=0x{binding.BaseAddress:X16} bytes={binding.DataLength} " +
                                $"pcs={string.Join(',', binding.InstructionPcs.Select(pc => $"0x{pc:X}"))}");
                        }

                        if (GuestGpu.Current.TryCompilePixelShader(
                                 pixelState,
                                 evaluation,
                                 [new(0, 0, Gen5PixelOutputKind.Float)],
                                 out var compiledPixel,
                                 out var compileError,
                                 pixelInputEnable: psInputEna,
                                 pixelInputAddress: psInputAddr,
                                 pixelInputCntl: ReadPsInputCntlRegisters(state.CxRegisters),
                                 storageBufferOffsetAlignment:
                                     _storageBufferOffsetAlignment))
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv ps=0x{pixelShaderAddress:X16} " +
                                $"bytes={compiledPixel!.Payload.Length} bindings={evaluation.ImageBindings.Count} " +
                                $"global_buffers={evaluation.GlobalMemoryBindings.Count}");
                        }
                        else
                        {
                            TraceAgcShader(
                                $"agc.shader_spirv_error ps=0x{pixelShaderAddress:X16} " +
                                compileError.ReplaceLineEndings(" "));
                        }
                    }
                    else
                    {
                        TraceAgcShader(
                            $"agc.shader_binding_error ps=0x{pixelShaderAddress:X16} " +
                            bindingError);
                    }
                }
            }
        }

        TraceAgcShader(
            $"agc.shader_translate_miss vertices={vertexCount} " +
            $"es={(hasExportShader ? $"0x{exportShaderAddress:X16}" : "missing")} " +
            $"ps={(hasPixelShader ? $"0x{pixelShaderAddress:X16}" : "missing")} " +
            $"ps_ena={(hasPsInputEna ? $"0x{psInputEna:X8}" : "missing")} " +
            $"ps_addr={(hasPsInputAddr ? $"0x{psInputAddr:X8}" : "missing")}" +
            (string.IsNullOrEmpty(translationError) ? string.Empty : $" error={translationError}") +
            shaderDecode);
    }

    private static bool TryMarkMissingPixelShaderBindingsTrace()
    {
        lock (_submitTraceGate)
        {
            if (_tracedMissingPixelShaderBindings)
            {
                return false;
            }

            _tracedMissingPixelShaderBindings = true;
            return true;
        }
    }

    private static string DescribeShaderRegisterCandidates(
        CpuContext ctx,
        IReadOnlyDictionary<uint, uint> registers)
    {
        var candidates = new List<(uint Register, ulong Address, ulong Header)>();
        lock (_submitTraceGate)
        {
            foreach (var (register, lo) in registers)
            {
                if (!registers.TryGetValue(register + 1, out var hi))
                {
                    continue;
                }

                var address = ((ulong)hi << 40) | ((ulong)lo << 8);
                if (address != 0 &&
                    _shaderHeadersByCode.TryGetValue(address, out var header))
                {
                    candidates.Add((register, address, header));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ',',
            candidates
                .OrderBy(candidate => candidate.Register)
                .Take(16)
                .Select(candidate =>
                {
                    var type = TryReadByte(
                        ctx,
                        candidate.Header + ShaderTypeOffset,
                        out var shaderType)
                        ? shaderType.ToString()
                        : "?";
                    return
                        $"sh[0x{candidate.Register:X}/0x{candidate.Register + 1:X}]=" +
                        $"0x{candidate.Address:X16}:type{type}";
                }));
    }

    private static bool TryGetShaderAddress(
        IReadOnlyDictionary<uint, uint> registers,
        uint loRegister,
        uint hiRegister,
        out ulong address)
    {
        address = 0;
        if (!registers.TryGetValue(loRegister, out var lo) ||
            !registers.TryGetValue(hiRegister, out var hi))
        {
            return false;
        }

        address = ((ulong)hi << 40) | ((ulong)lo << 8);
        return address != 0;
    }

    private static bool TryReadTextureDescriptor(
        CpuContext ctx,
        ulong packetAddress,
        uint packetLength,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (packetLength < 10 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister))
        {
            return false;
        }

        var valueCount = packetLength - 2;
        if (startRegister > PsTextureUserDataRegister ||
            startRegister + valueCount < PsTextureUserDataRegister + 8)
        {
            return false;
        }

        var descriptorAddress =
            packetAddress +
            8 +
            ((ulong)(PsTextureUserDataRegister - startRegister) * sizeof(uint));
        Span<uint> fields = stackalloc uint[8];
        for (var i = 0; i < fields.Length; i++)
        {
            if (!TryReadUInt32(ctx, descriptorAddress + ((ulong)i * sizeof(uint)), out fields[i]))
            {
                return false;
            }
        }

        return TryDecodeTextureDescriptor(fields.ToArray(), out descriptor);
    }

    private static bool TryDecodeTextureDescriptor(
        IReadOnlyList<uint> fields,
        out TextureDescriptor descriptor)
    {
        descriptor = default;
        if (fields.Count < 4)
        {
            return false;
        }

        // RDNA2 ISA table 45: BASE_ADDRESS is addr[47:8], WIDTH is the full
        // 16-bit field split across word1/word2, and HEIGHT is word2[29:14].
        // Keeping the high base byte is required for legal guest VAs above
        // 1 TiB; it is not descriptor metadata.
        var address = (((ulong)(fields[1] & 0xFFu) << 32) | fields[0]) << 8;
        var width = (((fields[1] >> 30) & 0x3u) | ((fields[2] & 0x3FFFu) << 2)) + 1;
        var height = ((fields[2] >> 14) & 0xFFFFu) + 1;
        var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
        if (unifiedFormat == 0 ||
            !Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var format,
                out var numberType))
        {
            return false;
        }
        var tileMode = (fields[3] >> 20) & 0x1Fu;
        var type = (fields[3] >> 28) & 0xFu;
        var baseLevel = (fields[3] >> 12) & 0xFu;
        var lastLevel = (fields[3] >> 16) & 0xFu;
        var bcSwizzle = (fields[3] >> 25) & 0x7u;
        var hasExtendedDescriptor = fields.Count >= 8;
        var word4 = fields.Count >= 5 ? fields[4] : 0u;
        var depthOrLastSlice = (word4 & 0x1FFFu) + 1;
        var baseArray = (word4 >> 16) & 0x1FFFu;
        // In a 256-bit 1D/2D/2D-MSAA descriptor word4[13:0] is
        // (pitch-1). A zeroed upper half denotes the common 128-bit resource,
        // where pitch is implicit; use width rather than inventing pitch=1.
        var pitch = type is 8u or 9u or 14u && word4 != 0
            ? (word4 & 0x3FFFu) + 1
            : width;
        var depth = type is 10u or 11u or 12u or 13u or 15u
            ? depthOrLastSlice
            : 1u;
        var word5 = fields.Count >= 6 ? fields[5] : 0u;
        var arrayPitch = word5 & 0xFu;
        var maxMip = (word5 >> 4) & 0xFu;
        var minLod = (fields[1] >> 8) & 0xFFFu;
        var minLodWarn = (word5 >> 8) & 0xFFFu;
        var word6 = fields.Count >= 7 ? fields[6] : 0u;
        var word7 = fields.Count >= 8 ? fields[7] : 0u;
        var metadataAddress = ((((ulong)word7 << 8) | (word6 >> 24)) << 8);
        var descriptorFlags = word6 & 0x00FF_FFFFu;
        var dstSelect = fields[3] & 0xFFFu;
        if (address == 0 || width == 0 || height == 0 || type is >= 1 and <= 7)
        {
            return false;
        }

        descriptor = new TextureDescriptor(
            address,
            width,
            height,
            format,
            numberType,
            tileMode,
            type,
            baseLevel,
            lastLevel,
            pitch,
            dstSelect,
            depth,
            baseArray,
            arrayPitch,
            maxMip,
            minLod,
            minLodWarn,
            bcSwizzle,
            metadataAddress,
            descriptorFlags,
            hasExtendedDescriptor);
        return true;
    }

    private static TextureDescriptor CreateFallbackTextureDescriptor(
        IReadOnlyList<uint> fields,
        uint instructionDimension)
    {
        var format = Gen5TextureFormatR8G8B8A8Unorm;
        var numberType = 0u;
        var tileMode = 0u;
        if (fields.Count >= 4)
        {
            var unifiedFormat = (fields[1] >> 20) & 0x1FFu;
            if (!Gfx10UnifiedFormat.TryDecode(
                    unifiedFormat,
                    out format,
                    out numberType))
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
                numberType = 0;
            }
            tileMode = (fields[3] >> 20) & 0x1Fu;
            if (format == 0)
            {
                format = Gen5TextureFormatR8G8B8A8Unorm;
            }
        }

        return new TextureDescriptor(
            Address: 0,
            Width: 1,
            Height: 1,
            Format: format,
            NumberType: numberType,
            TileMode: tileMode,
            Type: GetFallbackTextureType(instructionDimension),
            BaseLevel: 0,
            LastLevel: 0,
            Pitch: 1,
            DstSelect: 0xFAC);
    }

    internal static uint GetFallbackTextureType(uint instructionDimension) =>
        instructionDimension switch
        {
            0 => Gen5TextureType1D,
            2 => Gen5TextureType3D,
            3 => Gen5TextureTypeCube,
            4 => Gen5TextureType1DArray,
            5 or 7 => Gen5TextureType2DArray,
            _ => Gen5TextureType2D,
        };

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

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        const ulong fnvOffsetBasis = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var fingerprint = fnvOffsetBasis;
        foreach (var value in bytes)
        {
            fingerprint = (fingerprint ^ value) * fnvPrime;
        }

        return fingerprint;
    }

    private static void TraceSubmittedPacket(
        CpuContext ctx,
        ulong packetAddress,
        uint dwordOffset,
        uint header,
        uint length,
        uint op,
        uint register)
    {
        TraceAgc(
            $"agc.dcb.packet dw={dwordOffset} addr=0x{packetAddress:X16} header=0x{header:X8} len={length} op=0x{op:X2} reg=0x{register:X2}");

        var payloadCount = Math.Min(length - 1, 32u);
        for (uint i = 0; i < payloadCount; i++)
        {
            if (!TryReadUInt32(ctx, packetAddress + ((ulong)(i + 1) * sizeof(uint)), out var value))
            {
                return;
            }

            TraceAgc($"agc.dcb.payload dw={dwordOffset + i + 1} value=0x{value:X8}");
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            length < 4 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var registerSpace = register == RCxRegsIndirect ? "cx" : register == RShRegsIndirect ? "sh" : "uc";
        var tracedCount = Math.Min(registerCount, 256u);
        TraceAgc($"agc.dcb.indirect space={registerSpace} regs=0x{registersAddress:X16} count={registerCount}");
        for (uint i = 0; i < tracedCount; i++)
        {
            var entryAddress = registersAddress + ((ulong)i * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + 4, out var value))
            {
                TraceAgc($"agc.dcb.indirect_read_failed space={registerSpace} index={i} addr=0x{entryAddress:X16}");
                return;
            }

            TraceAgc($"agc.dcb.reg space={registerSpace} index={i} offset=0x{registerOffset:X4} value=0x{value:X8}");
        }

        if (tracedCount != registerCount)
        {
            TraceAgc($"agc.dcb.indirect_truncated space={registerSpace} traced={tracedCount} total={registerCount}");
        }
    }

    private static bool PatchShaderProgramRegisters(CpuContext ctx, ulong headerAddress, ulong codeAddress)
    {
        if (!TryReadUInt64(ctx, headerAddress + ShaderShRegistersOffset, out var shRegistersAddress) ||
            !TryReadByte(ctx, headerAddress + ShaderTypeOffset, out var shaderType) ||
            !TryReadByte(ctx, headerAddress + ShaderNumShRegistersOffset, out var registerCount))
        {
            return false;
        }

        if (shRegistersAddress == 0 || registerCount < 2)
        {
            TraceCreateShader(
                0,
                headerAddress,
                codeAddress,
                $"skip-pgm-patch type={shaderType} shRegs=0x{shRegistersAddress:X16} regCount={registerCount}");
            return true;
        }

        // Type bytes follow the Prospero half/fused enum used by fuse-shader
        // (#326). Type 3 still patches VS PGM registers on this tree (pre-fuse
        // CreateShader behavior); type 5 is the HS front half / hull path.
        var expectedLo = shaderType switch
        {
            ComputeShaderType => ComputePgmLo,
            PsShaderType => SpiShaderPgmLoPs,
            GsShaderType or GsBackShaderType => SpiShaderPgmLoEs,
            HsShaderType => SpiShaderPgmLoVs,
            GsFrontShaderType => SpiShaderPgmLoGs,
            HsFrontShaderType => SpiShaderPgmLoHs,
            HsBackShaderType => SpiShaderPgmLoLs,
            _ => 0u,
        };
        var expectedHi = shaderType switch
        {
            ComputeShaderType => ComputePgmHi,
            PsShaderType => SpiShaderPgmHiPs,
            GsShaderType or GsBackShaderType => SpiShaderPgmHiEs,
            HsShaderType => SpiShaderPgmHiVs,
            GsFrontShaderType => SpiShaderPgmHiGs,
            HsFrontShaderType => SpiShaderPgmHiHs,
            HsBackShaderType => SpiShaderPgmHiLs,
            _ => 0u,
        };

        // Some shader headers (e.g. GTA V Enhanced hull shaders, GS front shaders,
        // or UE4 mesh/task/vertex shaders where type=8 or user data registers are placed first)
        // omit PGM_LO/HI from the default SH table. Still succeed: the code VA
        // lives at ShaderCodeOffset and later binder paths republish it.
        if (!TryFindShaderProgramRegisterPair(
                ctx,
                shRegistersAddress,
                registerCount,
                expectedLo,
                expectedHi,
                out var loEntryAddress,
                out var hiEntryAddress,
                out var foundLo,
                out var foundHi))
        {
            TryReadUInt32(ctx, shRegistersAddress, out var firstLo);
            TraceCreateShader(
                0,
                headerAddress,
                codeAddress,
                $"skip-pgm-patch type={shaderType} expected_lo=0x{expectedLo:X8} first_lo=0x{firstLo:X8}");
            return true;
        }

        var loValue = (uint)((codeAddress >> 8) & 0xFFFF_FFFFUL);
        var hiValue = (uint)((codeAddress >> 40) & 0xFFUL);
        if (!TryWriteUInt32(ctx, loEntryAddress + sizeof(uint), loValue) ||
            !TryWriteUInt32(ctx, hiEntryAddress + sizeof(uint), hiValue))
        {
            return false;
        }

        if (foundLo != expectedLo || foundHi != expectedHi)
        {
            TraceCreateShader(
                0,
                headerAddress,
                codeAddress,
                $"patched-alt-registers type={shaderType} lo=0x{foundLo:X8} hi=0x{foundHi:X8}");
        }

        return true;
    }

    private static readonly (uint Lo, uint Hi)[] ShaderProgramRegisterPairs =
    [
        (ComputePgmLo, ComputePgmHi),
        (SpiShaderPgmLoPs, SpiShaderPgmHiPs),
        (SpiShaderPgmLoVs, SpiShaderPgmHiVs),
        (SpiShaderPgmLoEs, SpiShaderPgmHiEs),
        (SpiShaderPgmLoGs, SpiShaderPgmHiGs),
        (SpiShaderPgmLoHs, SpiShaderPgmHiHs),
        (SpiShaderPgmLoLs, SpiShaderPgmHiLs),
    ];

    private static bool TryFindShaderProgramRegisterPair(
        CpuContext ctx,
        ulong shRegistersAddress,
        byte registerCount,
        uint preferredLo,
        uint preferredHi,
        out ulong loEntryAddress,
        out ulong hiEntryAddress,
        out uint foundLo,
        out uint foundHi)
    {
        loEntryAddress = 0;
        hiEntryAddress = 0;
        foundLo = 0;
        foundHi = 0;

        ulong preferredLoAddress = 0;
        ulong preferredHiAddress = 0;
        ulong fallbackLoAddress = 0;
        ulong fallbackHiAddress = 0;
        uint fallbackLo = 0;
        uint fallbackHi = 0;

        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = shRegistersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var offset))
            {
                return false;
            }

            if (preferredLo != 0 && offset == preferredLo)
            {
                preferredLoAddress = entryAddress;
            }
            else if (preferredHi != 0 && offset == preferredHi)
            {
                preferredHiAddress = entryAddress;
            }

            if (fallbackLoAddress != 0)
            {
                continue;
            }

            foreach (var pair in ShaderProgramRegisterPairs)
            {
                if (offset != pair.Lo)
                {
                    continue;
                }

                // Prefer a contiguous LO/HI pair when present.
                if (index + 1 < registerCount &&
                    TryReadUInt32(ctx, entryAddress + 8, out var nextOffset) &&
                    nextOffset == pair.Hi)
                {
                    fallbackLoAddress = entryAddress;
                    fallbackHiAddress = entryAddress + 8;
                    fallbackLo = pair.Lo;
                    fallbackHi = pair.Hi;
                    break;
                }

                for (uint hiIndex = 0; hiIndex < registerCount; hiIndex++)
                {
                    if (hiIndex == index)
                    {
                        continue;
                    }

                    var hiAddress = shRegistersAddress + ((ulong)hiIndex * 8);
                    if (!TryReadUInt32(ctx, hiAddress, out var hiOffset) || hiOffset != pair.Hi)
                    {
                        continue;
                    }

                    fallbackLoAddress = entryAddress;
                    fallbackHiAddress = hiAddress;
                    fallbackLo = pair.Lo;
                    fallbackHi = pair.Hi;
                    break;
                }

                break;
            }
        }

        if (preferredLoAddress != 0 && preferredHiAddress != 0)
        {
            loEntryAddress = preferredLoAddress;
            hiEntryAddress = preferredHiAddress;
            foundLo = preferredLo;
            foundHi = preferredHi;
            return true;
        }

        if (fallbackLoAddress != 0 && fallbackHiAddress != 0)
        {
            loEntryAddress = fallbackLoAddress;
            hiEntryAddress = fallbackHiAddress;
            foundLo = fallbackLo;
            foundHi = fallbackHi;
            return true;
        }

        return false;
    }

    private static bool IsEsGeometryShaderType(byte shaderType) =>
        shaderType is GsShaderType or GsBackShaderType;

    private static int SetIndirectPatchAddress(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        if (commandAddress == 0 || registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_addr cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SetIndirectPatchRegisterCount(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 4, registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_count cmd=0x{commandAddress:X16} count={registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PatchWriteDataControlByte(CpuContext ctx, int byteIndex)
    {
        if (!TryResolveWriteDataPatchArguments(
                ctx,
                ctx[CpuRegister.Rdi],
                ctx[CpuRegister.Rsi],
                out var commandAddress,
                out var value) ||
            !TryReadUInt32(ctx, commandAddress + 4, out var control))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var shift = byteIndex * 8;
        var patchedControl = (control & ~(0xFFu << shift)) | (((uint)value & 0xFFu) << shift);
        return TryWriteUInt32(ctx, commandAddress + 4, patchedControl)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryResolveWriteDataPatchArguments(
        CpuContext ctx,
        ulong first,
        ulong second,
        out ulong commandAddress,
        out ulong value)
    {
        if (IsWriteDataPacket(ctx, first))
        {
            commandAddress = first;
            value = second;
            return true;
        }

        if (IsWriteDataPacket(ctx, second))
        {
            commandAddress = second;
            value = first;
            return true;
        }

        commandAddress = 0;
        value = 0;
        return false;
    }

    private static bool IsWriteDataPacket(CpuContext ctx, ulong commandAddress)
    {
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return false;
        }

        return op == ItWriteData || (op == ItNop && register == RWriteData);
    }

    private static int AddIndirectPatchRegisters(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, commandAddress + 4, out var currentCount) ||
            !TryWriteUInt32(ctx, commandAddress + 4, currentCount + registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_add cmd=0x{commandAddress:X16} add={registerCount} total={currentCount + registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int DcbSetRegistersIndirect(CpuContext ctx, uint packetRegister, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_{registerSpace}_indirect buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16} count={registerCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static int DcbSetRegisterDirect(CpuContext ctx, uint op, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        // Uc/Cx/Sh register is passed by value as {u32 offset, u32 value} in RSI.
        var packedRegister = ctx[CpuRegister.Rsi];
        var registerOffset = (uint)(packedRegister & 0xFFFF_FFFFUL);
        var registerValue = (uint)(packedRegister >> 32);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        const uint packetDwords = 3;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, op, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerOffset & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 8, registerValue))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_set_{registerSpace}_direct buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{registerOffset:X4} value=0x{registerValue:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static bool TryAllocateCommandDwords(CpuContext ctx, ulong commandBufferAddress, uint sizeDwords, out ulong commandAddress)
    {
        commandAddress = 0;
        if (sizeDwords == 0 ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out var cursorUp) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out var cursorDown) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCallbackOffset, out var callback) ||
            !TryReadUInt64(ctx, commandBufferAddress + CommandBufferUserDataOffset, out var userData) ||
            !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out var reservedDwords))
        {
            return false;
        }

        var remainingDwords = GetRemainingCommandDwords(cursorUp, cursorDown, reservedDwords);
        if (sizeDwords > remainingDwords)
        {
            // The one place that knows an arena's true final cursor before a
            // switch happens, regardless of which builder export wrote its
            // last bytes — fires only on genuine exhaustion, not per packet.
            if (_forceSubmitOrphanPreamblesEnabled &&
                TryReadUInt64(ctx, commandBufferAddress, out var exhaustedBase) &&
                exhaustedBase != 0)
            {
                lock (_orphanPreambleGate)
                {
                    if ((!_builderArenaLastSeen.TryGetValue(commandBufferAddress, out var seen) ||
                        seen.Base != exhaustedBase ||
                        cursorUp > seen.Cursor))
                    {
                        _builderArenaLastSeen[commandBufferAddress] =
                            (exhaustedBase, cursorUp, GuestThreadExecution.CurrentGuestThreadHandle, System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                }
            }

            TraceAgc($"agc.cmd_alloc_full buf=0x{commandBufferAddress:X16} need={sizeDwords} remaining={remainingDwords} callback=0x{callback:X16}");
            var scheduler = GuestThreadExecution.Scheduler;
            ulong callbackResult = 0;
            string? callbackError = null;
            if (callback == 0 ||
                scheduler is null ||
                !scheduler.TryCallGuestFunction(
                    ctx,
                    callback,
                    commandBufferAddress,
                    (ulong)sizeDwords + reservedDwords,
                    userData,
                    0,
                    0,
                    "agc_command_buffer_full",
                    out callbackResult,
                    out callbackError))
            {
                TraceAgc(
                    $"agc.cmd_alloc_callback_failed buf=0x{commandBufferAddress:X16} " +
                    $"callback=0x{callback:X16} result=0x{callbackResult:X16} " +
                    $"error={callbackError ?? "none"}");
                return false;
            }

            TraceAgc(
                $"agc.cmd_alloc_callback_complete buf=0x{commandBufferAddress:X16} " +
                $"callback=0x{callback:X16} result=0x{callbackResult:X16}");

            if (!TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorUpOffset, out cursorUp) ||
                !TryReadUInt64(ctx, commandBufferAddress + CommandBufferCursorDownOffset, out cursorDown) ||
                !TryReadUInt32(ctx, commandBufferAddress + CommandBufferReservedDwOffset, out reservedDwords) ||
                sizeDwords > GetRemainingCommandDwords(cursorUp, cursorDown, reservedDwords))
            {
                TraceAgc($"agc.cmd_alloc_callback_no_space buf=0x{commandBufferAddress:X16} need={sizeDwords}");
                return false;
            }
        }

        var nextCursor = cursorUp + ((ulong)sizeDwords * sizeof(uint));
        if (!ctx.TryWriteUInt64(commandBufferAddress + CommandBufferCursorUpOffset, nextCursor))
        {
            return false;
        }

        commandAddress = cursorUp;
        return true;
    }

    private static uint GetRemainingCommandDwords(
        ulong cursorUp,
        ulong cursorDown,
        uint reservedDwords)
    {
        var availableDwords = cursorDown >= cursorUp
            ? Math.Min((cursorDown - cursorUp) / sizeof(uint), uint.MaxValue)
            : 0;
        return availableDwords > reservedDwords
            ? (uint)availableDwords - reservedDwords
            : 0;
    }

    private static bool CopyShaderRegister(CpuContext ctx, ulong sourceAddress, ulong destinationAddress)
    {
        if (!TryReadUInt32(ctx, sourceAddress, out var offset) ||
            !TryReadUInt32(ctx, sourceAddress + sizeof(uint), out var value))
        {
            return false;
        }

        return TryWriteUInt32(ctx, destinationAddress, offset) &&
               TryWriteUInt32(ctx, destinationAddress + sizeof(uint), value);
    }

    private static bool IsFusedShaderHalfPair(byte frontType, byte backType) =>
        (frontType == GsFrontShaderType && backType == GsBackShaderType) ||
        (frontType == HsFrontShaderType && backType == HsBackShaderType);

    private static bool TryFindShaderRegister(
        CpuContext ctx,
        ulong registersAddress,
        int registerCount,
        uint registerOffset,
        int occurrence,
        out ulong entryAddress)
    {
        if (registersAddress != 0)
        {
            for (var index = 0; index < registerCount; index++)
            {
                var address = registersAddress + (ulong)index * 8;
                if (!TryReadUInt32(ctx, address, out var current) || current != registerOffset)
                {
                    continue;
                }

                if (occurrence == 0)
                {
                    entryAddress = address;
                    return true;
                }

                occurrence--;
            }
        }

        entryAddress = 0;
        return false;
    }

    // A missing or unpaired lo/hi register is not an error: the retail library
    // leaves absent registers untouched, unlike the create-time patch which
    // requires them.
    private static bool PatchFusedProgramAddress(
        CpuContext ctx,
        ulong registersAddress,
        int registerCount,
        uint loRegisterOffset,
        ulong codeAddress)
    {
        if (!TryFindShaderRegister(ctx, registersAddress, registerCount, loRegisterOffset, 0, out var loEntry))
        {
            TraceAgc($"agc.fuse_shader_halves.pgm_absent lo=0x{loRegisterOffset:X} regs=0x{registersAddress:X16}");
            return true;
        }

        var hiEntry = loEntry + 8;
        if (hiEntry >= registersAddress + (ulong)registerCount * 8 ||
            !TryReadUInt32(ctx, hiEntry, out var hiOffset) ||
            hiOffset != loRegisterOffset + 1)
        {
            TraceAgc($"agc.fuse_shader_halves.pgm_unpaired lo=0x{loRegisterOffset:X} regs=0x{registersAddress:X16}");
            return true;
        }

        if (!TryReadUInt32(ctx, hiEntry + sizeof(uint), out var hiValue))
        {
            return false;
        }

        return TryWriteUInt32(ctx, loEntry + sizeof(uint), (uint)(codeAddress >> 8)) &&
               TryWriteUInt32(ctx, hiEntry + sizeof(uint), (hiValue & 0xFFFF_FF00u) | (uint)((codeAddress >> 40) & 0xFFUL));
    }

    private static bool TryWriteByte(CpuContext ctx, ulong address, byte value)
    {
        Span<byte> buffer = [value];
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool RelocatePointerField(CpuContext ctx, ulong fieldAddress)
    {
        if (!TryReadUInt64(ctx, fieldAddress, out var relativeAddress))
        {
            return false;
        }

        if (relativeAddress == 0)
        {
            return true;
        }

        return ctx.TryWriteUInt64(fieldAddress, fieldAddress + relativeAddress);
    }

    private static int ReturnRegisterDefaults(CpuContext ctx, bool internalDefaults)
    {
        var version = (uint)ctx[CpuRegister.Rdi];
        if (!IsSupportedRegisterDefaultsVersion(version))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryGetRegisterDefaultsAllocation(ctx, out var allocation))
        {
            return ReturnPointer(ctx, 0);
        }

        var address = internalDefaults ? allocation.Internal : allocation.Primary;
        TraceAgc($"agc.get_register_defaults internal={internalDefaults} version={version} address=0x{address:X16}");
        return ReturnPointer(ctx, address);
    }

    private static bool IsSupportedRegisterDefaultsVersion(uint version)
    {
        return version is
            RegisterDefaultsVersion7 or
            RegisterDefaultsVersion8 or
            RegisterDefaultsVersion10 or
            RegisterDefaultsVersion13;
    }

    private static bool TryGetRegisterDefaultsAllocation(
        CpuContext ctx,
        out RegisterDefaultsAllocation allocation)
    {
        lock (_registerDefaultsGate)
        {
            if (_registerDefaultsAllocations.TryGetValue(ctx.Memory, out allocation!))
            {
                return true;
            }

            if (!TryBuildRegisterDefaults(
                    ctx,
                    PrimaryRegisterDefaults,
                    cxTableLength: 78,
                    shTableLength: 29,
                    ucTableLength: 20,
                    out var primaryAddress) ||
                !TryBuildRegisterDefaults(
                    ctx,
                    InternalRegisterDefaults,
                    cxTableLength: 4,
                    shTableLength: 15,
                    ucTableLength: 3,
                    out var internalAddress))
            {
                allocation = null!;
                return false;
            }

            allocation = new RegisterDefaultsAllocation(primaryAddress, internalAddress);
            _registerDefaultsAllocations.Add(ctx.Memory, allocation);
            return true;
        }
    }

    private static bool TryBuildRegisterDefaults(
        CpuContext ctx,
        RegisterDefaultGroup[] groups,
        int cxTableLength,
        int shTableLength,
        int ucTableLength,
        out ulong address)
    {
        var cxTableOffset = AlignUp(RegisterDefaultsSize, sizeof(ulong));
        var shTableOffset = cxTableOffset + (cxTableLength * sizeof(ulong));
        var ucTableOffset = shTableOffset + (shTableLength * sizeof(ulong));
        var typesOffset = AlignUp(ucTableOffset + (ucTableLength * sizeof(ulong)), sizeof(uint));
        var registerBlocksOffset = AlignUp(typesOffset + (groups.Length * 3 * sizeof(uint)), sizeof(ulong));
        var blobLength = registerBlocksOffset + (groups.Length * RegisterDefaultBlockSize);

        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, (ulong)blobLength, 0x1000, out address))
        {
            return false;
        }

        var blob = new byte[blobLength];
        WriteBlobUInt64(blob, 0x00, address + (ulong)cxTableOffset);
        WriteBlobUInt64(blob, 0x08, address + (ulong)shTableOffset);
        WriteBlobUInt64(blob, 0x10, address + (ulong)ucTableOffset);
        WriteBlobUInt64(blob, 0x30, address + (ulong)typesOffset);
        WriteBlobUInt32(blob, 0x38, (uint)groups.Length);

        for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            var group = groups[groupIndex];
            if (group.Registers.Length > 16)
            {
                return false;
            }

            var tableOffset = group.Space switch
            {
                0 => cxTableOffset,
                1 => shTableOffset,
                2 => ucTableOffset,
                _ => -1,
            };
            var tableLength = group.Space switch
            {
                0 => cxTableLength,
                1 => shTableLength,
                2 => ucTableLength,
                _ => 0,
            };
            if (tableOffset < 0 || group.Index >= tableLength)
            {
                return false;
            }

            var registerBlockOffset = registerBlocksOffset + (groupIndex * RegisterDefaultBlockSize);
            WriteBlobUInt64(
                blob,
                tableOffset + ((int)group.Index * sizeof(ulong)),
                address + (ulong)registerBlockOffset);

            var typeEntryOffset = typesOffset + (groupIndex * 3 * sizeof(uint));
            WriteBlobUInt32(blob, typeEntryOffset, group.Type);
            WriteBlobUInt32(blob, typeEntryOffset + sizeof(uint), (group.Index * 4) + group.Space);

            for (var registerIndex = 0; registerIndex < group.Registers.Length; registerIndex++)
            {
                var register = group.Registers[registerIndex];
                var registerOffset = registerBlockOffset + (registerIndex * 2 * sizeof(uint));
                WriteBlobUInt32(blob, registerOffset, register.Offset);
                WriteBlobUInt32(blob, registerOffset + sizeof(uint), register.Value);
            }
        }

        return ctx.Memory.TryWrite(address, blob);
    }

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) & -alignment;

    private static void WriteBlobUInt32(Span<byte> blob, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(blob[offset..], value);

    private static void WriteBlobUInt64(Span<byte> blob, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(blob[offset..], value);

    private static int ReturnPointer(CpuContext ctx, ulong pointer)
    {
        ctx[CpuRegister.Rax] = pointer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }

    private static uint Pm4(uint lengthDwords, uint op, uint register) =>
        0xC0000000u |
        ((((ushort)lengthDwords - 2u) & 0x3FFFu) << 16) |
        ((op & 0xFFu) << 8) |
        ((register & 0x3Fu) << 2);

    private static uint EncodeWaitRegMemPoll(uint pollCycles) =>
        Math.Min(pollCycles >> 4, 0xFFFFu);

    private static uint EncodeWaitRegMem32Control(uint compareFunction, uint operation, uint cachePolicy) =>
        0x10u |
        (compareFunction & 0x7u) |
        ((operation & 0x3u) << 8) |
        ((operation & 0xCu) << 4) |
        ((cachePolicy & 0x3u) << 25);

    private static uint EncodeWaitRegMem64Control(uint compareFunction, uint operation, uint cachePolicy) =>
        0x10u |
        (compareFunction & 0x7u) |
        ((operation & 0x1u) << 8) |
        ((operation & 0x6u) << 5) |
        ((cachePolicy & 0x3u) << 25);

    private static uint Pm4Length(uint header) =>
        ((header >> 16) & 0x3FFFu) + 2u;

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = buffer[0];
        return true;
    }

    // A submitted command buffer is bulk-copied once per submit and served
    // from this thread-local window: the previous per-dword reads each took
    // the guest-memory reader lock and ran a region binary search, which
    // dominated submit parsing (thousands of locked 4-byte reads per DCB).
    [ThreadStatic]
    private static byte[]? _dcbWindowBuffer;
    [ThreadStatic]
    private static ulong _dcbWindowStart;
    [ThreadStatic]
    private static int _dcbWindowByteLength;
    [ThreadStatic]
    private static DcbWindowInvalidationRegistry.Lease? _dcbWindowLease;

    /// <summary>
    /// Drops the bulk-read window when a self-patching command buffer writes
    /// into its own bytes during parse, so subsequent reads see live guest
    /// memory instead of the pre-write snapshot. Self-patching is rare, so
    /// paying live-read cost for the rest of that one submit is acceptable.
    /// </summary>
    private static void InvalidateDcbWindowIfOverlaps(ulong address, ulong length)
    {
        if (length == 0)
        {
            return;
        }

        DcbWindowInvalidationRegistry.Invalidate(address, length);

        if (_dcbWindowBuffer is not null &&
            address < SaturatingAdd(_dcbWindowStart, (ulong)_dcbWindowByteLength) &&
            SaturatingAdd(address, length) > _dcbWindowStart)
        {
            DropCurrentDcbWindow();
        }
    }

    private static void DropCurrentDcbWindow()
    {
        DcbWindowInvalidationRegistry.Unregister(_dcbWindowLease);
        _dcbWindowLease = null;
        _dcbWindowBuffer = null;
        _dcbWindowByteLength = 0;
    }

    private static ulong SaturatingAdd(ulong value, ulong addend) =>
        ulong.MaxValue - value < addend ? ulong.MaxValue : value + addend;

    private static bool TryReadUInt16(CpuContext ctx, ulong address, out ushort value)
    {
        if (_dcbWindowBuffer is { } window &&
            _dcbWindowLease is { } lease &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(ushort) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt16LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            if (DcbWindowInvalidationRegistry.IsValid(lease))
            {
                return true;
            }

            DropCurrentDcbWindow();
        }

        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        if (_dcbWindowBuffer is { } window &&
            _dcbWindowLease is { } lease &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(uint) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            if (DcbWindowInvalidationRegistry.IsValid(lease))
            {
                return true;
            }

            DropCurrentDcbWindow();
        }

        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        if (_dcbWindowBuffer is { } window &&
            _dcbWindowLease is { } lease &&
            address >= _dcbWindowStart &&
            address - _dcbWindowStart + sizeof(ulong) <= (ulong)_dcbWindowByteLength)
        {
            value = BinaryPrimitives.ReadUInt64LittleEndian(
                window.AsSpan((int)(address - _dcbWindowStart)));
            if (DcbWindowInvalidationRegistry.IsValid(lease))
            {
                return true;
            }

            DropCurrentDcbWindow();
        }

        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryReadGuestCString(
        CpuContext ctx,
        ulong address,
        int maximumLength,
        out byte[] bytes)
    {
        if (address == 0)
        {
            bytes = [];
            return true;
        }

        var values = new List<byte>(Math.Min(maximumLength, 128));
        for (var index = 0; index < maximumLength; index++)
        {
            if (!TryReadByte(ctx, address + (ulong)index, out var value))
            {
                bytes = [];
                return false;
            }

            if (value == 0)
            {
                bytes = [.. values];
                return true;
            }

            values.Add(value);
        }

        bytes = [];
        return false;
    }

    private static bool TryGetPacketIdentity(
        CpuContext ctx,
        ulong commandAddress,
        out uint op,
        out uint register)
    {
        op = 0;
        register = 0;
        if (commandAddress == 0 || !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return false;
        }

        op = (header >> 8) & 0xFFu;
        register = (header >> 2) & 0x3Fu;
        return true;
    }

    private static bool TryCopyGuestMemory(
        CpuContext ctx,
        ulong sourceAddress,
        ulong destinationAddress,
        uint byteCount)
    {
        if (sourceAddress == destinationAddress)
        {
            return true;
        }

        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        ulong offset = 0;
        while (offset < byteCount)
        {
            var chunkLength = (int)Math.Min((ulong)buffer.Length, byteCount - offset);
            var chunk = buffer.AsSpan(0, chunkLength);
            if (!ctx.Memory.TryRead(sourceAddress + offset, chunk) ||
                !ctx.Memory.TryWrite(destinationAddress + offset, chunk))
            {
                return false;
            }

            offset += (uint)chunkLength;
        }

        return true;
    }

    private static bool TryFillGuestMemory(
        CpuContext ctx,
        uint value,
        ulong destinationAddress,
        uint byteCount)
    {
        var buffer = new byte[Math.Min(byteCount, 64u * 1024u)];
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, value);
        for (var offset = 0; offset < buffer.Length; offset += sizeof(uint))
        {
            var remaining = Math.Min(sizeof(uint), buffer.Length - offset);
            encoded[..remaining].CopyTo(buffer.AsSpan(offset, remaining));
        }

        ulong destinationOffset = 0;
        while (destinationOffset < byteCount)
        {
            var chunkLength = (int)Math.Min(
                (ulong)buffer.Length,
                byteCount - destinationOffset);
            if (!ctx.Memory.TryWrite(
                    destinationAddress + destinationOffset,
                    buffer.AsSpan(0, chunkLength)))
            {
                return false;
            }

            destinationOffset += (uint)chunkLength;
        }

        return true;
    }

    private static bool ShouldTraceHotPath(ref long counter)
    {
        var count = Interlocked.Increment(ref counter);
        return count <= 8 || count % 100_000 == 0;
    }

    // Interpolated-string handlers gated on the trace flags: when tracing is
    // off (the normal case) the compiler skips every AppendFormatted call, so
    // the interpolation never runs. These functions are on the hottest guest
    // paths — e.g. AddIndirectPatchRegisters fires tens of thousands of times
    // per second — and previously formatted a discarded string every call.
    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    private ref struct AgcTraceHandler
    {
        private System.Runtime.CompilerServices.DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        public AgcTraceHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _enabled = _traceAgc;
            shouldAppend = _enabled;
            _inner = _enabled
                ? new System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
        public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    [System.Runtime.CompilerServices.InterpolatedStringHandler]
    private ref struct AgcShaderTraceHandler
    {
        private System.Runtime.CompilerServices.DefaultInterpolatedStringHandler _inner;
        private readonly bool _enabled;

        public AgcShaderTraceHandler(int literalLength, int formattedCount, out bool shouldAppend)
        {
            _enabled = _traceAgcShader;
            shouldAppend = _enabled;
            _inner = _enabled
                ? new System.Runtime.CompilerServices.DefaultInterpolatedStringHandler(literalLength, formattedCount)
                : default;
        }

        public void AppendLiteral(string value) => _inner.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _inner.AppendFormatted(value, format);
        public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
    }

    // Monotonic seconds since process start, prefixed on every AGC trace
    // line — the frame pipeline's dependency chains span tens of seconds.
    private static readonly long _traceStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

    private static string TraceSeconds() =>
        ((System.Diagnostics.Stopwatch.GetTimestamp() - _traceStartTicks) /
         (double)System.Diagnostics.Stopwatch.Frequency).ToString(
            "F3", System.Globalization.CultureInfo.InvariantCulture);

    private const long MaximumRetainedIndexBytesPerSubmission = 64L * 1024 * 1024;
    private const long MaximumRetainedVertexBytesPerSubmission = 64L * 1024 * 1024;

    private static Dictionary<ulong, SubmittedIndexSnapshot>? CaptureSubmittedIndexPackets(
        CpuContext ctx,
        ulong commandAddress,
        uint dwordCount,
        uint initialIndexSize,
        out Dictionary<ulong, SubmittedVertexSnapshot>? vertexSnapshots)
    {
        vertexSnapshots = null;
        if (!_retainSubmittedIndexData &&
            !_retainSubmittedVertexData)
        {
            return null;
        }

        var visited = new HashSet<(ulong Address, uint Dwords)>();
        var indexSnapshots = _retainSubmittedIndexData
            ? new Dictionary<ulong, SubmittedIndexSnapshot>()
            : null;
        vertexSnapshots = _retainSubmittedVertexData
            ? new Dictionary<ulong, SubmittedVertexSnapshot>()
            : null;
        var captureState = new SubmittedDcbState
        {
            IndexSize = initialIndexSize,
        };
        var retainedIndexBytes = 0L;
        var retainedVertexBytes = 0L;
        CaptureSubmittedIndexPacketsCore(
            ctx,
            commandAddress,
            dwordCount,
            visited,
            indexSnapshots,
            vertexSnapshots,
            captureState,
            ref retainedIndexBytes,
            ref retainedVertexBytes,
            depth: 0);
        if (vertexSnapshots is { Count: 0 })
        {
            vertexSnapshots = null;
        }

        return indexSnapshots is { Count: > 0 } ? indexSnapshots : null;
    }

    private static void CaptureSubmittedIndexPacketsCore(
        CpuContext ctx,
        ulong commandAddress,
        uint dwordCount,
        HashSet<(ulong Address, uint Dwords)> visited,
        Dictionary<ulong, SubmittedIndexSnapshot>? indexSnapshots,
        Dictionary<ulong, SubmittedVertexSnapshot>? vertexSnapshots,
        SubmittedDcbState captureState,
        ref long retainedIndexBytes,
        ref long retainedVertexBytes,
        int depth)
    {
        if (commandAddress == 0 || dwordCount == 0 || depth > 8 ||
            !visited.Add((commandAddress, dwordCount)))
        {
            return;
        }

        var offset = 0u;
        while (offset < dwordCount)
        {
            var packetAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, packetAddress, out var header))
            {
                return;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                offset++;
                continue;
            }

            if (packetType != 3)
            {
                return;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                return;
            }

            var opcode = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (opcode == ItIndexType && length >= 2 &&
                TryReadUInt32(ctx, packetAddress + 4, out var packetIndexSize))
            {
                captureState.IndexSize = packetIndexSize & 0x3u;
            }

            ApplySubmittedRegisters(
                ctx,
                captureState,
                packetAddress,
                length,
                opcode,
                register);

            if (opcode == ItDrawIndex2 && length >= 6 &&
                TryReadUInt32(ctx, packetAddress + 4, out var maximumIndexCount) &&
                TryReadUInt32(ctx, packetAddress + 8, out var indexBaseLo) &&
                TryReadUInt32(ctx, packetAddress + 12, out var indexBaseHi) &&
                TryReadUInt32(ctx, packetAddress + 16, out var indexCount))
            {
                var indexAddress = indexBaseLo | ((ulong)indexBaseHi << 32);
                var indexStride = AgcIndexHelpers.GetGuestStrideBytes(
                    AgcIndexHelpers.Decode(captureState.IndexSize));
                var byteCount64 = (ulong)indexCount * (uint)indexStride;
                SubmittedIndexSnapshot? indexSnapshot = null;
                if (indexSnapshots is not null &&
                    byteCount64 != 0 &&
                    byteCount64 <= int.MaxValue &&
                    retainedIndexBytes + (long)byteCount64 <=
                        MaximumRetainedIndexBytesPerSubmission)
                {
                    var data = new byte[(int)byteCount64];
                    if (ctx.Memory.TryRead(indexAddress, data) ||
                        KernelMemoryCompatExports.TryReadTrackedLibcHeap(indexAddress, data))
                    {
                        indexSnapshot = new SubmittedIndexSnapshot(
                            indexAddress,
                            indexCount,
                            indexStride,
                            data);
                        indexSnapshots[packetAddress] = indexSnapshot;
                        retainedIndexBytes += data.Length;
                    }
                }

                captureState.IndexBufferAddress = indexAddress;
                captureState.IndexBufferCount = maximumIndexCount;
                captureState.DrawIndexOffset = 0;
                captureState.CurrentIndexSnapshot = indexSnapshot;
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    captureState,
                    packetAddress,
                    indexCount,
                    indexed: true,
                    vertexSnapshots,
                    ref retainedVertexBytes);
                captureState.CurrentIndexSnapshot = null;
            }
            else if (opcode == ItDrawIndexAuto && length >= 3 &&
                     TryReadUInt32(ctx, packetAddress + 4, out var vertexCount))
            {
                TryCaptureSubmittedVertexSnapshot(
                    ctx,
                    captureState,
                    packetAddress,
                    vertexCount,
                    indexed: false,
                    vertexSnapshots,
                    ref retainedVertexBytes);
            }

            if (opcode == ItIndirectBuffer && length >= 4 &&
                TryReadUInt32(ctx, packetAddress + 4, out var chainLow) &&
                TryReadUInt32(ctx, packetAddress + 8, out var chainHigh) &&
                TryReadUInt32(ctx, packetAddress + 12, out var chainDwords))
            {
                var chainAddress = ((ulong)(chainHigh & 0xFFFFu) << 32) | chainLow;
                var chainLength = chainDwords & 0xFFFFFu;
                CaptureSubmittedIndexPacketsCore(
                    ctx,
                    chainAddress,
                    chainLength,
                    visited,
                    indexSnapshots,
                    vertexSnapshots,
                    captureState,
                    ref retainedIndexBytes,
                    ref retainedVertexBytes,
                    depth + 1);
            }

            offset += length;
        }
    }

    private static void TryCaptureSubmittedVertexSnapshot(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint drawCount,
        bool indexed,
        Dictionary<ulong, SubmittedVertexSnapshot>? snapshots,
        ref long retainedBytes)
    {
        if (snapshots is null ||
            drawCount == 0 ||
            !TryGetShaderAddress(
                state.ShRegisters,
                SpiShaderPgmLoEs,
                SpiShaderPgmHiEs,
                out var exportShaderAddress))
        {
            return;
        }

        ulong exportShaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(exportShaderAddress, out exportShaderHeader);
        }

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                exportShaderAddress,
                exportShaderHeader,
                state.ShRegisters,
                SelectExportUserDataRegister(state.ShRegisters),
                out var exportState,
                out _,
                userDataScalarRegisterBase: NggUserDataScalarRegisterBase) ||
            !TryGetRequiredVertexRecordCount(
                ctx,
                state,
                drawCount,
                indexed,
                out var recordCount) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                exportState,
                out var evaluation,
                out _,
                resolveVertexInputs: true,
                requiredVertexRecordCount: recordCount,
                captureVertexInputsOnly: true))
        {
            return;
        }

        try
        {
            if (evaluation.VertexInputs is not { Count: > 0 } inputs)
            {
                return;
            }

            if (!TryCopySubmittedVertexInputs(
                    inputs,
                    MaximumRetainedVertexBytesPerSubmission - retainedBytes,
                    out var retainedInputs,
                    out var snapshotBytes))
            {
                return;
            }

            snapshots[packetAddress] = new SubmittedVertexSnapshot(
                exportShaderAddress,
                retainedInputs);
            retainedBytes += snapshotBytes;
        }
        finally
        {
            ReturnPooledEvaluationArrays(evaluation);
        }
    }

    internal static bool TryCopySubmittedVertexInputs(
        IReadOnlyList<Gen5VertexInputBinding> inputs,
        long maximumBytes,
        out Gen5VertexInputBinding[] retainedInputs,
        out long retainedBytes)
    {
        retainedInputs = [];
        retainedBytes = 0;
        if (inputs.Count == 0 || maximumBytes <= 0)
        {
            return false;
        }

        var uniqueLengths = new Dictionary<byte[], int>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var input in inputs)
        {
            var length = Math.Clamp(input.DataLength, 0, input.Data.Length);
            if (!uniqueLengths.TryGetValue(input.Data, out var existing) ||
                length > existing)
            {
                uniqueLengths[input.Data] = length;
            }
        }

        retainedBytes = uniqueLengths.Values.Sum(static length => (long)length);
        if (retainedBytes == 0 || retainedBytes > maximumBytes)
        {
            retainedBytes = 0;
            return false;
        }

        var copies = new Dictionary<byte[], byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var (source, length) in uniqueLengths)
        {
            var copy = new byte[length];
            source.AsSpan(0, length).CopyTo(copy);
            copies.Add(source, copy);
        }

        retainedInputs = new Gen5VertexInputBinding[inputs.Count];
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var copy = copies[input.Data];
            retainedInputs[index] = input with
            {
                Data = copy,
                DataLength = Math.Clamp(input.DataLength, 0, copy.Length),
                DataPooled = false,
            };
        }

        return true;
    }

    private static void ApplySubmittedVertexSnapshot(
        SubmittedDcbState state,
        ulong exportShaderAddress,
        ref Gen5ShaderEvaluation evaluation)
    {
        var snapshot = state.CurrentVertexSnapshot;
        var current = evaluation.VertexInputs;
        if (snapshot is null ||
            snapshot.ExportShaderAddress != exportShaderAddress ||
            current is null ||
            current.Count != snapshot.Bindings.Count)
        {
            return;
        }

        for (var index = 0; index < current.Count; index++)
        {
            var live = current[index];
            var retained = snapshot.Bindings[index];
            if (live.Pc != retained.Pc ||
                live.Location != retained.Location ||
                live.BaseAddress != retained.BaseAddress ||
                live.Stride != retained.Stride ||
                live.OffsetBytes != retained.OffsetBytes)
            {
                return;
            }
        }

        var returned = new HashSet<byte[]>(
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var binding in current)
        {
            if (binding.DataPooled && returned.Add(binding.Data))
            {
                GuestDataPool.Shared.Return(binding.Data);
            }
        }
        evaluation = evaluation with { VertexInputs = snapshot.Bindings };
    }

    private static void TraceAgc(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcTraceHandler message)
    {
        if (_traceAgc)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgc(string message)
    {
        if (!_traceAgc)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message}");
    }

    private static void TraceAgcShader(
        [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument] ref AgcShaderTraceHandler message)
    {
        if (_traceAgcShader)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message.ToStringAndClear()}");
        }
    }

    private static void TraceAgcShader(string message)
    {
        if (!_traceAgcShader)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] t={TraceSeconds()} {message}");
    }

    private static string FormatShaderDwords(IReadOnlyList<uint> values) =>
        values.Count == 0
            ? "none"
            : string.Join(',', values.Select(static value => $"{value:X8}"));

    private static string FormatTextureDescriptor(TextureDescriptor descriptor) =>
        $"addr=0x{descriptor.Address:X16} {descriptor.Width}x{descriptor.Height} " +
        $"fmt={descriptor.Format} num={descriptor.NumberType} tile={descriptor.TileMode} " +
        $"type={descriptor.Type} depth={descriptor.Depth} base_array={descriptor.BaseArray} " +
        $"levels={descriptor.BaseLevel}-{descriptor.LastLevel}/max{descriptor.MaxMip} " +
        $"pitch={descriptor.Pitch} array_pitch={descriptor.ArrayPitch} " +
        $"lod={descriptor.MinLod:X3}/{descriptor.MinLodWarn:X3} " +
        $"bc={descriptor.BcSwizzle} meta=0x{descriptor.MetadataAddress:X16} " +
        $"flags=0x{descriptor.DescriptorFlags:X6} dst=0x{descriptor.DstSelect:X3}";

    private static ulong? ParseOptionalHexAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(
            span,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var address)
            ? address
            : null;
    }

    private static void DumpCompiledShader(
        string stage,
        ulong shaderAddress,
        ulong stateFingerprint,
        IGuestCompiledShader shader,
        Gen5ShaderProgram program)
    {
        if (shader.Payload.Length == 0 ||
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var addressFilter = Environment.GetEnvironmentVariable(
            "SHARPEMU_DUMP_SPIRV_ADDRESS");
        if (!string.IsNullOrWhiteSpace(addressFilter))
        {
            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(
                    span,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var filteredAddress) ||
                shaderAddress != filteredAddress)
            {
                return;
            }
        }

        var directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
        Directory.CreateDirectory(directory);
        var name = $"{shaderAddress:X16}-{stateFingerprint:X16}.{stage}";
        File.WriteAllBytes(
            Path.Combine(directory, $"{name}.{shader.PayloadFileExtension}"),
            shader.Payload);

        var lines = new List<string>(program.Instructions.Count + 2)
        {
            $"address=0x{program.Address:X16}",
            "pc words opcode destinations <- sources control",
        };
        foreach (var instruction in program.Instructions)
        {
            lines.Add(
                $"0x{instruction.Pc:X4} " +
                $"{string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} " +
                $"{instruction.Opcode} " +
                $"{string.Join(',', instruction.Destinations)} <- " +
                $"{string.Join(',', instruction.Sources)} " +
                $"{instruction.Control}");
        }

        File.WriteAllLines(Path.Combine(directory, $"{name}.ir.txt"), lines);
    }

    private static void TraceCreateShader(ulong destinationAddress, ulong headerAddress, ulong codeAddress, string detail)
    {
        var isOk = string.Equals(detail, "ok", StringComparison.Ordinal);
        if (isOk &&
            (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal) ||
             !ShouldTraceHotPath(ref _createShaderTraceCount)))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.create_shader dst=0x{destinationAddress:X16} header=0x{headerAddress:X16} code=0x{codeAddress:X16} {detail}");
    }

    // Hardware REWIND is a fixed 2-dword header + body (valid bit 31).
    [SysAbiExport(
        Nid = "QIXCsbipds0",
        ExportName = "sceAgcDcbRewindGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbRewindGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    // Writes IT_REWIND. When valid=0 the submit parser suspends until
    // sceAgcRewindPatchSetRewindState sets bit 31 on the body dword.
    [SysAbiExport(
        Nid = "zfcxg-ewMK8",
        ExportName = "sceAgcDcbRewind",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbRewind(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        // rsi bit0 = valid; bit1 = offload_enable (PM4 body bits 31 / 24).
        var flags = ctx[CpuRegister.Rsi];
        var valid = (flags & 1UL) != 0;
        var offloadEnable = (flags & 2UL) != 0;
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var body = (valid ? RewindValidBit : 0u) |
                   (offloadEnable ? RewindOffloadEnableBit : 0u);
        if (!TryAllocateCommandDwords(ctx, dcb, 2, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(2, ItRewind, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, body))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_rewind buf=0x{dcb:X16} cmd=0x{cmd:X16} valid={valid} offload={offloadEnable}");
        return ReturnPointer(ctx, cmd);
    }

    // Patches the REWIND body dword's valid bit and wakes any DCB suspended on it.
    // rdi is the packet pointer returned by sceAgcDcbRewind (header address).
    [SysAbiExport(
        Nid = "ziVA3whp3p4",
        ExportName = "sceAgcRewindPatchSetRewindState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int RewindPatchSetRewindState(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        var valid = (ctx[CpuRegister.Rsi] & 1UL) != 0;
        if (packetAddress == 0 ||
            (long)packetAddress < 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var bodyAddress = packetAddress;
        if (TryReadUInt32(ctx, packetAddress, out var header) &&
            ((header >> 8) & 0xFFu) == ItRewind)
        {
            bodyAddress = packetAddress + sizeof(uint);
        }

        if (!TryReadUInt32(ctx, bodyAddress, out var body) ||
            !TryWriteUInt32(
                ctx,
                bodyAddress,
                valid ? body | RewindValidBit : body & ~RewindValidBit))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReadUInt32(ctx, bodyAddress, out var patched))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (valid)
        {
            GpuWaitRegistry.RecordProduced(ctx.Memory, bodyAddress, patched);
        }

        TraceAgc($"agc.rewind_patch addr=0x{bodyAddress:X16} valid={valid} body=0x{patched:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Matches the 4-dword INDIRECT_BUFFER packet DcbJump writes below.
    // Returning NOT_FOUND here left callers with a null packet pointer and an
    // immediate write AV on RenderThread.
    [SysAbiExport(
        Nid = "VEGu4dixjUg",
        ExportName = "sceAgcDcbJumpGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbJumpGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "xSAR0LTcRKM",
        ExportName = "sceAgcDcbJump",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbJump(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var target = ctx[CpuRegister.Rsi];
        var sizeDwords = (uint)ctx[CpuRegister.Rdx];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, dcb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItIndirectBuffer, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 12, sizeDwords & 0xFFFFF))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    // Matches the 4-dword INDIRECT_BUFFER packet CbBranch writes below.
    [SysAbiExport(
        Nid = "uZW-mqsxkrM",
        ExportName = "sceAgcCbBranchGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbBranchGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    // COND_EXEC gates the following execCount dwords on a 32-bit predicate in
    // memory. Our submitted-packet walker skips unknown PM4 ops, so the recorded
    // packet degrades to "predicate always true" — the gated commands always run.
    [SysAbiExport(
        Nid = "BIPexNBSGog",
        ExportName = "sceAgcDcbCondExec",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbCondExec(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var predicateAddress = ctx[CpuRegister.Rsi];
        var execCountDwords = (uint)ctx[CpuRegister.Rdx];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, dcb, 5, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(5, ItCondExec, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(predicateAddress & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)(predicateAddress >> 32)) ||
            !ctx.TryWriteUInt32(cmd + 12, 0) ||
            !ctx.TryWriteUInt32(cmd + 16, execCountDwords & 0x3FFF))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    // How a title continues a frame whose command arena filled: it branches from
    // the tail of the exhausted buffer into a fresh one and submits only the first
    // buffer, leaving the driver to follow the link. Dropping this packet strands
    // everything written after the switch -- for UE 4.27 that is the rest of the
    // frame, including its flip and the end-of-frame labels the guest's AGC
    // interrupt thread needs before it will trigger the backbuffer event.
    //
    // The branch target and its length arrive on the stack, past six register
    // arguments (verified against a live call: the values matched the continuation
    // buffer the title had already written into).
    [SysAbiExport(
        Nid = "w1KFAHVqpaU",
        ExportName = "sceAgcCbBranch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbBranch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var target) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (3 * sizeof(ulong)), out var targetDwords))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItIndirectBuffer, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)targetDwords & 0xFFFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_branch buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"target=0x{target:X16} dwords={targetDwords}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "b-oySn+G2tE",
        ExportName = "sceAgcAcbJumpGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbJumpGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "e1DFTg+Sd8U",
        ExportName = "sceAgcAcbJump",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbJump(CpuContext ctx) => DcbJump(ctx);

    // Sony SetCf* range writer — SET_CONTEXT_REG packet (same shape as SH range).
    [SysAbiExport(
        Nid = "BVFg3CWU6Eo",
        ExportName = "sceAgcDcbSetCfRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCfRegisterRangeDirect(CpuContext ctx) =>
        DcbSetRegisterRangeDirect(ctx, ItSetContextReg, "cf");

    // Logged unresolved as LHFXRrlTPD8 during North Yankton load.
    [SysAbiExport(
        Nid = "LHFXRrlTPD8",
        ExportName = "sceAgcDcbSetCxRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegisterDirect(CpuContext ctx) =>
        DcbSetRegisterDirect(ctx, ItSetContextReg, "cx");

    private static int DcbSetRegisterRangeDirect(CpuContext ctx, uint op, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || valueCount == 0 || valueCount > 0x3FFE)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = valueCount + 2;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, op, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset & 0xFFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc(
            $"agc.dcb_set_{registerSpace}_range buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{offset:X4} count={valueCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "bbFueFP+J4k",
        ExportName = "sceAgcDcbSetPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetPredication(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var condition = (uint)(ctx[CpuRegister.Rsi] & 1u);
        var operation = (uint)(ctx[CpuRegister.Rdx] & 0x7u);
        var waitOperation = (uint)(ctx[CpuRegister.Rcx] & 1u);
        var address = ctx[CpuRegister.R8];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var flags = (condition << 8) | (waitOperation << 12) | (operation << 16);
        if (!TryAllocateCommandDwords(ctx, dcb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItSetPredication, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, flags) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)address & 0xFFFF_FFF0u) ||
            !ctx.TryWriteUInt32(cmd + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    [SysAbiExport(
        Nid = "w6Dj1VJt5qY",
        ExportName = "sceAgcSetPacketPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetPacketPredication(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        var predication = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 || !TryReadUInt32(ctx, packetAddress, out var header))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        header = (header & ~1u) | (predication == 1 ? 1u : 0u);
        return !ctx.TryWriteUInt32(packetAddress, header)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // ABI (reversed from Quake): rdi = array of DCB base addresses (u64 each),
    // rsi = array of DCB sizes in dwords (u32 each), rdx = buffer count.
    [SysAbiExport(
        Nid = "6UzEidRZwkg",
        ExportName = "sceAgcDriverSubmitMultiDcbs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSubmitMultiDcbs(CpuContext ctx)
    {
        Interlocked.Increment(ref _dcbSubmitCount);
        Volatile.Write(ref _lastDcbSubmitTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

        var addressArray = ctx[CpuRegister.Rdi];
        var sizeArray = ctx[CpuRegister.Rsi];
        var bufferCount = (uint)ctx[CpuRegister.Rdx];
        if (addressArray == 0 || sizeArray == 0 || bufferCount == 0 || bufferCount > 4096)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var tracePackets = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal);

        var gpuState = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (gpuState.Gate)
        {
            Gen5ShaderScalarEvaluator.BeginGlobalMemoryReadScope();
            try
            {
                for (uint i = 0; i < bufferCount; i++)
                {
                    if (gpuState.Graphics.IsFaulted)
                    {
                        ReportFaultedSubmissionRejected(
                            gpuState.Graphics,
                            gpuState.Graphics.ActiveSubmissionId);
                        break;
                    }

                    if (!ctx.TryReadUInt64(addressArray + i * 8, out var commandAddress) ||
                        commandAddress == 0 ||
                        !ctx.TryReadUInt32(sizeArray + i * 4, out var dwordCount) ||
                        dwordCount == 0)
                    {
                        continue;
                    }

                    if (tracePackets)
                    {
                        TraceAgc(
                            $"agc.driver_submit_multi_dcbs index={i}/{bufferCount} " +
                            $"addr=0x{commandAddress:X16} dwords={dwordCount}");
                    }

                    ParseSubmittedDcb(
                        ctx,
                        gpuState,
                        gpuState.Graphics,
                        commandAddress,
                        dwordCount,
                        tracePackets);
                }

                DrainResumableDcbs(ctx, gpuState, tracePackets);
            }
            finally
            {
                Gen5ShaderScalarEvaluator.EndGlobalMemoryReadScope();
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "AOLcoIkQDgM",
        ExportName = "sceAgcDriverQueryResourceRegistrationUserMemoryRequirements",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverQueryResourceRegistrationUserMemoryRequirements(CpuContext ctx)
    {
        var sizeAddress = ctx[CpuRegister.Rdi];
        var resourceCount = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (sizeAddress == 0 || resourceCount == 0 || ownerCount == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ulong requiredSize;
        try
        {
            requiredSize = checked(
                resourceCount * ResourceRegistrationBytesPerResource +
                ownerCount * ResourceRegistrationBytesPerOwner);
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryWriteUInt64(sizeAddress, requiredSize))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.driver_query_resource_registration_memory resources={resourceCount} " +
            $"owners={ownerCount} bytes=0x{requiredSize:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "F0Y42t-3e18",
        ExportName = "sceAgcDriverInitResourceRegistration",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverInitResourceRegistration(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rdi];
        var memorySize = ctx[CpuRegister.Rsi];
        var ownerCount = ctx[CpuRegister.Rdx];
        if (memoryAddress == 0 || memorySize == 0 || ownerCount == 0 || ownerCount > uint.MaxValue)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.ResourceRegistrationInitialized = true;
            state.ResourceRegistrationMemory = memoryAddress;
            state.ResourceRegistrationMemorySize = memorySize;
            state.ResourceRegistrationMaxOwners = (uint)ownerCount;
            state.ResourceOwners.Clear();
            state.RegisteredResources.Clear();
            state.DefaultOwner = DefaultAgcOwner;
            state.NextOwner = 1;
            state.NextResource = 1;
        }

        TraceAgc(
            $"agc.driver_init_resource_registration memory=0x{memoryAddress:X16} " +
            $"bytes=0x{memorySize:X} owners={ownerCount}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "U9ueyEhSkF4",
        ExportName = "sceAgcDriverRegisterDefaultOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterDefaultOwner(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            state.DefaultOwner = owner;
        }

        TraceAgc($"agc.driver_register_default_owner owner={owner}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "X-Nm5KLREeg",
        ExportName = "sceAgcDriverRegisterOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverRegisterOwner(CpuContext ctx)
    {
        var ownerAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (ownerAddress == 0 || nameAddress == 0 ||
            !TryReadGuestCString(
                ctx,
                nameAddress,
                ResourceRegistrationMaxNameLength,
                out var nameBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        uint owner;
        lock (state.Gate)
        {
            if (state.ResourceRegistrationInitialized &&
                state.ResourceRegistrationMaxOwners != 0 &&
                state.ResourceOwners.Count >= state.ResourceRegistrationMaxOwners)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            owner = state.NextOwner;
            while (owner == state.DefaultOwner || state.ResourceOwners.ContainsKey(owner))
            {
                owner++;
                if (owner == 0)
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                }
            }

            state.NextOwner = owner + 1;
            state.ResourceOwners.Add(owner, System.Text.Encoding.UTF8.GetString(nameBytes));
        }

        if (!ctx.TryWriteUInt32(ownerAddress, owner))
        {
            lock (state.Gate)
            {
                state.ResourceOwners.Remove(owner);
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc(
            $"agc.driver_register_owner out=0x{ownerAddress:X16} owner={owner} " +
            $"name={System.Text.Encoding.UTF8.GetString(nameBytes)}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int RemoveResourcesForOwner(SubmittedGpuState state, uint owner)
    {
        var stale = new List<uint>();
        foreach (var (handle, resource) in state.RegisteredResources)
        {
            if (resource.Owner == owner)
            {
                stale.Add(handle);
            }
        }

        foreach (var handle in stale)
        {
            state.RegisteredResources.Remove(handle);
        }

        return stale.Count;
    }

    [SysAbiExport(
        Nid = "ZLJk9r2+2Aw",
        ExportName = "sceAgcDriverUnregisterOwnerAndResources",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterOwnerAndResources(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        int resources;
        lock (state.Gate)
        {
            if (!state.ResourceOwners.Remove(owner))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            resources = RemoveResourcesForOwner(state, owner);
            state.ComputeQueues.Remove(owner);
        }

        TraceAgc($"agc.driver_unregister_owner owner={owner} resources={resources}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "SCoAN5fYlUM",
        ExportName = "sceAgcDriverUnregisterAllResourcesForOwner",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterAllResourcesForOwner(CpuContext ctx)
    {
        var owner = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(ctx.Memory, static _ => new SubmittedGpuState());
        int resources;
        lock (state.Gate)
        {
            resources = RemoveResourcesForOwner(state, owner);
        }

        TraceAgc($"agc.driver_unregister_owner_resources owner={owner} resources={resources}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pWLG7WOpVcw",
        ExportName = "sceAgcDriverUnregisterResource",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DriverUnregisterResource(CpuContext ctx)
    {
        var resourceHandle = (uint)ctx[CpuRegister.Rdi];
        var state = _submittedGpuStates.GetValue(CanonicalMemory(ctx.Memory), static _ => new SubmittedGpuState());
        lock (state.Gate)
        {
            if (!state.RegisteredResources.Remove(resourceHandle))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        TraceAgc($"agc.driver_unregister_resource handle={resourceHandle}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Tessellation-factor ring and hull-shader off-chip buffers are guest-driver
    // configuration for on-hardware tessellation memory. Our translator handles
    // shader execution directly, so there is no guest-side ring to program: the
    // guest driver only needs these to report success so init proceeds. Games
    // (e.g. Unity titles) call them during GPU setup and stall if unresolved.
    [SysAbiExport(
        Nid = "XlNp7jzGiPo",
        ExportName = "sceAgcDriverSetTFRing",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSetTFRing(CpuContext ctx)
    {
        TraceAgc(
            $"agc.driver_set_tf_ring ring=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"size=0x{(uint)ctx[CpuRegister.Rsi]:X8}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "MM4IZSEYytQ",
        ExportName = "sceAgcDriverSetHsOffchipParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAgcDriver")]
    public static int DriverSetHsOffchipParam(CpuContext ctx)
    {
        TraceAgc(
            $"agc.driver_set_hs_offchip_param buffer=0x{ctx[CpuRegister.Rdi]:X16} " +
            $"param=0x{(uint)ctx[CpuRegister.Rsi]:X8}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
