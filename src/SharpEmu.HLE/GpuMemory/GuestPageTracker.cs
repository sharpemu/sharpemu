// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GuestMemory;

namespace SharpEmu.HLE.GpuMemory;

public sealed class GuestPageTracker
{
    [ThreadStatic]
    private static UploadScratch? _availableUploadScratch;

    private sealed class UploadScratch
    {
        internal readonly List<TrackedRegion> Held = new();
        internal readonly List<(TrackedRegion Region, ulong Address, ulong Size)> Cleared = new();
    }

    private const ulong BlockBytes = TrackerLayout.BlockBytes;
    private const ulong PageBytes = TrackerLayout.PageBytes;

    [ThreadStatic]
    private static GuestPageTracker? _uploadOwner;

    private readonly PageGuard _pages;
    private readonly TrackedRegion?[] _regions = new TrackedRegion?[TrackerLayout.BlockCount];
    private readonly long[] _presentRegions = new long[(TrackerLayout.BlockCount + 63) / 64];
    private readonly object _regionGate = new();
    private readonly CpuDirtySummary _cpuDirtySummary = new();

    private static readonly ThreadCounter SweepBlocks = new();
    private static readonly ThreadCounter SweepBlocksClean = new();
    private static readonly ThreadCounter SweepBlocksTaken = new();
    private static readonly ThreadCounter SweepPagesTaken = new();
    private static readonly ThreadCounter EmulatorWritesKept = new();
    private static readonly ThreadCounter EmulatorWritesOverGpuData = new();
    private static readonly ThreadCounter GpuProtectionsDeferred = new();
    private static readonly ThreadCounter GpuProtectionFlushes = new();

    public static string TakeDirtyTrackingReport() => FormattableString.Invariant(
        $"sweep_blocks={SweepBlocks.Take()} sweep_clean={SweepBlocksClean.Take()} sweep_taken={SweepBlocksTaken.Take()} sweep_pages={SweepPagesTaken.Take()} ") +
        FormattableString.Invariant(
        $"emulator_kept={EmulatorWritesKept.Take()} emulator_over_gpu={EmulatorWritesOverGpuData.Take()} gpu_protect_deferred={GpuProtectionsDeferred.Take()} gpu_protect_flushes={GpuProtectionFlushes.Take()}");

    private readonly object _pendingGpuGate = new();
    private readonly object _applyGpuGate = new();
    private List<TrackedRegion> _pendingGpuProtections = [];
    private List<TrackedRegion> _applyingGpuProtections = [];

    public GuestPageTracker(PageGuard pages) => _pages = pages;

    public bool DefersGpuWriteProtection { get; set; }

    internal void QueueGpuProtection(TrackedRegion region)
    {
        GpuProtectionsDeferred.Increment();
        lock (_pendingGpuGate)
        {
            _pendingGpuProtections.Add(region);
        }
    }

    public void ApplyPendingGpuProtections()
    {
        lock (_applyGpuGate)
        {
            List<TrackedRegion> regions;
            lock (_pendingGpuGate)
            {
                if (_pendingGpuProtections.Count == 0)
                {
                    return;
                }

                regions = _pendingGpuProtections;
                _pendingGpuProtections = _applyingGpuProtections;
                _applyingGpuProtections = regions;
            }

            GpuProtectionFlushes.Increment();

            foreach (var region in regions)
            {
                if (region.Lock.IsHeldByCurrentThread)
                {
                    region.ApplyGpuProtection();
                    continue;
                }

                using var _ = region.Lock.Hold();
                region.ApplyGpuProtection();
            }

            regions.Clear();
        }
    }

    public bool HasCpuDirtyPages(ulong vaddr, ulong size)
    {
        RejectUploadCallbackReentry();
        if (IsKnownCpuClean(vaddr, size))
        {
            return false;
        }

        return VisitRegions(vaddr, size, create: true, (region, offset, bytes) =>
        {
            using var _ = region.Lock.Hold();
            return region.IsModified(WriteOrigin.Cpu, offset, bytes);
        });
    }

    public (ulong Address, ulong Size) ExtendCpuDirtyRun(ulong vaddr, ulong size, ulong lowerBound, ulong upperBound, bool preserveHotPages, int budgetPages)
    {
        ValidateRange(vaddr, size);
        var index = vaddr / BlockBytes;
        if (budgetPages <= 0 || (vaddr + size - 1) / BlockBytes != index || Volatile.Read(ref _regions[index]) is not { } region)
        {
            return (vaddr, size);
        }

        using var _ = region.Lock.Hold();
        return region.ExtendCpuDirtyRun(vaddr, size, lowerBound, upperBound, preserveHotPages, budgetPages);
    }

    public bool MayHaveCpuDirtyPages(ulong vaddr, ulong size)
    {
        if (IsKnownCpuClean(vaddr, size))
        {
            return false;
        }

        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            if (Volatile.Read(ref _regions[index]) is not { } region || region.IsModified(WriteOrigin.Cpu, offset, bytes))
            {
                return true;
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return false;
    }

    public long CpuDirtyEpoch => _cpuDirtySummary.Epoch;

    public bool HasGpuDirtyPages(ulong vaddr, ulong size)
    {
        RejectUploadCallbackReentry();
        return VisitRegions(vaddr, size, create: false, (region, offset, bytes) =>
        {
            using var _ = region.Lock.Hold();
            return region.IsModified(WriteOrigin.Gpu, offset, bytes);
        });
    }

    // Lock-free pre-check for hot readers: only the GPU queue thread sets GPU-dirty bits,
    // so on that thread a clear answer is exact; a stale dirty answer merely sends the
    // caller to HasGpuDirtyPages. Allocation- and lock-free.
    public bool MayHaveGpuDirtyPages(ulong vaddr, ulong size)
    {
        ValidateRange(vaddr, size);
        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            if (Volatile.Read(ref _regions[index]) is { } region && region.IsModified(WriteOrigin.Gpu, offset, bytes))
            {
                return true;
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return false;
    }

    // Lock-free: whether the GPU ever wrote a page of the range, downloaded since or not.
    public bool MayHaveGpuWrittenPages(ulong vaddr, ulong size)
    {
        ValidateRange(vaddr, size);
        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            if (Volatile.Read(ref _regions[index]) is { } region && region.WasGpuWritten(offset, bytes))
            {
                return true;
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return false;
    }

    public bool IsGpuOwned(ulong vaddr, ulong size)
    {
        ValidateRange(vaddr, size);
        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            if (Volatile.Read(ref _regions[index]) is not { } region || !region.IsGpuOwned(offset, bytes))
            {
                return false;
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return true;
    }

    public bool IsCpuWriteHotRange(ulong vaddr, ulong size)
    {
        RejectUploadCallbackReentry();
        return !VisitRegions(vaddr, size, create: true, (region, offset, bytes) =>
        {
            using var _ = region.Lock.Hold();
            return !region.IsCpuWriteHot(offset, bytes);
        });
    }

    public ulong CountCpuWriteHotBytes(ulong address, ulong size)
    {
        RejectUploadCallbackReentry();
        ulong count = 0;
        VisitRegions(address, size, create: false, (region, offset, bytes) =>
        {
            using var regionLock = region.Lock.Hold();
            count += region.CountCpuWriteHotBytes(offset, bytes);
            return false;
        });
        return count;
    }

    public void MarkCpuDirtyPages(ulong vaddr, ulong size) => Mark(vaddr, size, WriteOrigin.Cpu, enable: true, create: true);

    public void MarkGpuDirtyPages(ulong vaddr, ulong size) => Mark(vaddr, size, WriteOrigin.Gpu, enable: true, create: true);

    public void ClearGpuDirtyPages(ulong vaddr, ulong size) => Mark(vaddr, size, WriteOrigin.Gpu, enable: false, create: false);

    // True when every page of the range has a region, so the range is under tracking.
    public bool HasRegion(ulong vaddr, ulong size)
    {
        ValidateRange(vaddr, size);
        for (var index = vaddr / BlockBytes; index <= (vaddr + size - 1) / BlockBytes; index++)
        {
            if (Volatile.Read(ref _regions[index]) == null)
            {
                return false;
            }
        }

        return true;
    }

    public void UntrackMemory(ulong vaddr, ulong size)
    {
        RejectUploadCallbackReentry();
        ApplyPendingGpuProtections();
        var regions = AcquireRegionLocks(vaddr, size);
        try
        {
            if (VisitRegions(vaddr, size, create: false, (region, offset, bytes) => region.IsModified(WriteOrigin.Gpu, offset, bytes)))
            {
                PageGuard.OnFatal("Cannot remove tracking while the memory is GPU-dirty.");
            }

            VisitRegions(vaddr, size, create: false, (region, offset, bytes) =>
            {
                region.ChangeState(WriteOrigin.Cpu, enable: true, region.BaseAddress + offset, bytes);
                region.ResetCpuWriteHeat(region.BaseAddress + offset, bytes);
                return false;
            });
        }
        finally
        {
            ReleaseRegionLocks(regions);
        }
    }

    // Removes protection from a range; a GPU-dirty region flushes through onFlush without the lock.
    public bool InvalidateRegion(ulong vaddr, ulong size, Action onFlush)
    {
        var tracked = InvalidateRegion(vaddr, size, out var needsGpuFlush);
        if (needsGpuFlush)
        {
            onFlush();
        }

        return tracked;
    }

    // Marks the CPU write in every region whose bytes the GPU has not modified and
    // reports whether any region still holds GPU data the caller must download first
    // (one download of the whole range covers them all). Allocation-free: guest
    // command writes land here once per dword.
    public bool InvalidateRegion(ulong vaddr, ulong size, out bool needsGpuFlush)
    {
        RejectUploadCallbackReentry();
        ValidateRange(vaddr, size);
        var tracked = false;
        needsGpuFlush = false;
        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            if (Volatile.Read(ref _regions[index]) is { } region)
            {
                tracked = true;
                if (region.IsCpuWritableThroughout(offset, bytes))
                {
                    remaining -= bytes;
                    offset = 0;
                    index++;
                    continue;
                }

                using (region.Lock.Hold())
                {
                    if (region.IsModified(WriteOrigin.Gpu, offset, bytes))
                    {
                        needsGpuFlush = true;
                    }
                    else
                    {
                        region.MarkCpuWrite(region.BaseAddress + offset, bytes);
                    }
                }
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return tracked;
    }

    // After a write landed through the backing: every page the GPU does not own turns CPU-dirty, also in a
    // block where another page of the range is GPU-dirty. GPU-dirty pages keep their owner.
    public bool MarkLandedCpuWrite(ulong vaddr, ulong size)
    {
        RejectUploadCallbackReentry();
        ValidateRange(vaddr, size);
        var tracked = false;
        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            if (Volatile.Read(ref _regions[index]) is { } region)
            {
                tracked = true;
                if (!region.IsCpuDirtyThroughout(offset, bytes))
                {
                    using (region.Lock.Hold())
                    {
                        for (var page = offset & ~(PageBytes - 1); page < offset + bytes; page += PageBytes)
                        {
                            var start = Math.Max(page, offset);
                            var length = Math.Min(page + PageBytes, offset + bytes) - start;
                            if (!region.IsModified(WriteOrigin.Gpu, start, length))
                            {
                                region.MarkCpuWrite(region.BaseAddress + start, length);
                            }
                        }
                    }
                }
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return tracked;
    }

    public bool MarkCpuWriteKeepingProtection(ulong vaddr, ulong size, out bool needsGpuFlush)
    {
        RejectUploadCallbackReentry();
        ValidateRange(vaddr, size);
        var tracked = false;
        needsGpuFlush = false;
        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            if (Volatile.Read(ref _regions[index]) is { } region)
            {
                tracked = true;
                if (!region.IsCpuDirtyThroughout(offset, bytes))
                {
                    using var _ = region.Lock.Hold();
                    if (region.IsModified(WriteOrigin.Gpu, offset, bytes))
                    {
                        needsGpuFlush = true;
                        EmulatorWritesOverGpuData.Increment();
                    }
                    else
                    {
                        region.MarkCpuWriteKeepingProtection(region.BaseAddress + offset, bytes);
                        EmulatorWritesKept.Increment();
                    }
                }
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return tracked;
    }

    public void ForEachDownloadRange(ulong vaddr, ulong size, bool clear, Action<ulong, ulong>? preflight, Action<ulong, ulong> visit)
    {
        RejectUploadCallbackReentry();
        var regions = AcquireRegionLocks(vaddr, size);
        try
        {
            if (preflight != null)
            {
                VisitDirtyRanges(vaddr, size, WriteOrigin.Gpu, clear: false, preflight);
            }

            VisitDirtyRanges(vaddr, size, WriteOrigin.Gpu, clear: false, visit);
            if (clear)
            {
                VisitDirtyRanges(vaddr, size, WriteOrigin.Gpu, clear: true, static (_, _) => { });
            }
        }
        finally
        {
            ReleaseRegionLocks(regions);
        }
    }

    // Region locks stay held across uploadFunc only for a written range, which turns GPU-dirty.
    public long UploadBatch { get; set; } = -1;

    public void ForEachUploadRange(ulong vaddr, ulong size, bool isWritten, Action<ulong, ulong> rangeFunc, Action uploadFunc,
        bool preserveCpuWriteHotPages = true)
    {
        RejectUploadCallbackReentry();
        if (!isWritten && !MayHaveCpuDirtyPages(vaddr, size))
        {
            uploadFunc();
            return;
        }

        using (isWritten ? GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.WrittenUploadRegionSetup) : default)
            CreateRegions(vaddr, size);
        var scratch = _availableUploadScratch ?? new UploadScratch();
        // Nested uploads on another tracker must use separate storage.
        _availableUploadScratch = null;
        var held = scratch.Held;
        var releasedCount = 0;
        var cleared = scratch.Cleared;
        var previousOwner = _uploadOwner;
        _uploadOwner = this;
        try
        {
            using (GpuMemoryAccessProfile.Measure(GpuMemoryAccessProfile.Operation.UploadTracking, size))
            {
                var remaining = size;
                var index = vaddr / BlockBytes;
                var offset = vaddr % BlockBytes;
                while (remaining != 0)
                {
                    var bytes = Math.Min(BlockBytes - offset, remaining);
                    var region = Volatile.Read(ref _regions[index])!;
                    if (!isWritten || !region.IsGpuOwned(offset, bytes))
                    {
                        using (isWritten ? GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.WrittenUploadLockWait) : default)
                            region.Lock.Enter();
                        held.Add(region);
                        using (isWritten ? GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.WrittenUploadRangeScan) : default)
                            region.ForEachCpuUploadRange(
                                preserveHotPages: !isWritten && preserveCpuWriteHotPages,
                                region.BaseAddress + offset,
                                bytes,
                                cleared,
                                rangeFunc,
                                UploadBatch);
                        if (!isWritten)
                        {
                            region.Lock.Exit();
                            held.RemoveAt(held.Count - 1);
                        }
                    }

                    remaining -= bytes;
                    offset = 0;
                    index++;
                }
            }
            using (isWritten ? GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.WrittenUploadSource) : default)
                uploadFunc();
            if (isWritten)
            {
                for (; releasedCount < held.Count; releasedCount++)
                {
                    var region = held[releasedCount];
                    var address = Math.Max(vaddr, region.BaseAddress);
                    var end = Math.Min(vaddr + size, region.BaseAddress + BlockBytes);
                    using (GuestMemoryProfile.Measure(GuestMemoryProfile.Operation.WrittenUploadOwnership))
                        region.ChangeState(WriteOrigin.Gpu, enable: true, address, end - address);
                    region.Lock.Exit();
                }
            }
        }
        catch
        {
            // Rollback must see only locks that are still held.
            held.RemoveRange(0, releasedCount);
            releasedCount = 0;
            // Runs the buffer never received go back to the CPU; a run another operation made
            // GPU-owned after this upload released its lock keeps that newer ownership.
            var lockedThroughout = new HashSet<TrackedRegion>(held);
            foreach (var (region, address, bytes) in cleared)
            {
                if (!held.Contains(region))
                {
                    region.Lock.Enter();
                    held.Add(region);
                }

                region.CancelCpuUpload(address, bytes);

                if (!lockedThroughout.Contains(region))
                {
                    for (var page = address; page < address + bytes; page += PageBytes)
                    {
                        if (!region.IsModified(WriteOrigin.Gpu, page - region.BaseAddress, PageBytes))
                        {
                            region.ChangeState(WriteOrigin.Cpu, enable: true, page, PageBytes);
                        }
                    }

                    continue;
                }

                if (region.IsModified(WriteOrigin.Gpu, address - region.BaseAddress, bytes))
                {
                    region.ChangeState(WriteOrigin.Gpu, enable: false, address, bytes);
                }

                region.ChangeState(WriteOrigin.Cpu, enable: true, address, bytes);
            }

            throw;
        }
        finally
        {
            for (var index = releasedCount; index < held.Count; index++)
            {
                held[index].Lock.Exit();
            }

            _uploadOwner = previousOwner;
            held.Clear();
            cleared.Clear();
            _availableUploadScratch ??= scratch;
        }
    }

    [Conditional("DEBUG")]
    public void ValidateGpuDirtyPages(AvlSpanSet dirty, ulong vaddr, ulong size, string operation)
    {
        if (!new GuestSpan(vaddr, size).IsValid || vaddr % PageBytes != 0 || size % PageBytes != 0)
        {
            PageGuard.OnFatal("Cannot validate dirty pages in this range.");
        }

        for (var page = vaddr; page < vaddr + size; page += PageBytes)
        {
            if (!dirty.Overlaps(page, PageBytes))
            {
                PageGuard.OnFatal($"The GPU-dirty page has no dirty bytes: operation={operation} addr=0x{page:X16}");
            }
        }
    }

    [Conditional("DEBUG")]
    public void ValidateGpuDirtyOwnership(AvlSpanSet dirty, ulong vaddr, ulong size, string operation)
    {
        ValidateRange(vaddr, size);
        var end = (vaddr + size + PageBytes - 1) & ~(PageBytes - 1);
        for (var page = vaddr & ~(PageBytes - 1); page < end; page += PageBytes)
        {
            if (HasGpuDirtyPages(page, PageBytes) != dirty.Overlaps(page, PageBytes))
            {
                PageGuard.OnFatal($"Page tracking and byte ownership disagree: operation={operation} addr=0x{page:X16}");
            }
        }
    }

    private void Mark(ulong vaddr, ulong size, WriteOrigin side, bool enable, bool create)
    {
        RejectUploadCallbackReentry();
        VisitRegions(vaddr, size, create, (region, offset, bytes) =>
        {
            using var _ = region.Lock.Hold();
            if (side == WriteOrigin.Cpu && enable)
            {
                region.MarkCpuWrite(region.BaseAddress + offset, bytes);
            }
            else
            {
                region.ChangeState(side, enable, region.BaseAddress + offset, bytes);
            }
            return false;
        });
    }

    private void VisitDirtyRanges(ulong vaddr, ulong size, WriteOrigin side, bool clear, Action<ulong, ulong> visit) =>
        VisitRegions(vaddr, size, create: false, (region, offset, bytes) =>
        {
            region.ForEachModifiedRange(side, clear, region.BaseAddress + offset, bytes, visit);
            return false;
        });

    private List<TrackedRegion> AcquireRegionLocks(ulong vaddr, ulong size)
    {
        var regions = new List<TrackedRegion>();
        VisitRegions(vaddr, size, create: false, (region, _, _) =>
        {
            regions.Add(region);
            return false;
        });
        foreach (var region in regions)
        {
            region.Lock.Enter();
        }

        return regions;
    }

    private static void ReleaseRegionLocks(List<TrackedRegion> regions)
    {
        foreach (var region in regions)
        {
            region.Lock.Exit();
        }
    }

    private void RejectUploadCallbackReentry()
    {
        if (_uploadOwner == this)
        {
            PageGuard.OnFatal("Cannot enter the memory tracker from its upload callback.");
        }
    }

    // True when every block of the range is tracked and has no CPU-dirty page. A missing
    // region is not known clean: the precise path creates it, fully dirty.
    // Visits the maximal runs of the range whose 4 MiB blocks may hold CPU-dirty pages: a block
    // with no region yet starts all dirty, and a region's summary bit is set while any of its
    // pages is dirty. Known-clean blocks are skipped without a lock, the same test
    // HasCpuDirtyPages starts with, so a caller that only uploads dirty pages loses nothing.
    public void ForEachPossiblyCpuDirtyRange(ulong vaddr, ulong size, Action<ulong, ulong> visit)
    {
        if (size == 0)
        {
            return;
        }

        if (!new GuestSpan(vaddr, size).IsValid)
        {
            // Outside the tracked space nothing is known clean; the caller decides as before.
            visit(vaddr, size);
            return;
        }

        var end = vaddr + size;
        var last = (end - 1) / BlockBytes;
        var runStart = 0UL;
        var inRun = false;
        var index = vaddr / BlockBytes;
        var loadedWord = -1;
        var possiblyDirty = 0UL;
        while (index <= last)
        {
            var word = (int)(index / 64);
            if (word != loadedWord)
            {
                loadedWord = word;
                possiblyDirty = ~(ulong)Volatile.Read(ref _presentRegions[word]) | _cpuDirtySummary.Word(word);
            }

            var shift = (int)(index % 64);
            var pending = (inRun ? ~possiblyDirty : possiblyDirty) >> shift;
            if (pending == 0)
            {
                index = ((ulong)word + 1) * 64;
                continue;
            }

            index += (ulong)System.Numerics.BitOperations.TrailingZeroCount(pending);
            if (index > last)
            {
                break;
            }

            if (inRun)
            {
                visit(runStart, index * BlockBytes - runStart);
                inRun = false;
            }
            else
            {
                runStart = Math.Max(vaddr, index * BlockBytes);
                inRun = true;
            }
        }

        if (inRun)
        {
            visit(runStart, end - runStart);
        }
    }

    public sealed class SweepPlan
    {
        internal GuestSpan[] Spans { get; init; } = [];
        internal ulong[] Blocks { get; init; } = [];
        internal int FirstWord { get; init; }
    }

    public static SweepPlan? PlanSweep(IReadOnlyList<GuestSpan> spans)
    {
        if (spans.Count == 0)
        {
            return new SweepPlan();
        }

        var previousEnd = 0UL;
        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];
            if (span.Size == 0 || !span.IsValid || span.Address < previousEnd)
            {
                return null;
            }

            previousEnd = span.End;
        }

        var firstWord = (int)(spans[0].Address / BlockBytes / 64);
        var lastWord = (int)((previousEnd - 1) / BlockBytes / 64);
        var blocks = new ulong[lastWord - firstWord + 1];
        var copy = new GuestSpan[spans.Count];
        for (var index = 0; index < copy.Length; index++)
        {
            var span = spans[index];
            copy[index] = span;
            var last = (span.End - 1) / BlockBytes;
            for (var block = span.Address / BlockBytes; block <= last; block++)
            {
                blocks[(int)(block / 64) - firstWord] |= 1UL << (int)(block % 64);
            }
        }

        return new SweepPlan { Spans = copy, Blocks = blocks, FirstWord = firstWord };
    }

    public void ForEachCoveredCpuDirtyPageRun(SweepPlan plan, PageCoverage coverage, Action<ulong, ulong> visit, Action? blockVisited = null)
    {
        var nextSpan = 0;
        for (var offset = 0; offset < plan.Blocks.Length; offset++)
        {
            var word = plan.FirstWord + offset;
            var pending = plan.Blocks[offset] & coverage.BlockWord(word) &
                (~(ulong)Volatile.Read(ref _presentRegions[word]) | _cpuDirtySummary.Word(word));
            while (pending != 0)
            {
                var block = (ulong)word * 64 + (ulong)System.Numerics.BitOperations.TrailingZeroCount(pending);
                pending &= pending - 1;
                if (coverage.BlockAt(block) is not { } covered)
                {
                    continue;
                }

                SweepBlocks.Increment();
                var region = Volatile.Read(ref _regions[block]);
                if (region is not null)
                {
                    var version = region.CpuDirtyVersion;
                    if (covered.CleanRegionVersion == version && covered.CleanCoverageVersion == covered.Version)
                    {
                        SweepBlocksClean.Increment();
                        continue;
                    }

                    if (region.CpuDirtyPages(in covered.Pages).None)
                    {
                        covered.CleanRegionVersion = version;
                        covered.CleanCoverageVersion = covered.Version;
                        SweepBlocksClean.Increment();
                        continue;
                    }
                }

                var candidates = covered.Pages & SpanPages(plan.Spans, ref nextSpan, block);
                if (region is not null && region.CpuDirtyPages(in candidates).None)
                {
                    continue;
                }

                region ??= GetOrCreateRegion(block);
                PageMask taken;
                using (region.Lock.Hold())
                {
                    taken = region.TakeCpuDirtyPages(in candidates);
                }

                if (taken.Any)
                {
                    SweepBlocksTaken.Increment();
                }

                try
                {
                    foreach (var (runStart, runEnd) in taken)
                    {
                        SweepPagesTaken.Add(runEnd - runStart);
                        visit(block * BlockBytes + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes);
                    }

                    blockVisited?.Invoke();
                }
                catch
                {
                    using (region.Lock.Hold())
                    {
                        region.ReturnCpuDirtyPages(in taken);
                    }

                    throw;
                }
            }
        }
    }

    private static PageMask SpanPages(GuestSpan[] spans, ref int nextSpan, ulong block)
    {
        var blockStart = block * BlockBytes;
        var blockEnd = blockStart + BlockBytes;
        var low = nextSpan;
        var high = spans.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (spans[middle].End <= blockStart)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        nextSpan = low;
        var pages = new PageMask();
        for (var index = low; index < spans.Length && spans[index].Address < blockEnd; index++)
        {
            var start = Math.Max(spans[index].Address, blockStart) - blockStart;
            var end = Math.Min(spans[index].End, blockEnd) - blockStart;
            pages.SetRange((int)(start / PageBytes), (int)((end + PageBytes - 1) / PageBytes));
        }

        return pages;
    }

    private bool IsKnownCpuClean(ulong vaddr, ulong size)
    {
        ValidateRange(vaddr, size);
        var last = (vaddr + size - 1) / BlockBytes;
        for (var index = vaddr / BlockBytes; index <= last; index++)
        {
            if (Volatile.Read(ref _regions[index]) == null || _cpuDirtySummary.IsDirty(index))
            {
                return false;
            }
        }

        return true;
    }

    private void CreateRegions(ulong vaddr, ulong size)
    {
        ValidateRange(vaddr, size);
        var last = (vaddr + size - 1) / BlockBytes;
        for (var index = vaddr / BlockBytes; index <= last; index++)
        {
            if (Volatile.Read(ref _regions[index]) == null)
            {
                _ = GetOrCreateRegion(index);
            }
        }
    }

    // Visits (region, offset, bytes) per 4 MiB chunk; a true result stops the walk early.
    private bool VisitRegions(ulong vaddr, ulong size, bool create, Func<TrackedRegion, ulong, ulong, bool> visit)
    {
        ValidateRange(vaddr, size);
        var remaining = size;
        var index = vaddr / BlockBytes;
        var offset = vaddr % BlockBytes;
        while (remaining != 0)
        {
            var bytes = Math.Min(BlockBytes - offset, remaining);
            var region = Volatile.Read(ref _regions[index]);
            if (region == null && create)
            {
                region = GetOrCreateRegion(index);
            }

            if (region != null && visit(region, offset, bytes))
            {
                return true;
            }

            remaining -= bytes;
            offset = 0;
            index++;
        }

        return false;
    }

    private TrackedRegion GetOrCreateRegion(ulong index)
    {
        lock (_regionGate)
        {
            if (Volatile.Read(ref _regions[index]) is { } existing)
            {
                return existing;
            }

            var created = new TrackedRegion(_pages, index * BlockBytes, _cpuDirtySummary, this);
            Volatile.Write(ref _regions[index], created);
            Interlocked.Or(ref _presentRegions[index / 64], 1L << (int)(index % 64));
            return created;
        }
    }

    private static void ValidateRange(ulong vaddr, ulong size)
    {
        if (vaddr == 0 || size == 0 || !new GuestSpan(vaddr, size).IsValid)
        {
            PageGuard.OnFatal("The requested memory tracking range is invalid.");
        }
    }
}
