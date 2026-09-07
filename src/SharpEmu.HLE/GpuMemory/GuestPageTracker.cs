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
    private readonly object _regionGate = new();

    public GuestPageTracker(PageGuard pages) => _pages = pages;

    public bool HasCpuDirtyPages(ulong vaddr, ulong size)
    {
        RejectUploadCallbackReentry();
        return VisitRegions(vaddr, size, create: true, (region, offset, bytes) =>
        {
            using var _ = region.Lock.Hold();
            return region.IsModified(WriteOrigin.Cpu, offset, bytes);
        });
    }

    public bool HasGpuDirtyPages(ulong vaddr, ulong size)
    {
        RejectUploadCallbackReentry();
        return VisitRegions(vaddr, size, create: false, (region, offset, bytes) =>
        {
            using var _ = region.Lock.Hold();
            return region.IsModified(WriteOrigin.Gpu, offset, bytes);
        });
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
                return false;
            });
        }
        finally
        {
            ReleaseRegionLocks(regions);
        }
    }

    // Removes protection from a range; a GPU-dirty region flushes through onFlush without the lock.
    public void InvalidateRegion(ulong vaddr, ulong size, Action onFlush)
    {
        RejectUploadCallbackReentry();
        VisitRegions(vaddr, size, create: false, (region, offset, bytes) =>
        {
            bool shouldFlush;
            using (region.Lock.Hold())
            {
                shouldFlush = region.IsModified(WriteOrigin.Gpu, offset, bytes);
                if (!shouldFlush)
                {
                    region.ChangeState(WriteOrigin.Cpu, enable: true, region.BaseAddress + offset, bytes);
                }
            }

            if (shouldFlush)
            {
                onFlush();
            }

            return false;
        });
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
    public void ForEachUploadRange(ulong vaddr, ulong size, bool isWritten, Action<ulong, ulong> rangeFunc, Action uploadFunc)
    {
        RejectUploadCallbackReentry();
        VisitRegions(vaddr, size, create: true, static (_, _, _) => false);
        var previousOwner = _uploadOwner;
        _uploadOwner = this;
        var held = new List<TrackedRegion>();
        var cleared = new List<(TrackedRegion Region, ulong Address, ulong Size)>();
        try
        {
            VisitRegions(vaddr, size, create: false, (region, offset, bytes) =>
            {
                region.Lock.Enter();
                held.Add(region);
                var address = region.BaseAddress + offset;
                region.ForEachModifiedRange(WriteOrigin.Cpu, clear: false, address, bytes, (runAddress, runSize) => cleared.Add((region, runAddress, runSize)));
                region.ForEachModifiedRange(WriteOrigin.Cpu, clear: true, address, bytes, rangeFunc);
                if (!isWritten)
                {
                    region.Lock.Exit();
                    held.Remove(region);
                }

                return false;
            });
            uploadFunc();
            if (isWritten)
            {
                VisitRegions(vaddr, size, create: false, (region, offset, bytes) =>
                {
                    region.ChangeState(WriteOrigin.Gpu, enable: true, region.BaseAddress + offset, bytes);
                    region.Lock.Exit();
                    held.Remove(region);
                    return false;
                });
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
            region.ChangeState(side, enable, region.BaseAddress + offset, bytes);
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

            var created = new TrackedRegion(_pages, index * BlockBytes);
            Volatile.Write(ref _regions[index], created);
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
