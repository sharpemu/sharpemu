// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// Dirty state of one 4 MiB block: a page is CPU-dirty, GPU-dirty or clean, never both dirty.
public sealed class TrackedRegion
{
    private const ulong PageBytes = TrackerLayout.PageBytes;
    private const ulong BlockBytes = TrackerLayout.BlockBytes;

    private readonly PageGuard _pages;
    private PageMask _cpuDirty;
    private PageMask _gpuDirty;
    private PageMask _gpuEver;
    private PageMask _writable;
    private PageMask _protectedWhileDirty;
    private PageMask _publishedCpuDirty;
    private PageMask _readable;
    private PageMask _recentCpuUploads;
    private PageMask _repeatedCpuWrites;
    private PageMask _hotCpuWrites;
    private PageMask _uploadedHotPages;
    private long _uploadBatch = -1;
    private static long _hotUploadsSkipped;
    private readonly CpuDirtySummary? _summary;
    private readonly GuestPageTracker? _owner;
    private bool _gpuProtectionQueued;

    public static long TakeHotUploadsSkipped() => Interlocked.Exchange(ref _hotUploadsSkipped, 0);
    private int _cpuDirtyVersion;

    public int CpuDirtyVersion => Volatile.Read(ref _cpuDirtyVersion);

    public PageMask CpuDirtyPages(in PageMask pages) => _cpuDirty & pages;

    public TrackedRegion(PageGuard pages, ulong baseAddress, CpuDirtySummary? summary = null, GuestPageTracker? owner = null)
    {
        _owner = owner;
        if (baseAddress % BlockBytes != 0)
        {
            PageGuard.OnFatal("Cannot create the tracking region with these parameters.");
        }

        _pages = pages;
        _summary = summary;
        BaseAddress = baseAddress;
        _cpuDirty.Fill();
        _publishedCpuDirty.Fill();
        _writable.Fill();
        _readable.Fill();
        _summary?.Set(BaseAddress / BlockBytes, dirty: true);
        _summary?.NoteDirtied();
    }

    public RegionLock Lock { get; } = new();

    public ulong BaseAddress { get; }

    public bool IsModified(WriteOrigin side, ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return GetDirtyMask(side).AnyInRange(start, end);
    }

    public bool WasGpuWritten(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return _gpuEver.AnyInRange(start, end);
    }

    public bool IsCpuDirtyThroughout(ulong offset, ulong size) => AllSet(_cpuDirty, offset, size);

    public bool IsCpuWritableThroughout(ulong offset, ulong size) => AllSet(_writable, offset, size);

    private bool AllSet(in PageMask mask, ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        for (var page = start; page < end; page++)
        {
            if (!mask.Get(page))
            {
                return false;
            }
        }

        return true;
    }

    public bool IsGpuOwned(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return _gpuDirty.AllInRange(start, end);
    }

    public bool IsCpuWriteHot(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        for (var page = start; page < end; page++)
        {
            if (!_hotCpuWrites.Get(page))
            {
                return false;
            }
        }

        return true;
    }

    public ulong CountCpuWriteHotBytes(ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        ulong bytes = 0;
        foreach (var (first, last) in new PageMask(_hotCpuWrites, start, end))
        {
            bytes += Math.Min((ulong)last * PageBytes, offset + size) - Math.Max((ulong)first * PageBytes, offset);
        }
        return bytes;
    }

    // Count only clean-to-dirty transitions. Repeated reads cannot make a page hot.
    public void MarkCpuWrite(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        for (var page = start; page < end; page++)
        {
            if (_cpuDirty.Get(page))
            {
                continue;
            }

            if (_recentCpuUploads.Get(page))
            {
                if (_repeatedCpuWrites.Get(page))
                {
                    _hotCpuWrites.Set(page);
                }
                else
                {
                    _repeatedCpuWrites.Set(page);
                }

                _recentCpuUploads.Unset(page);
            }
            else
            {
                _repeatedCpuWrites.Unset(page);
            }
        }

        ChangeState(WriteOrigin.Cpu, enable: true, address, size);
    }

    public void MarkCpuWriteKeepingProtection(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        if (new PageMask(_gpuDirty, start, end).Any)
        {
            PageGuard.OnFatal("Cannot mark GPU-dirty pages as CPU-dirty.");
        }

        foreach (var (runStart, runEnd) in new PageMask(~_cpuDirty, start, end))
        {
            _protectedWhileDirty.SetRange(runStart, runEnd);
        }

        _cpuDirty.SetRange(start, end);
        UpdateCpuProtection(ProtectionCause.UploadReprotect, ProtectionCause.EmulatorWrite);
    }

    public void ChangeState(WriteOrigin side, bool enable, ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        if (enable && side == WriteOrigin.Cpu && _gpuDirty.AnyInRange(start, end))
        {
            PageGuard.OnFatal("Cannot mark GPU-dirty pages as CPU-dirty.");
        }

        if (enable && side == WriteOrigin.Gpu && _cpuDirty.AnyInRange(start, end))
        {
            PageGuard.OnFatal("Cannot mark CPU-dirty pages as GPU-dirty.");
        }

        ref var bits = ref GetDirtyMask(side);
        if (enable && side == WriteOrigin.Cpu)
        {
            _protectedWhileDirty.UnsetRange(start, end);
        }

        if (enable && side == WriteOrigin.Gpu)
        {
            bits.SetRangeAndClear(start, end, ref _recentCpuUploads, ref _repeatedCpuWrites, ref _hotCpuWrites);
            _gpuEver.SetRange(start, end);
        }
        else if (enable)
        {
            bits.SetRange(start, end);
        }
        else
        {
            bits.UnsetRange(start, end);
        }

        if (side == WriteOrigin.Cpu)
        {
            UpdateCpuProtection();
        }
        else
        {
            UpdateGpuProtection();
        }
    }

    // Hot read-only pages stay writable and dirty, so each obtain observes current CPU bytes.
    public void ForEachCpuUploadRange(
        bool preserveHotPages,
        ulong address,
        ulong size,
        List<(TrackedRegion Region, ulong Address, ulong Size)> cleared,
        Action<ulong, ulong> visitUpload,
        long uploadBatch = -1)
    {
        var (start, end) = GetPageRange(address, size);
        if (!_cpuDirty.AnyInRange(start, end))
        {
            UpdateCpuProtection();
            return;
        }

        var upload = new PageMask(_cpuDirty, start, end);
        var dedupeHotPages = preserveHotPages && uploadBatch >= 0;
        if (dedupeHotPages)
        {
            if (_uploadBatch != uploadBatch)
            {
                _uploadedHotPages = default;
                _uploadBatch = uploadBatch;
            }

            var alreadyUploaded = upload & _hotCpuWrites & _uploadedHotPages;
            if (alreadyUploaded.Any)
            {
                upload = upload & ~alreadyUploaded;
                Interlocked.Increment(ref _hotUploadsSkipped);
            }
        }

        var clearedPages = preserveHotPages ? upload & ~_hotCpuWrites : upload;
        foreach (var (runStart, runEnd) in clearedPages)
        {
            cleared.Add((this, BaseAddress + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes));
            _cpuDirty.UnsetRange(runStart, runEnd);
            _recentCpuUploads.SetRange(runStart, runEnd);
        }

        UpdateCpuProtection();
        foreach (var (runStart, runEnd) in upload)
        {
            visitUpload(BaseAddress + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes);
        }

        if (dedupeHotPages)
        {
            foreach (var (runStart, runEnd) in upload & _hotCpuWrites)
            {
                _uploadedHotPages.SetRange(runStart, runEnd);
            }
        }
    }

    public PageMask TakeCpuDirtyPages(in PageMask candidates)
    {
        var taken = _cpuDirty & candidates;
        if (taken.None)
        {
            return taken;
        }

        foreach (var (runStart, runEnd) in taken)
        {
            _cpuDirty.UnsetRange(runStart, runEnd);
            _recentCpuUploads.SetRange(runStart, runEnd);
        }

        UpdateCpuProtection(ProtectionCause.SweepReprotect, ProtectionCause.CpuWriteRelease);
        return taken;
    }

    public void ReturnCpuDirtyPages(in PageMask pages)
    {
        foreach (var (runStart, runEnd) in pages)
        {
            _recentCpuUploads.UnsetRange(runStart, runEnd);
        }

        foreach (var (runStart, runEnd) in pages & ~_gpuDirty)
        {
            _protectedWhileDirty.UnsetRange(runStart, runEnd);
            _cpuDirty.SetRange(runStart, runEnd);
        }

        UpdateCpuProtection();
    }

    public (ulong Address, ulong Size) ExtendCpuDirtyRun(ulong address, ulong size, ulong lowerBound, ulong upperBound, bool preserveHotPages, int budgetPages)
    {
        var (start, end) = GetPageRange(address, size);
        var lowPage = (int)((Math.Max(lowerBound, BaseAddress) - BaseAddress) / PageBytes);
        var highPage = (int)((Math.Min(upperBound, BaseAddress + BlockBytes) - BaseAddress) / PageBytes);
        var first = start;
        while (first > lowPage && budgetPages > 0 && IsCpuUploadable(first - 1, preserveHotPages))
        {
            first--;
            budgetPages--;
        }

        var last = end;
        while (last < highPage && budgetPages > 0 && IsCpuUploadable(last, preserveHotPages))
        {
            last++;
            budgetPages--;
        }

        if (first == start && last == end)
        {
            return (address, size);
        }

        var runAddress = Math.Max(BaseAddress + (ulong)first * PageBytes, Math.Min(address, lowerBound));
        var runEnd = Math.Min(BaseAddress + (ulong)last * PageBytes, Math.Max(address + size, upperBound));
        return (runAddress, runEnd - runAddress);
    }

    private bool IsCpuUploadable(int page, bool preserveHotPages) =>
        _cpuDirty.Get(page) && !(preserveHotPages && _hotCpuWrites.Get(page));

    public void ResetCpuWriteHeat(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        _recentCpuUploads.UnsetRange(start, end);
        _repeatedCpuWrites.UnsetRange(start, end);
        _hotCpuWrites.UnsetRange(start, end);
    }

    public void CancelCpuUpload(ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        _recentCpuUploads.UnsetRange(start, end);
    }

    // Visits maximal dirty runs after the bits were cleared and the protection updated.
    public void ForEachModifiedRange(WriteOrigin side, bool clear, ulong address, ulong size, Action<ulong, ulong> visit)
    {
        var (start, end) = GetPageRange(address, size);
        var mask = new PageMask(GetDirtyMask(side), start, end);
        if (clear)
        {
            GetDirtyMask(side).UnsetRange(start, end);
            if (side == WriteOrigin.Cpu)
            {
                UpdateCpuProtection();
            }
            else
            {
                UpdateGpuProtection();
            }
        }

        foreach (var (runStart, runEnd) in mask)
        {
            visit(BaseAddress + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes);
        }
    }

    private ref PageMask GetDirtyMask(WriteOrigin side) => ref side == WriteOrigin.Cpu ? ref _cpuDirty : ref _gpuDirty;

    // Every change to the CPU-dirty mask ends here, so it also publishes the block summary.
    private void UpdateCpuProtection() => UpdateCpuProtection(ProtectionCause.UploadReprotect, ProtectionCause.CpuWriteRelease);

    private void UpdateCpuProtection(ProtectionCause protectCause, ProtectionCause releaseCause)
    {
        _protectedWhileDirty = _protectedWhileDirty & _cpuDirty;
        _summary?.Set(BaseAddress / BlockBytes, _cpuDirty.Any);
        var changed = _cpuDirty ^ _publishedCpuDirty;
        if (changed.Any)
        {
            if (_summary is not null && (_cpuDirty & changed).Any)
            {
                _summary.NoteDirtied();
            }

            _publishedCpuDirty = _cpuDirty;
            Interlocked.Increment(ref _cpuDirtyVersion);
        }

        var writable = _cpuDirty & ~_protectedWhileDirty;
        var protect = _writable & ~writable;
        var release = writable & ~_writable;
        _writable = writable;
        if (protect.Any)
        {
            _pages.AddWatchMask(BaseAddress, protect, blockReads: false, protectCause);
        }

        if (release.Any)
        {
            _pages.RemoveWatchMask(BaseAddress, release, blockReads: false, releaseCause);
        }
    }

    private void UpdateGpuProtection()
    {
        var release = ~_gpuDirty & ~_readable;
        if (release.Any)
        {
            _readable = _readable ^ release;
            _pages.RemoveWatchMask(BaseAddress, release, blockReads: true);
        }

        if ((_readable & _gpuDirty).None)
        {
            return;
        }

        if (_owner is { DefersGpuWriteProtection: true } owner)
        {
            if (!_gpuProtectionQueued)
            {
                _gpuProtectionQueued = true;
                owner.QueueGpuProtection(this);
            }

            return;
        }

        ApplyGpuProtection();
    }

    public void ApplyGpuProtection()
    {
        _gpuProtectionQueued = false;
        var block = _readable & _gpuDirty;
        if (block.None)
        {
            return;
        }

        _readable = _readable ^ block;
        _pages.AddWatchMask(BaseAddress, block, blockReads: true);
    }

    private (int Start, int End) GetPageRange(ulong address, ulong size)
    {
        if (size == 0 || address < BaseAddress || address >= BaseAddress + BlockBytes || size > BaseAddress + BlockBytes - address)
        {
            PageGuard.OnFatal("The requested range is outside the tracking region.");
            return (0, 0);
        }

        var offset = address - BaseAddress;
        return ((int)(offset / PageBytes), (int)((offset + size + PageBytes - 1) / PageBytes));
    }
}
