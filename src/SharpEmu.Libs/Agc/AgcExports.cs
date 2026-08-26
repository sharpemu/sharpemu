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
    private const uint ItCondWrite = 0x45;
    private const uint ItEventWrite = 0x46;
    private const uint ItReleaseMem = 0x49;
    private const uint ItDmaData = 0x50;
    private const uint ItRewind = 0x59;
    private const uint ItSetContextReg = 0x69;
    private const uint ItSetShReg = 0x76;
    private const uint ItSetUconfigReg = 0x79;
    private const uint ItSetUconfigRegIndex = 0x7A;
    private const uint RewindValidBit = 1u << 31;
    private const uint RewindOffloadEnableBit = 1u << 24;
    private const uint ItGetLodStats = 0x8E;

    private static readonly HashSet<uint> KnownPm4Opcodes =
    [
        ItNop, ItSetBase, ItIndexBufferSize, ItIndexBase, ItDrawIndirect,
        ItDrawIndexIndirect, ItDrawIndex2, ItIndexType, ItDrawIndexAuto,
        ItNumInstances, ItDrawIndexMultiAuto, ItDrawIndexOffset2, ItWriteData,
        ItAtomicMem, ItMemSemaphore, ItCopyData,
        ItDispatchDirect, ItDispatchIndirect, ItSetPredication, ItCondExec,
        ItWaitRegMem,
        ItIndirectBuffer, ItCondWrite, ItEventWrite, ItReleaseMem, ItDmaData,
        ItRewind, ItSetContextReg, ItSetShReg, ItSetUconfigReg,
        ItSetUconfigRegIndex, ItGetLodStats,
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
    private static readonly bool _gpuLabelHostMirrorRetirementEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_LABEL_HOST_MIRROR_RETIRE"),
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

    private const ulong CommandBufferCursorUpOffset = 0x10;
    private const ulong CommandBufferCursorDownOffset = 0x18;
    private const ulong CommandBufferCallbackOffset = 0x20;
    private const ulong CommandBufferUserDataOffset = 0x28;
    private const ulong CommandBufferReservedDwOffset = 0x30;
    private static readonly object _submitTraceGate = new();
    private static readonly HashSet<uint> _tracedDcbSizes = new();
    private static readonly HashSet<(ulong Es, ulong Ps, GuestDrawKind Kind)> _tracedShaderTranslations = new();
    private static readonly HashSet<(ulong Es, ulong Ps)> _tracedShaderDecodePairs = new();
    private static readonly HashSet<(ulong Ps, string Error)> _tracedShaderFailures = new();
    private static readonly HashSet<(int Handle, int Index, ulong Address, string Path)> _tracedDisplayBuffers = new();
    private static readonly HashSet<uint> _tracedSubmittedDrawOpcodes = new();
    // Every PM4 opcode with no handler, logged once with its first two
    // payload dwords as a possible target address.
    private static readonly HashSet<uint> _seenUnknownOpcodes = new();
    private static readonly Dictionary<ulong, ulong> _shaderHeadersByCode = new();
    private static readonly ConcurrentDictionary<ulong, byte> _arrayUploadUnsupported = new();
    private static readonly bool _traceAgc = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _traceAgcShader =
        _traceAgc ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"),
            "1",
            StringComparison.Ordinal);
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



    private readonly record struct SubmittedAcquireMem(
        uint Engine,
        uint CbDbControl,
        ulong BaseAddress,
        ulong SizeBytes,
        uint PollInterval,
        AcquireMemGcrControl GcrControl)
    {
        public AgcGpuCacheSemantics Semantics =>
            GcrControl.ToSemantics(SizeBytes == 0);

        public bool InvalidatesGuestResources => GcrControl.HasResourceOperation;

        public bool CoversAllGuestMemory => Semantics.CoversAllMemory;
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
            Dictionary<ulong, SubmittedVertexSnapshot>? VertexSnapshots,
            ulong PublicationGeneration);

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
        public bool ConditionalWaitEnabled { get; set; }
        public string QueueName { get; set; } = "graphics";
        // Ident this queue's end-of-pipe completion interrupt is published under.
        // The graphics queue keeps 0; a compute queue takes the owner handle it
        // was submitted with, which is the same value the guest registers through
        // sceAgcDriverAddEqEvent.
        public ulong CompletionEventId { get; set; }
        public ulong ActiveSubmissionId { get; set; }
        public SubmittedCompletionState? ActiveCompletionState { get; set; }
        public ulong ActiveSubmissionPublicationGeneration { get; set; }
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
        public ulong AtomicReturnMeData { get; set; }
        public bool AtomicReturnMeValid { get; set; }
        public bool AtomicReturnMePending { get; set; }
        public ulong AtomicReturnMeSequence { get; set; }
        public ulong AtomicReturnMeCompletedSequence { get; set; }
        public ulong AtomicReturnPfpData { get; set; }
        public bool AtomicReturnPfpValid { get; set; }
        public bool AtomicReturnPfpPending { get; set; }
        public ulong AtomicReturnPfpSequence { get; set; }
        public ulong AtomicReturnPfpCompletedSequence { get; set; }

        // Set when parsing stops on an INDIRECT_BUFFER packet so the caller can
        // continue into the buffer it links to.
        public ulong PendingChainAddress { get; set; }
        public uint PendingChainDwords { get; set; }
        public (ulong Address, uint Dwords, ulong RingChunkBase)?
            IndirectCallReturn { get; set; }

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
        public AgcGpuCacheDomain PendingAcquireDomains { get; set; }
        public AgcGpuCacheAction PendingAcquireActions { get; set; }
        public uint PendingAcquireCbDbControl { get; set; }
        public uint PendingAcquireGcrControl { get; set; }

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
            interrupt > 6 ||
            (interrupt >= 4 && gcrControl != 0) ||
            (interrupt == 5 &&
             (destinationAddress == 0 || destinationAddress % sizeof(uint) != 0)) ||
            (interrupt == 6 &&
             (destinationAddress == 0 || destinationAddress % sizeof(ulong) != 0)))
        {
            return ReturnPointer(ctx, 0);
        }

        var packetAddressValue = interrupt == 4 ? 0UL : destinationAddress;
        var packetData = interrupt == 4 ? 0UL : data;

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RReleaseMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, action | (cachePolicy << 8)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                gcrControl | (dataSelection << 16) | (interrupt << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)packetAddressValue) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(packetAddressValue >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)packetData) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)(packetData >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 28, interruptContextId & 0x07FF_FFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_release_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"action=0x{action:X2} gcr=0x{gcrControl:X4} dst=0x{destinationAddress:X16} data_sel={dataSelection} data=0x{data:X16}");
        if (interrupt is 0 or 2 or 3 && dataSelection != 0)
        {
            TrackCbReleaseMemTarget(ctx, commandBufferAddress, destinationAddress);
        }
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
        Nid = "FuVbkyKlf+s",
        ExportName = "sceAgcCbCondWriteGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbCondWriteGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 9u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "7toV+elXqNM",
        ExportName = "sceAgcCbCondWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbCondWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var compareFunction = (uint)ctx[CpuRegister.Rsi];
        var writeSpace = (uint)ctx[CpuRegister.Rdx];
        var writeAddress = ctx[CpuRegister.Rcx];
        var writeValue = (uint)ctx[CpuRegister.R8];
        var readAddress = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt32(ctx, stackAddress + sizeof(ulong), out var reference) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var mask) ||
            commandBufferAddress == 0 ||
            compareFunction > 6 ||
            writeSpace is not 1 and not 2 ||
            readAddress == 0 ||
            (writeSpace == 1 &&
             (writeAddress == 0 || writeAddress % sizeof(uint) != 0)) ||
            readAddress % sizeof(uint) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var control = 0x10u |
                      compareFunction |
                      (writeSpace == 1 ? 0x100u : 0u);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 9, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(9, ItCondWrite, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)readAddress & ~0x3u) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(readAddress >> 32) & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 16, reference) ||
            !TryWriteUInt32(ctx, commandAddress + 20, mask) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)writeAddress & ~0x3u) ||
            !TryWriteUInt32(ctx, commandAddress + 28, (uint)(writeAddress >> 32) & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 32, writeValue))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_cond_write buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} compare={compareFunction} " +
            $"space={writeSpace} read=0x{readAddress:X16} " +
            $"ref=0x{reference:X8} mask=0x{mask:X8} " +
            $"write=0x{writeAddress:X16} data=0x{writeValue:X8}");
        return ReturnPointer(ctx, commandAddress);
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
            !IsValidWaitOperation(operation) ||
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

        var publicationGeneration = GpuWaitRegistry.BeginSubmission(ctx.Memory);
        state.PendingSubmissions.Enqueue(new SubmittedDcbState.PendingSubmission(
            commandAddress,
            dwordCount,
            submissionId,
            tracePackets,
            indexSnapshots,
            vertexSnapshots,
            publicationGeneration));
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
            GpuWaitRegistry.EndSubmission(
                ctx.Memory,
                state.ActiveSubmissionPublicationGeneration);
            state.ActiveSubmissionPublicationGeneration = 0;
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
            state.ActiveCompletionState = new SubmittedCompletionState();
            state.ActiveSubmissionPublicationGeneration =
                submission.PublicationGeneration;
            state.ActiveIndexSnapshots = submission.IndexSnapshots;
            state.ActiveVertexSnapshots = submission.VertexSnapshots;
            state.RingChunkBase = state.IsForceSubmittedRing ? 0 : submission.CommandAddress;
            state.FollowedChunkAdvance = false;
            state.IndirectCallReturn = null;
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
            GpuWaitRegistry.EndSubmission(
                ctx.Memory,
                state.ActiveSubmissionPublicationGeneration);
            state.ActiveSubmissionPublicationGeneration = 0;
            state.ActiveIndexSnapshots = null;
            state.ActiveVertexSnapshots = null;
            NotifySubmittedDcbCompleted(gpuState, state, submission.SubmissionId);
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
                if (state.IndirectCallReturn is not { } returnTarget)
                {
                    return false;
                }

                state.IndirectCallReturn = null;
                commandAddress = returnTarget.Address;
                dwordCount = returnTarget.Dwords;
                state.RingChunkBase = returnTarget.RingChunkBase;
                continue;
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
                    var jumpMode = (chainDwords >> 20) & 0x1u;
                    if (jumpMode == 0 && offset + length < dwordCount)
                    {
                        if (state.IndirectCallReturn is not null)
                        {
                            StopSubmittedQueue(
                                ctx,
                                state,
                                currentAddress,
                                ItIndirectBuffer,
                                "nested indirect calls are not supported by AGC");
                            return true;
                        }

                        state.IndirectCallReturn = (
                            currentAddress + ((ulong)length * sizeof(uint)),
                            dwordCount - (offset + length),
                            state.RingChunkBase);
                    }

                    state.PendingChainAddress = chainAddress;
                    state.PendingChainDwords = chainLength;
                    state.RingChunkBase = chainAddress;
                    TraceAgc(
                        $"agc.dcb_chain queue={state.QueueName} " +
                        $"submission={state.ActiveSubmissionId} " +
                        $"packet=0x{currentAddress:X16} " +
                        $"mode={(jumpMode == 0 ? "call" : "chain")} " +
                        $"target=0x{chainAddress:X16} dwords={chainLength}");

                    // A call keeps the parent continuation. A chain replaces it.
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

            if (op == ItCondWrite && length >= 9)
            {
                ApplySubmittedCondWrite(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItAtomicMem &&
                HandleSubmittedAtomicMem(
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
        op is ItAtomicMem or ItMemSemaphore or ItCopyData or ItCondWrite ||
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
                    $"gcr=0x{acquire.GcrControl.Raw:X8}");
            }

            return;
        }

        var size = acquire.CoversAllGuestMemory ? ulong.MaxValue : acquire.SizeBytes;
        NotePendingAcquireInvalidation(
            state,
            acquire.BaseAddress,
            size,
            acquire.Semantics,
            acquire.CbDbControl,
            acquire.GcrControl.Raw);

        if (tracePacket)
        {
            TraceAgc(
                $"agc.acquire_mem_coalesce queue={state.QueueName} " +
                $"submission={state.ActiveSubmissionId} packet=0x{packetAddress:X16} " +
                $"engine={acquire.Engine} cbdb=0x{acquire.CbDbControl:X8} " +
                $"base=0x{acquire.BaseAddress:X16} size=0x{acquire.SizeBytes:X16} " +
                $"scope={(acquire.CoversAllGuestMemory ? "all" : "range")} " +
                $"poll={acquire.PollInterval} gcr=0x{acquire.GcrControl.Raw:X8} " +
                $"domains={acquire.Semantics.Domains} actions={acquire.Semantics.Actions} " +
                $"pending_base=0x{state.PendingAcquireBase:X16} " +
                $"pending_size=0x{state.PendingAcquireSize:X16}");
        }
    }

    private static void NotePendingAcquireInvalidation(
        SubmittedDcbState state,
        ulong baseAddress,
        ulong sizeBytes,
        AgcGpuCacheSemantics semantics,
        uint cbDbControl,
        uint gcrControl)
    {
        state.PendingAcquireDomains |= semantics.Domains;
        state.PendingAcquireActions |= semantics.Actions;
        state.PendingAcquireCbDbControl |= cbDbControl;
        state.PendingAcquireGcrControl |= gcrControl;
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
        var domains = state.PendingAcquireDomains;
        var actions = state.PendingAcquireActions;
        var cbDbControl = state.PendingAcquireCbDbControl;
        var gcrControl = state.PendingAcquireGcrControl;
        state.PendingAcquireInvalidation = false;
        state.PendingAcquireBase = 0;
        state.PendingAcquireSize = 0;
        state.PendingAcquireDomains = AgcGpuCacheDomain.None;
        state.PendingAcquireActions = AgcGpuCacheAction.None;
        state.PendingAcquireCbDbControl = 0;
        state.PendingAcquireGcrControl = 0;

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

        var semantics = new AgcGpuCacheSemantics(
            domains,
            actions,
            CoversAllMemory: sizeBytes == ulong.MaxValue);
        var operation = semantics.ToGuestOperation(
            baseAddress,
            sizeBytes,
            cbDbControl,
            gcrControl);
        if (tracePacket || _logGpuCacheOperations)
        {
            TraceUniqueGpuCacheOperation(
                "acquire_mem_flush",
                state,
                cbDbControl,
                gcrControl,
                baseAddress,
                sizeBytes,
                semantics,
                nextConsumer: "submission_boundary");
        }

        if (!_gpuCacheHostEffectsEnabled)
        {
            if (Interlocked.Increment(ref _gpuCacheHostEffectsDisabledReportCount) == 1)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] AGC host cache effects disabled. " +
                    "Packet decode and logging remain active.");
            }

            return;
        }

        var sequence = GuestGpu.Current.SubmitGuestCacheOperation(
            operation,
            ApplyAcquire,
            debugName);
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
            GcrControl: new AcquireMemGcrControl(gcrControl & 0x7FFFFu));
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
        state.ConditionalWaitEnabled = false;
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
        GpuWaitRegistry.VirtualLabelPublication publication = default;

        void Publish(GuestGpuLabelDependency dependency)
        {
            publication = GpuWaitRegistry.RecordVirtualProducedRange(
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
            if (!GpuWaitRegistry.IsCurrentVirtualPublication(
                    ctx.Memory,
                    publication))
            {
                return;
            }

            var wroteAll = true;
            for (var index = 0; index < values.Length; index++)
            {
                wroteAll &= TryWriteUInt32(
                    ctx,
                    destinationAddress + ((ulong)index * sizeof(uint)),
                    values[index]);
            }

            if (wroteAll)
            {
                // Invalidate after the full write. A parser that drops its
                // cached window must only see the complete new value.
                InvalidateDcbWindowIfOverlaps(destinationAddress, byteCount);
                if (_gpuLabelHostMirrorRetirementEnabled)
                {
                    GpuWaitRegistry.RetireVirtualPublication(
                        ctx.Memory,
                        publication);
                }
            }
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


    private static void ValidateSubmittedQueueAndReleaseMemDecoders()
    {
        var nggRegisters = new Dictionary<uint, uint>
        {
            [GsUserDataRegister - 1] = 3u << 1,
        };
        System.Diagnostics.Debug.Assert(
            SelectExportUserDataRegister(nggRegisters) == GsUserDataRegister);

        var queue = new SubmittedDcbState();
        queue.PendingSubmissions.Enqueue(new(0x1000, 8, 11, false, null, null, 0));
        queue.PendingSubmissions.Enqueue(new(0x2000, 16, 12, true, null, null, 0));
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
        queue.PendingSubmissions.Enqueue(new(0x3000, 2, 8, false, null, null, 0));
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

        var waitOperation = DecodeWaitOperation(controlValue, is64Bit);
        if (!IsValidWaitOperation(waitOperation))
        {
            TraceAgc(
                $"agc.dcb.wait_reject addr=0x{waitAddress:X16} " +
                $"operation={waitOperation} bits={(is64Bit ? 64 : 32)} " +
                $"standard={isStandard} packet=0x{packetAddress:X16} " +
                "reason=invalid-operation");
            return false;
        }

        if (!ShouldExecuteWaitOperation(waitOperation, state.ConditionalWaitEnabled))
        {
            if (tracePacket)
            {
                TraceAgc(
                    $"agc.dcb.conditional_wait_skipped addr=0x{waitAddress:X16} " +
                    $"packet=0x{packetAddress:X16} scratch=0");
            }

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
            SubmissionPublicationGeneration =
                state.ActiveSubmissionPublicationGeneration,
            RegisteredTicks = System.Diagnostics.Stopwatch.GetTimestamp(),
            State = state,
        };

        ulong? ReadGuestLabel(ulong address, bool read64Bit)
        {
            if (read64Bit)
            {
                return TryReadUInt64(ctx, address, out var value64)
                    ? value64
                    : null;
            }

            return TryReadUInt32(ctx, address, out var value32)
                ? value32
                : null;
        }

        if (!_gpuWaitSuspendEnabled)
        {
            var waitCachePolicy = (controlValue >> 25) & 0x3u;
            ulong currentValue = 0;
            GuestGpuLabelDependency currentDependency = default;
            var hasCurrent = waitCachePolicy <= 2 &&
                             GpuWaitRegistry.TryReadVirtual(
                                 ctx.Memory,
                                 waitAddress,
                                 is64Bit,
                                 out currentValue,
                                 out currentDependency,
                                 waitCachePolicy);
            if (!hasCurrent)
            {
                var current = ReadGuestLabel(waitAddress, is64Bit);
                hasCurrent = current.HasValue;
                currentValue = current.GetValueOrDefault();
            }

            TraceSubmittedWait(
                waitAddress,
                currentValue,
                mask,
                reference,
                compareFunction,
                is64Bit ? 64 : 32,
                tracePacket);
            if (hasCurrent &&
                GpuWaitRegistry.Compare(waiter, currentValue) &&
                GpuWaitRegistry.IsLabelFresh(ctx.Memory, waitAddress))
            {
                GuestGpu.Current.RequireGpuLabelDependency(currentDependency);
                return false;
            }

            if (hasCurrent)
            {
                ForceSatisfyGpuWait(ctx, waiter, currentValue);
            }

            return false;
        }

        var registration = GpuWaitRegistry.RegisterIfUnsatisfied(
            waiter,
            ReadGuestLabel,
            out var observedValue,
            out var observedDependency);
        TraceSubmittedWait(
            waitAddress,
            observedValue,
            mask,
            reference,
            compareFunction,
            is64Bit ? 64 : 32,
            tracePacket);
        if (registration is GpuWaitRegistry.WaitRegistrationResult.Satisfied or
            GpuWaitRegistry.WaitRegistrationResult.SatisfiedByHistory)
        {
            GuestGpu.Current.RequireGpuLabelDependency(observedDependency);
            return false;
        }

        if (registration == GpuWaitRegistry.WaitRegistrationResult.Unreadable)
        {
            return false; // cannot evaluate the label — do not stall the DCB
        }

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
            observedValue);
        if (tracePacket)
        {
            TraceAgc(
                $"agc.dcb.suspended addr=0x{waitAddress:X16} ref=0x{reference:X16} " +
                $"mask=0x{mask:X16} cur=0x{observedValue:X16} cmp={compareFunction}");
        }

        return true;
    }

    private static void ApplySubmittedCondWrite(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong packetAddress,
        bool tracePacket)
    {
        if (!TryReadUInt32(ctx, packetAddress + 4, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var readLow) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var readHigh) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var reference) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var mask) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var writeLow) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var writeHigh) ||
            !TryReadUInt32(ctx, packetAddress + 32, out var writeValue))
        {
            return;
        }

        var compareFunction = control & 0x7u;
        var pollsMemory = (control & (1u << 4)) != 0;
        var writesMemory = (control & (1u << 8)) != 0;
        var readAddress = ((ulong)(readHigh & 0xFFFFu) << 32) |
                          (readLow & 0xFFFF_FFFCu);
        var writeAddress = ((ulong)(writeHigh & 0xFFFFu) << 32) |
                           (writeLow & 0xFFFF_FFFCu);
        if (!pollsMemory ||
            compareFunction == 7 ||
            readAddress == 0 ||
            (writesMemory && writeAddress == 0))
        {
            TraceAgc(
                $"agc.dcb.cond_write_reject packet=0x{packetAddress:X16} " +
                $"compare={compareFunction} poll_memory={pollsMemory} " +
                $"read=0x{readAddress:X16}");
            return;
        }

        var readSucceeded = false;
        var conditionPassed = false;
        var wroteData = false;
        var observed = 0u;

        void TraceResult()
        {
            if (!tracePacket)
            {
                return;
            }

            TraceAgc(
                $"agc.dcb.cond_write packet=0x{packetAddress:X16} " +
                $"read=0x{readAddress:X16} value=0x{observed:X8} " +
                $"ref=0x{reference:X8} mask=0x{mask:X8} compare={compareFunction} " +
                $"read_ok={readSucceeded} pass={conditionPassed} " +
                $"space={(writesMemory ? "gl2" : "scratch")} " +
                $"write=0x{writeAddress:X16} data=0x{writeValue:X8} wrote={wroteData}");
        }

        void ApplyCondition()
        {
            readSucceeded = TryReadLiveUInt32(ctx, readAddress, out observed);
            conditionPassed = readSucceeded &&
                CompareConditionalValue(observed, reference, mask, compareFunction);
            if (!conditionPassed)
            {
                TraceResult();
                return;
            }

            if (writesMemory)
            {
                InvalidateDcbWindowIfOverlaps(writeAddress, sizeof(uint));
                wroteData = TryWriteUInt32(ctx, writeAddress, writeValue);
                if (wroteData)
                {
                    GpuWaitRegistry.RecordProduced(
                        ctx.Memory,
                        writeAddress,
                        writeValue);
                }

                TraceResult();
                return;
            }

            state.ConditionalWaitEnabled = writeValue != 0;
            TraceResult();
        }

        if (writesMemory)
        {
            SubmitOrderedGpuSideEffect(
                ctx,
                gpuState,
                state,
                ApplyCondition,
                $"cond_write dst=0x{writeAddress:X16}",
                packetAddress,
                writeAddress,
                sizeof(uint));
            return;
        }

        // The parser needs the CP scratch result before it handles the next wait.
        var sequence = GuestGpu.Current.SubmitOrderedGuestAction(
            ApplyCondition,
            $"cond_write scratch read=0x{readAddress:X16}");
        if (sequence == 0)
        {
            ApplyCondition();
        }
        else if (!GuestGpu.Current.WaitForGuestWork(sequence))
        {
            TraceAgc(
                $"agc.dcb.cond_write_wait_failed packet=0x{packetAddress:X16} " +
                $"sequence={sequence}");
        }
    }

    internal static bool CompareConditionalValue(
        uint value,
        uint reference,
        uint mask,
        uint compareFunction)
    {
        var maskedValue = value & mask;
        return compareFunction switch
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
            GpuWaitRegistry.EndSubmission(
                waiter.Memory ?? ctx.Memory,
                state.ActiveSubmissionPublicationGeneration);
            state.ActiveSubmissionPublicationGeneration = 0;
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
        GpuWaitRegistry.EndSubmission(
            waiter.Memory ?? ctx.Memory,
            state.ActiveSubmissionPublicationGeneration);
        state.ActiveSubmissionPublicationGeneration = 0;
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
        if (!TryReadUInt32(ctx, packetAddress + 4, out var releaseControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var interruptContextId))
        {
            return;
        }

        var cacheControl = DecodeStandardReleaseMemCacheControl(releaseControl);
        var cacheSemantics = cacheControl.GcrControl.ToSemantics();
        var (destination, dataSelection) = DecodeStandardReleaseMemControl(control);
        var interruptSelection = (control >> 24) & 0x7u;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var isAsyncCompute = state.QueueName.StartsWith("acb.", StringComparison.Ordinal);
        var staticDecision = EvaluateQueuedInterrupt(
            interruptSelection,
            isAsyncCompute,
            dataSelection,
            conditionReadable: false,
            conditionValue: 0,
            data);
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 or 4 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        var expectsGuestMemoryWrite = staticDecision.WritesData &&
                                      destination is 0 or 1 &&
                                      writeLength != 0;
        var writesGuestMemory = expectsGuestMemoryWrite && destinationAddress != 0;
        var submissionCompletionState = state.ActiveCompletionState;

        if (tracePacket || _logGpuCacheOperations)
        {
            TraceUniqueGpuCacheOperation(
                "release_mem_standard",
                state,
                cbDbAction: 0,
                cacheControl.GcrControl.Raw,
                baseAddress: 0,
                sizeBytes: ulong.MaxValue,
                cacheSemantics);
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                var conditionReadable = TryReadQueuedInterruptCondition(
                    ctx,
                    interruptSelection,
                    destinationAddress,
                    out var conditionValue);
                var interruptDecision = EvaluateQueuedInterrupt(
                    interruptSelection,
                    isAsyncCompute,
                    dataSelection,
                    conditionReadable,
                    conditionValue,
                    data);
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
                        ctx.Memory,
                        destinationAddress,
                        dataSelection == 1 ? dataLo : data,
                        hasHighDword: dataSelection == 2);
                }
                else if (expectsGuestMemoryWrite && !wroteData && dataSelection is 1 or 2)
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
                var wokenQueues = interruptDecision.RaisesInterrupt
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        interruptContextId & 0x07FF_FFFFu)
                    : 0;
                if (interruptDecision.RaisesInterrupt &&
                    submissionCompletionState is not null)
                {
                    submissionCompletionState.RaisedQueuedInterrupt = true;
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem_standard dst_sel={destination} " +
                        $"dst=0x{destinationAddress:X16} data_sel={dataSelection} " +
                        $"data=0x{data:X16} wrote={wroteData} " +
                        $"event=0x{cacheControl.EventType:X2} " +
                        $"event_index={cacheControl.EventIndex} " +
                        $"cache={cacheControl.CachePolicy} " +
                        $"gcr=0x{cacheControl.GcrControl.Raw:X4} " +
                        $"int={interruptSelection} condition_read={conditionReadable} " +
                        $"condition=0x{conditionValue:X16} woken={wokenQueues}");
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
        if (!TryReadUInt32(ctx, packetAddress + 4, out var actionControl) ||
            !TryReadUInt32(ctx, packetAddress + 8, out var control) ||
            !TryReadUInt32(ctx, packetAddress + 12, out var destinationLo) ||
            !TryReadUInt32(ctx, packetAddress + 16, out var destinationHi) ||
            !TryReadUInt32(ctx, packetAddress + 20, out var dataLo) ||
            !TryReadUInt32(ctx, packetAddress + 24, out var dataHi) ||
            !TryReadUInt32(ctx, packetAddress + 28, out var interruptContextId))
        {
            return;
        }

        var cacheControl = DecodeAgcReleaseMemCacheControl(actionControl, control);
        var gcrSemantics = cacheControl.GcrControl.ToSemantics();
        var actionSemantics = cacheControl.ActionSemantics;
        var cacheSemantics = new AgcGpuCacheSemantics(
            gcrSemantics.Domains | actionSemantics.Domains,
            gcrSemantics.Actions | actionSemantics.Actions,
            gcrSemantics.CoversAllMemory || actionSemantics.CoversAllMemory);
        var dataSelection = (control >> 16) & 0xFFu;
        var interrupt = (control >> 24) & 0xFFu;
        var destinationAddress = ((ulong)destinationHi << 32) | destinationLo;
        var data = ((ulong)dataHi << 32) | dataLo;
        var isAsyncCompute = state.QueueName.StartsWith("acb.", StringComparison.Ordinal);
        var staticDecision = EvaluateQueuedInterrupt(
            interrupt,
            isAsyncCompute,
            dataSelection,
            conditionReadable: false,
            conditionValue: 0,
            data);
        var writeLength = dataSelection switch
        {
            1 => (ulong)sizeof(uint),
            2 or 3 => (ulong)sizeof(ulong),
            _ => 0UL,
        };
        var expectsGuestMemoryWrite = staticDecision.WritesData && writeLength != 0;
        var writesGuestMemory = expectsGuestMemoryWrite && destinationAddress != 0;
        var submissionCompletionState = state.ActiveCompletionState;
        if (tracePacket || _logGpuCacheOperations)
        {
            TraceUniqueGpuCacheOperation(
                "release_mem",
                state,
                cacheControl.RawAction,
                cacheControl.GcrControl.Raw,
                baseAddress: 0,
                sizeBytes: ulong.MaxValue,
                cacheSemantics,
                completionAction: cacheControl.ActionName);
        }

        SubmitOrderedGpuSideEffect(
            ctx,
            gpuState,
            state,
            () =>
            {
                var conditionReadable = TryReadQueuedInterruptCondition(
                    ctx,
                    interrupt,
                    destinationAddress,
                    out var conditionValue);
                var interruptDecision = EvaluateQueuedInterrupt(
                    interrupt,
                    isAsyncCompute,
                    dataSelection,
                    conditionReadable,
                    conditionValue,
                    data);
                if (writesGuestMemory)
                {
                    InvalidateDcbWindowIfOverlaps(destinationAddress, writeLength);
                }

                var wroteData = writesGuestMemory && dataSelection switch
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
                        ctx.Memory,
                        destinationAddress,
                        dataSelection == 1 ? dataLo : data,
                        hasHighDword: dataSelection == 2);
                }
                else if (expectsGuestMemoryWrite && !wroteData && dataSelection is 1 or 2)
                {
                    // A label write that fails is not a benign miss: this packet
                    // is the producer a suspended WAIT_REG_MEM is waiting for, and
                    // RecordProduced above is skipped, so the deadlock breaker has
                    // no value to replay either. The queue then never resumes.
                    // Never let that happen quietly.
                    ReportLabelWriteFailure("release_mem", destinationAddress, data, dataSelection);
                }

                // Same interrupt gating as the standard form above.
                var wokenQueues = interruptDecision.RaisesInterrupt
                    ? KernelEventQueueCompatExports.TriggerRegisteredEventsByFilter(
                        KernelEventQueueCompatExports.KernelEventFilterGraphics,
                        interruptContextId & 0x07FF_FFFFu)
                    : 0;
                if (interruptDecision.RaisesInterrupt &&
                    submissionCompletionState is not null)
                {
                    submissionCompletionState.RaisedQueuedInterrupt = true;
                }

                if (tracePacket)
                {
                    TraceAgc(
                        $"agc.dcb.release_mem dst=0x{destinationAddress:X16} " +
                        $"data_sel={dataSelection} data=0x{data:X16} wrote={wroteData} " +
                        $"completion={cacheControl.ActionName} " +
                        $"action=0x{cacheControl.RawAction:X2} " +
                        $"cache={cacheControl.CachePolicy} " +
                        $"gcr=0x{cacheControl.GcrControl.Raw:X4} " +
                        $"int={interrupt} condition_read={conditionReadable} " +
                        $"condition=0x{conditionValue:X16} woken={wokenQueues}");
                }
            },
            $"release_mem dst=0x{destinationAddress:X16} data=0x{data:X16}",
            packetAddress,
            writesGuestMemory ? destinationAddress : 0,
            writesGuestMemory ? writeLength : 0);
    }

    private static void ApplySubmittedRegisters(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        uint register)
    {
        if (op is ItSetShReg or ItSetContextReg or ItSetUconfigReg or ItSetUconfigRegIndex)
        {
            if (packetLength < 3 ||
                !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var startRegister))
            {
                return;
            }

            if (op == ItSetUconfigRegIndex)
            {
                startRegister &= 0x0FFF_FFFFu;
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
                if (op is ItSetUconfigReg or ItSetUconfigRegIndex)
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

    internal static bool TryGetGraphicsIndexSizeForTests(
        CpuContext ctx,
        out uint indexSize)
    {
        indexSize = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            indexSize = gpuState.Graphics.IndexSize;
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

    internal static GuestRenderState CreateDepthTargetRenderState(
        IReadOnlyDictionary<uint, uint> registers,
        GuestDepthTarget depthTarget)
    {
        var target = new RenderTargetDescriptor(
            Slot: 0,
            Address: 0,
            depthTarget.Width,
            depthTarget.Height,
            Format: 0,
            NumberType: 0,
            ComponentSwap: 0,
            TileMode: 0);
        return CreateRenderState(registers, target) with
        {
            Blends = [GuestBlendState.Default with { WriteMask = 0 }],
        };
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
        out GuestDrawTexture texture,
        int snapshotAttempt = 0)
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
            var storageSnapshot = default(SharpEmu.HLE.GuestImageWriteTracker.ReadSnapshot);
            if (descriptor.Address != 0 && !uploadKnown)
            {
                // Storage images can be pre-populated in tiled guest memory
                // just like sampled images. Reading only the logical linear
                // byte count both truncates 64 KiB swizzle blocks and uploads
                // tiled bytes as scanlines. Read the full physical footprint
                // and run the same AddrLib-derived detile path used below for
                // sampled textures before seeding the Vulkan image.
                storageSnapshot = SharpEmu.HLE.GuestImageWriteTracker.BeginReadSnapshot(
                    descriptor.Address,
                    checked(baseMipByteOffset + physicalSourceByteCount),
                    source: "agc.storage-image-snapshot");
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

                if (readSucceeded &&
                    !SharpEmu.HLE.GuestImageWriteTracker.IsReadSnapshotStable(storageSnapshot))
                {
                    if (snapshotAttempt < 2)
                    {
                        return TryCreateGuestDrawTexture(
                            ctx,
                            descriptor,
                            isStorage,
                            mipLevel,
                            samplerDescriptor,
                            isArrayed,
                            out texture,
                            snapshotAttempt + 1);
                    }

                    initialPixels = [];
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
                WriteGeneration: storageSnapshot.Active ? storageSnapshot.Generation : -1,
                Type: descriptor.Type,
                Depth: textureDepth,
                SourceByteCount: checked(baseMipByteOffset + physicalSourceByteCount),
                CpuSnapshotStable:
                    !readSucceeded ||
                    SharpEmu.HLE.GuestImageWriteTracker.IsReadSnapshotStable(storageSnapshot));
            return true;
        }

        // When the presenter already holds this exact texture identity in
        // its cache, the texel copy below would be discarded on arrival; for
        // scenes that sample large textures every draw this copy dominated
        // CPU time (Dead Cells menus). The cache records the write generation
        // that supplied its pixels. A later native or managed CPU write bumps
        // the tracker generation and makes IsTextureContentCached return false.
        // CPU-updated guest Bink planes are handled by the upload-known gate
        // above when the tracker cannot observe native writes.
        var sampler = ToGuestSampler(samplerDescriptor);
        // Capture the generation associated with these texels. The presenter
        // records it after upload, and a later tracked write makes the cache
        // generation differ so the next bind sends fresh texels.
        var hasWriteGeneration =
            SharpEmu.HLE.GuestImageWriteTracker.TryGetWriteGeneration(
                descriptor.Address,
                out var writeGeneration);
        if (!_textureCopySkipDisabled &&
            descriptor.Address != 0 &&
            GuestGpu.Current.IsTextureContentCached(
                new TextureCacheLookupIdentity(
                    new TextureContentIdentity(
                        descriptor.Address,
                        descriptor.Width,
                        descriptor.Height,
                        descriptor.Format,
                        descriptor.NumberType,
                        descriptor.DstSelect,
                        descriptor.TileMode,
                        sourceWidth,
                        isArrayed,
                        arrayUploadLayers,
                        descriptor.Type,
                        textureDepth,
                        descriptor.ResourceMipLevels),
                    sampler)))
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

        var trackedSourceByteCount = wantsArrayUpload
            ? checked(chainSliceBytes * arrayUploadLayers)
            : checked(baseMipByteOffset + physicalSourceByteCount);
        var readSnapshot = SharpEmu.HLE.GuestImageWriteTracker.BeginReadSnapshot(
            descriptor.Address,
            trackedSourceByteCount,
            source: "agc.sampled-texture-snapshot");
        if (readSnapshot.Active)
        {
            hasWriteGeneration = true;
            writeGeneration = readSnapshot.Generation;
        }

        if (wantsArrayUpload)
        {
            var arrayLayers = arrayUploadLayers;
            var layerBytes = checked((int)sourceSliceByteCount);
            var totalBytes = (long)layerBytes * arrayLayers;

            if (hasElementLayout && resourceMipLevels > 1 &&
                TryCreateTiledArrayMipChain(
                    ctx,
                    descriptor,
                    sampler,
                    arrayLayers,
                    elementsWide,
                    elementsHigh,
                    bytesPerElement,
                    hasWriteGeneration ? writeGeneration : -1,
                    out texture))
            {
                NoteSampledAddress(descriptor.Address, descriptor.Format, descriptor.NumberType);
                return FinalizeGuestTextureSnapshot(
                    ctx,
                    descriptor,
                    isStorage,
                    mipLevel,
                    samplerDescriptor,
                    isArrayed,
                    readSnapshot,
                    snapshotAttempt,
                    texture,
                    out texture);
            }

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
                            Detile: gpuArrayParams,
                            SourceByteCount: trackedSourceByteCount);
                        return FinalizeGuestTextureSnapshot(
                            ctx,
                            descriptor,
                            isStorage,
                            mipLevel,
                            samplerDescriptor,
                            isArrayed,
                            readSnapshot,
                            snapshotAttempt,
                            texture,
                            out texture);
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
                        WriteGeneration: hasWriteGeneration ? writeGeneration : -1,
                        ArrayedView: true,
                        ArrayLayers: arrayLayers,
                        Type: descriptor.Type,
                        Depth: textureDepth,
                        SourceByteCount: checked(chainSliceBytes * arrayLayers));
                    return FinalizeGuestTextureSnapshot(
                        ctx,
                        descriptor,
                        isStorage,
                        mipLevel,
                        samplerDescriptor,
                        isArrayed,
                        readSnapshot,
                        snapshotAttempt,
                        texture,
                        out texture);
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
                    Detile: gpuDetileParams,
                    SourceByteCount: (ulong)source.Length);
                return FinalizeGuestTextureSnapshot(
                    ctx,
                    descriptor,
                    isStorage,
                    mipLevel,
                    samplerDescriptor,
                    isArrayed,
                    readSnapshot,
                    snapshotAttempt,
                    texture,
                    out texture);
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
            Depth: textureDepth,
            SourceByteCount: physicalSourceByteCount);
        return FinalizeGuestTextureSnapshot(
            ctx,
            descriptor,
            isStorage,
            mipLevel,
            samplerDescriptor,
            isArrayed,
            readSnapshot,
            snapshotAttempt,
            texture,
            out texture);
    }

    private static bool FinalizeGuestTextureSnapshot(
        CpuContext ctx,
        TextureDescriptor descriptor,
        bool isStorage,
        uint mipLevel,
        IReadOnlyList<uint> samplerDescriptor,
        bool isArrayed,
        SharpEmu.HLE.GuestImageWriteTracker.ReadSnapshot snapshot,
        int snapshotAttempt,
        GuestDrawTexture candidate,
        out GuestDrawTexture texture)
    {
        if (SharpEmu.HLE.GuestImageWriteTracker.IsReadSnapshotStable(snapshot))
        {
            texture = snapshot.Active
                ? candidate with
                {
                    WriteGeneration = snapshot.Generation,
                    CpuSnapshotStable = true,
                }
                : candidate;
            return true;
        }

        if (snapshotAttempt < 2)
        {
            return TryCreateGuestDrawTexture(
                ctx,
                descriptor,
                isStorage,
                mipLevel,
                samplerDescriptor,
                isArrayed,
                out texture,
                snapshotAttempt + 1);
        }

        // Keep the descriptor but withhold bytes that crossed a guest write.
        // The backend can retain an older cached image and retry on a later
        // bind without publishing a torn atlas or video plane.
        texture = candidate with
        {
            RgbaPixels = [],
            TiledSource = null,
            MipUploads = null,
            WriteGeneration = -1,
            CpuSnapshotStable = false,
        };
        return true;
    }

    private static bool TryCreateTiledArrayMipChain(
        CpuContext ctx,
        TextureDescriptor descriptor,
        GuestSampler sampler,
        uint arrayLayers,
        int elementsWide,
        int elementsHigh,
        int bytesPerElement,
        long writeGeneration,
        out GuestDrawTexture texture)
    {
        texture = default!;
        if (!GnmTiling.TryGetMipChainPlacement(
                descriptor.TileMode,
                elementsWide,
                elementsHigh,
                bytesPerElement,
                descriptor.ResourceMipLevels,
                out var placements,
                out var chainSliceBytes) ||
            chainSliceBytes == 0 ||
            chainSliceBytes > int.MaxValue)
        {
            return false;
        }

        var uploads = new GuestTextureMipUpload[placements.Length];
        var mipByteCounts = new ulong[placements.Length];
        ulong totalLinearBytes = 0;
        for (var mip = 0; mip < placements.Length; mip++)
        {
            var width = Math.Max(descriptor.Width >> mip, 1u);
            var height = Math.Max(descriptor.Height >> mip, 1u);
            var mipBytes = GetTextureByteCount(descriptor.Format, width, height);
            if (mipBytes == 0 || mipBytes > int.MaxValue)
            {
                return false;
            }

            uploads[mip] = new GuestTextureMipUpload(
                totalLinearBytes,
                (uint)mip,
                width,
                height,
                width);
            mipByteCounts[mip] = mipBytes;
            try
            {
                totalLinearBytes = checked(totalLinearBytes + mipBytes * arrayLayers);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (totalLinearBytes > int.MaxValue)
        {
            return false;
        }

        var linear = new byte[(int)totalLinearBytes];
        var tiledSlice = new byte[(int)chainSliceBytes];
        byte[]? tailLinear = null;
        var tailBlockWidth = 0;
        var tailBlockHeight = 0;
        if (placements.Any(static placement => placement.InMipTail))
        {
            if (!GnmTiling.TryGetBlockElementDimensions(
                    descriptor.TileMode,
                    bytesPerElement,
                    out tailBlockWidth,
                    out tailBlockHeight))
            {
                return false;
            }

            tailLinear = new byte[checked(tailBlockWidth * tailBlockHeight * bytesPerElement)];
        }

        for (var layer = 0u; layer < arrayLayers; layer++)
        {
            if (!ctx.Memory.TryRead(
                    descriptor.Address + layer * chainSliceBytes,
                    tiledSlice))
            {
                return false;
            }

            if (tailLinear is not null &&
                !GnmTiling.TryDetile(
                    tiledSlice.AsSpan(0, (int)placements.First(static placement => placement.InMipTail).ByteCount),
                    tailLinear,
                    descriptor.TileMode,
                    tailBlockWidth,
                    tailBlockHeight,
                    bytesPerElement))
            {
                return false;
            }

            for (var mip = 0; mip < placements.Length; mip++)
            {
                var placement = placements[mip];
                var mipBytes = mipByteCounts[mip];
                var destinationOffset = checked(
                    uploads[mip].BufferOffset + mipBytes * layer);
                var destination = linear.AsSpan((int)destinationOffset, (int)mipBytes);
                if (!placement.InMipTail)
                {
                    if (!GnmTiling.TryDetile(
                            tiledSlice.AsSpan((int)placement.ByteOffset, (int)placement.ByteCount),
                            destination,
                            descriptor.TileMode,
                            placement.ElementsWide,
                            placement.ElementsHigh,
                            bytesPerElement))
                    {
                        return false;
                    }

                    continue;
                }

                var rowBytes = placement.ElementsWide * bytesPerElement;
                for (var y = 0; y < placement.ElementsHigh; y++)
                {
                    var sourceOffset = checked(
                        ((placement.TailElementY + y) * tailBlockWidth +
                         placement.TailElementX) * bytesPerElement);
                    tailLinear.AsSpan(sourceOffset, rowBytes)
                        .CopyTo(destination.Slice(y * rowBytes, rowBytes));
                }
            }
        }

        texture = new GuestDrawTexture(
            descriptor.Address,
            descriptor.Width,
            descriptor.Height,
            descriptor.Format,
            descriptor.NumberType,
            linear,
            IsFallback: false,
            IsStorage: false,
            MipLevels: descriptor.MipLevels,
            MipLevel: 0,
            BaseMipLevel: descriptor.ViewBaseLevel,
            ResourceMipLevels: descriptor.ResourceMipLevels,
            Pitch: descriptor.Width,
            TileMode: descriptor.TileMode,
            DstSelect: descriptor.DstSelect,
            Sampler: sampler,
            WriteGeneration: writeGeneration,
            ArrayedView: true,
            ArrayLayers: arrayLayers,
            Type: descriptor.Type,
            Depth: 1,
            MipUploads: uploads,
            SourceByteCount: checked(chainSliceBytes * arrayLayers));
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

    internal static bool IsValidWaitOperation(uint operation) =>
        operation is 0 or 1 or 4;

    internal static bool ShouldExecuteWaitOperation(
        uint operation,
        bool conditionalWaitEnabled) =>
        operation != 4 || conditionalWaitEnabled;

    internal static uint DecodeWaitOperation(uint control, bool is64Bit) =>
        is64Bit
            ? ((control >> 8) & 0x1u) | ((control >> 5) & 0x6u)
            : ((control >> 8) & 0x3u) | ((control >> 4) & 0xCu);

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

    private static bool TryReadLiveUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadLiveUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
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
