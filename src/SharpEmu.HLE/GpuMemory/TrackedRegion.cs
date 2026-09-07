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
    private PageMask _writable;
    private PageMask _readable;

    public TrackedRegion(PageGuard pages, ulong baseAddress)
    {
        if (baseAddress % BlockBytes != 0)
        {
            PageGuard.OnFatal("Cannot create the tracking region with these parameters.");
        }

        _pages = pages;
        BaseAddress = baseAddress;
        _cpuDirty.Fill();
        _writable.Fill();
        _readable.Fill();
    }

    public RegionLock Lock { get; } = new();

    public ulong BaseAddress { get; }

    public bool IsModified(WriteOrigin side, ulong offset, ulong size)
    {
        var (start, end) = GetPageRange(BaseAddress + offset, size);
        return new PageMask(GetDirtyMask(side), start, end).Any;
    }

    public void ChangeState(WriteOrigin side, bool enable, ulong address, ulong size)
    {
        var (start, end) = GetPageRange(address, size);
        if (enable && side == WriteOrigin.Cpu && new PageMask(_gpuDirty, start, end).Any)
        {
            PageGuard.OnFatal("Cannot mark GPU-dirty pages as CPU-dirty.");
        }

        if (enable && side == WriteOrigin.Gpu && new PageMask(_cpuDirty, start, end).Any)
        {
            PageGuard.OnFatal("Cannot mark CPU-dirty pages as GPU-dirty.");
        }

        ref var bits = ref GetDirtyMask(side);
        if (enable)
        {
            bits.SetRange(start, end);
        }
        else
        {
            bits.UnsetRange(start, end);
        }

        if (side == WriteOrigin.Cpu)
        {
            UpdateCpuProtection(track: !enable);
        }
        else
        {
            UpdateGpuProtection(track: enable);
        }
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
                UpdateCpuProtection(track: true);
            }
            else
            {
                UpdateGpuProtection(track: false);
            }
        }

        foreach (var (runStart, runEnd) in mask)
        {
            visit(BaseAddress + (ulong)runStart * PageBytes, (ulong)(runEnd - runStart) * PageBytes);
        }
    }

    private ref PageMask GetDirtyMask(WriteOrigin side) => ref side == WriteOrigin.Cpu ? ref _cpuDirty : ref _gpuDirty;

    private void UpdateCpuProtection(bool track)
    {
        var mask = _cpuDirty ^ _writable;
        _writable = _cpuDirty;
        if (mask.None)
        {
            return;
        }

        if (track)
        {
            _pages.AddWatchMask(BaseAddress, mask, blockReads: false);
        }
        else
        {
            _pages.RemoveWatchMask(BaseAddress, mask, blockReads: false);
        }
    }

    private void UpdateGpuProtection(bool track)
    {
        var readable = ~_gpuDirty;
        var mask = readable ^ _readable;
        _readable = readable;
        if (mask.None)
        {
            return;
        }

        if (track)
        {
            _pages.AddWatchMask(BaseAddress, mask, blockReads: true);
        }
        else
        {
            _pages.RemoveWatchMask(BaseAddress, mask, blockReads: true);
        }
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
