// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

// This partial recovers unsubmitted command-builder preambles.
public static partial class AgcExports
{
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
}
