// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GpuMemory;

public sealed class GuestPageTracker
{
    private const ulong BlockBytes = TrackerLayout.BlockBytes;
    private const ulong PageBytes = TrackerLayout.PageBytes;

    [ThreadStatic]
    private static GuestPageTracker? _uploadOwner;

    private readonly PageGuard _pages;
    private readonly TrackedRegion?[] _regions = new TrackedRegion?[TrackerLayout.BlockCount];
    private readonly long[] _presentRegions = new long[(TrackerLayout.BlockCount + 63) / 64];
    private readonly object _regionGate = new();
    private readonly CpuDirtySummary _cpuDirtySummary = new();

    public GuestPageTracker(PageGuard pages) => _pages = pages;

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

    // Receives the CPU-dirty runs an upload copies, then performs the upload while the regions are held.
    public interface IUploadRangeSink
    {
        void Range(ulong address, ulong size);

        void Upload();
    }

    private struct DelegateUploadSink(Action<ulong, ulong> rangeFunc, Action uploadFunc) : IUploadRangeSink
    {
        public readonly void Range(ulong address, ulong size) => rangeFunc(address, size);

        public readonly void Upload() => uploadFunc();
    }

    // Forwards a region's upload runs to the sink and remembers the runs it cleared, for rollback.
    private struct RegionUploadVisitor<TSink>(TSink sink, List<(TrackedRegion Region, ulong Address, ulong Size)> cleared)
        : TrackedRegion.ICpuUploadVisitor
        where TSink : struct, IUploadRangeSink
    {
        public TSink Sink = sink;

        public readonly void Cleared(TrackedRegion region, ulong address, ulong size) => cleared.Add((region, address, size));

        public void Upload(ulong address, ulong size) => Sink.Range(address, size);
    }

    // Uploads run on every bound buffer of every draw, so the bookkeeping lists are reused per thread.
    [ThreadStatic]
    private static List<TrackedRegion>? _scratchHeld;

    [ThreadStatic]
    private static List<(TrackedRegion Region, ulong Address, ulong Size)>? _scratchCleared;

    public void ForEachUploadRange(ulong vaddr, ulong size, bool isWritten, Action<ulong, ulong> rangeFunc, Action uploadFunc,
        bool preserveCpuWriteHotPages = true)
    {
        var sink = new DelegateUploadSink(rangeFunc, uploadFunc);
        ForEachUploadRange(vaddr, size, isWritten, ref sink, preserveCpuWriteHotPages);
    }

    // Region locks stay held across the upload only for a written range, which turns GPU-dirty.
    public void ForEachUploadRange<TSink>(ulong vaddr, ulong size, bool isWritten, ref TSink sink, bool preserveCpuWriteHotPages = true)
        where TSink : struct, IUploadRangeSink
    {
        RejectUploadCallbackReentry();
        VisitRegions(vaddr, size, create: true, static (_, _, _) => false);
        var previousOwner = _uploadOwner;
        _uploadOwner = this;
        // Another tracker may upload from this callback. It needs its own rollback
        // state; the reentry guard only rejects reentry into this tracker.
        var held = previousOwner is null
            ? _scratchHeld ??= new List<TrackedRegion>()
            : new List<TrackedRegion>();
        var cleared = previousOwner is null
            ? _scratchCleared ??= new List<(TrackedRegion Region, ulong Address, ulong Size)>()
            : new List<(TrackedRegion Region, ulong Address, ulong Size)>();
        held.Clear();
        cleared.Clear();
        try
        {
            using (GpuMemoryAccessProfile.Measure(GpuMemoryAccessProfile.Operation.UploadTracking, size))
            {
                var visitor = new RegionUploadVisitor<TSink>(sink, cleared);
                var preserveHotPages = !isWritten && preserveCpuWriteHotPages;
                for (var (index, offset, remaining) = (vaddr / BlockBytes, vaddr % BlockBytes, size); remaining != 0; index++, offset = 0)
                {
                    var bytes = Math.Min(BlockBytes - offset, remaining);
                    remaining -= bytes;
                    if (Volatile.Read(ref _regions[index]) is not { } region)
                    {
                        continue;
                    }

                    region.Lock.Enter();
                    held.Add(region);
                    region.ForEachCpuUploadRange(preserveHotPages, region.BaseAddress + offset, bytes, ref visitor);
                    if (!isWritten)
                    {
                        region.Lock.Exit();
                        held.RemoveAt(held.Count - 1);
                    }
                }

                sink = visitor.Sink;
            }
            sink.Upload();
            if (isWritten)
            {
                for (var (index, offset, remaining) = (vaddr / BlockBytes, vaddr % BlockBytes, size); remaining != 0; index++, offset = 0)
                {
                    var bytes = Math.Min(BlockBytes - offset, remaining);
                    remaining -= bytes;
                    if (Volatile.Read(ref _regions[index]) is not { } region)
                    {
                        continue;
                    }

                    region.ChangeState(WriteOrigin.Gpu, enable: true, region.BaseAddress + offset, bytes);
                    region.Lock.Exit();
                    held.Remove(region);
                }
            }
        }
        catch
        {
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
            foreach (var region in held)
            {
                region.Lock.Exit();
            }

            held.Clear();
            cleared.Clear();
            _uploadOwner = previousOwner;
        }
    }

    [Conditional("DEBUG")]
    public void ValidateGpuDirtyPages(SpanSet dirty, ulong vaddr, ulong size, string operation)
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
    public void ValidateGpuDirtyOwnership(SpanSet dirty, ulong vaddr, ulong size, string operation)
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

            var created = new TrackedRegion(_pages, index * BlockBytes, _cpuDirtySummary);
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
