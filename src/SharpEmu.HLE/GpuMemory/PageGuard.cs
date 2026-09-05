// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public enum FaultKind
{
    Read,
    Write,
    Execute,
    Unknown,
}

public sealed class PageGuard : IDisposable
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    private const ulong PageBytes = TrackerLayout.PageBytes;
    private const ulong BlockBytes = TrackerLayout.BlockBytes;
    private const ulong SpaceBytes = TrackerLayout.SpaceBytes;
    private const int PagesPerBlock = TrackerLayout.PagesPerBlock;

    private struct PageCounts
    {
        private byte _value;

        public readonly int WriteWatchCount => _value & 0x7f;

        public readonly int ReadWatchCount => _value >> 7;

        public readonly GuestPageProtection GetAllowedAccess()
        {
            if (ReadWatchCount != 0)
            {
                return GuestPageProtection.None;
            }

            if (WriteWatchCount != 0)
            {
                return GuestPageProtection.Read;
            }

            return GuestPageProtection.Read | GuestPageProtection.Write;
        }

        public int ChangeWatchCount(int delta, bool isRead, ulong address)
        {
            if (isRead)
            {
                if (delta == 1)
                {
                    if (ReadWatchCount != 0)
                    {
                        OnFatal($"Cannot add a read watch. The count is at its limit at 0x{address:X16}.");
                    }

                    _value |= 0x80;
                }
                else if (delta == -1)
                {
                    if (ReadWatchCount == 0)
                    {
                        OnFatal($"Cannot remove a read watch. The count is zero at 0x{address:X16}.");
                    }

                    _value &= 0x7f;
                }

                return ReadWatchCount;
            }

            if (delta == 1)
            {
                if (WriteWatchCount == 0x7f)
                {
                    OnFatal($"Cannot add a write watch. The count is at its limit at 0x{address:X16}.");
                }

                _value++;
            }
            else if (delta == -1)
            {
                if (WriteWatchCount == 0)
                {
                    OnFatal($"Cannot remove a write watch. The count is zero at 0x{address:X16}.");
                }

                _value--;
            }

            return WriteWatchCount;
        }
    }

    private sealed class PageBlock
    {
        public int Lock;
        public readonly PageCounts[] Pages = new PageCounts[PagesPerBlock];
    }

    // This lock runs in the fault handler. It must not allocate.
    private readonly ref struct BlockLock
    {
        private readonly PageBlock _block;

        public BlockLock(PageBlock block)
        {
            _block = block;
            while (Interlocked.CompareExchange(ref block.Lock, 1, 0) != 0)
            {
                Thread.SpinWait(1);
            }
        }

        public void Dispose() => Volatile.Write(ref _block.Lock, 0);
    }

    private readonly IGuestAddressSpace _addressSpace;
    private readonly PageBlock?[] _blocks = new PageBlock?[TrackerLayout.BlockCount];
    private readonly object _blockGate = new();

    public PageGuard(IGuestAddressSpace addressSpace)
    {
        if (Environment.SystemPageSize != (int)PageBytes)
        {
            OnFatal($"The host page size is not supported: 0x{Environment.SystemPageSize:X8}.");
        }

        _addressSpace = addressSpace;
    }

    public void Dispose()
    {
        foreach (var block in _blocks)
        {
            if (block == null)
            {
                continue;
            }

            using var _ = new BlockLock(block);
            foreach (var page in block.Pages)
            {
                if (page.WriteWatchCount != 0 || page.ReadWatchCount != 0)
                {
                    OnFatal("Cannot release the page guard while page state is active.");
                    return;
                }
            }
        }
    }

    public void AddWatch(ulong address, ulong size, bool blockReads) => UpdatePages(address, size, true, blockReads);

    public void RemoveWatch(ulong address, ulong size, bool blockReads) => UpdatePages(address, size, false, blockReads);

    public void AddWatchMask(ulong blockBase, in PageMask pages, bool blockReads) => UpdateMask(blockBase, pages, true, blockReads);

    public void RemoveWatchMask(ulong blockBase, in PageMask pages, bool blockReads) => UpdateMask(blockBase, pages, false, blockReads);

    public bool Allows(ulong address, FaultKind kind)
    {
        var block = FindBlock(address);
        if (block == null)
        {
            return true;
        }

        using var _ = new BlockLock(block);
        var allowedAccess = block.Pages[(int)(address % BlockBytes / PageBytes)].GetAllowedAccess();
        return kind == FaultKind.Write
            ? allowedAccess == (GuestPageProtection.Read | GuestPageProtection.Write)
            : allowedAccess != GuestPageProtection.None;
    }

    public void NoteMapped(ulong address, ulong size)
    {
    }

    public void NoteUnmapped(ulong address, ulong size)
    {
    }

    private PageBlock? FindBlock(ulong address) =>
        address < SpaceBytes ? Volatile.Read(ref _blocks[address / BlockBytes]) : null;

    private PageBlock GetOrCreateBlock(ulong address)
    {
        var index = address / BlockBytes;
        if (Volatile.Read(ref _blocks[index]) is { } block)
        {
            return block;
        }

        lock (_blockGate)
        {
            if (Volatile.Read(ref _blocks[index]) is { } existing)
            {
                return existing;
            }

            var created = new PageBlock();
            Volatile.Write(ref _blocks[index], created);
            return created;
        }
    }

    private void ApplyAccess(ulong address, ulong size, GuestPageProtection allowedAccess)
    {
        if (!_addressSpace.TryProtect(address, size, allowedAccess))
        {
            OnFatal($"Could not change page access at 0x{address:X16}, new=0x{(uint)allowedAccess:X8}.");
        }
    }

    private void UpdateBlock(PageBlock block, ulong blockBase, int first, int last, bool track, bool isRead, bool masked, in PageMask mask)
    {
        using var _ = new BlockLock(block);
        var pages = block.Pages;
        var allowedAccess = pages[first].GetAllowedAccess();
        var rangeBegin = 0;
        var rangeBytes = 0UL;
        var potentialRangeBytes = 0UL;

        void ReleasePending()
        {
            if (rangeBytes != 0)
            {
                ApplyAccess(blockBase + (ulong)rangeBegin * PageBytes, rangeBytes, allowedAccess);
                rangeBytes = 0;
                potentialRangeBytes = 0;
            }
        }

        for (var pageIndex = first; pageIndex < last; pageIndex++)
        {
            var address = blockBase + (ulong)pageIndex * PageBytes;
            var update = !masked || mask.Get(pageIndex);

            var oldAllowedAccess = pages[pageIndex].GetAllowedAccess();
            var newCount = pages[pageIndex].ChangeWatchCount(update ? (track ? 1 : -1) : 0, isRead, address);
            var newAllowedAccess = pages[pageIndex].GetAllowedAccess();

            if (newAllowedAccess != allowedAccess)
            {
                ReleasePending();
                allowedAccess = newAllowedAccess;
            }
            else if (rangeBytes != 0)
            {
                potentialRangeBytes += PageBytes;
            }

            if (!update)
            {
                continue;
            }

            var watcherEdge = (track && newCount == 1) || (!track && newCount == 0);
            if (watcherEdge && oldAllowedAccess != newAllowedAccess)
            {
                if (rangeBytes == 0)
                {
                    rangeBegin = pageIndex;
                    potentialRangeBytes = PageBytes;
                }

                rangeBytes = potentialRangeBytes;
            }
        }

        ReleasePending();
    }

    private void UpdatePages(ulong address, ulong size, bool track, bool isRead)
    {
        var begin = GetPageStart(address);
        var end = GetPageRangeEnd(address, size);
        for (var chunkBegin = begin; chunkBegin < end;)
        {
            var chunkEnd = Math.Min(end, (chunkBegin / BlockBytes + 1) * BlockBytes);
            var blockBase = chunkBegin / BlockBytes * BlockBytes;
            var block = track ? GetOrCreateBlock(chunkBegin) : FindBlock(chunkBegin);
            if (block == null)
            {
                OnFatal($"Cannot remove tracking for an unknown page at 0x{chunkBegin:X16}.");
                return;
            }

            var first = (int)((chunkBegin - blockBase) / PageBytes);
            var last = (int)((chunkEnd - blockBase) / PageBytes);
            UpdateBlock(block, blockBase, first, last, track, isRead, false, default);
            chunkBegin = chunkEnd;
        }
    }

    private void UpdateMask(ulong blockBase, in PageMask pages, bool track, bool isRead)
    {
        if (blockBase % BlockBytes != 0 || blockBase >= SpaceBytes)
        {
            OnFatal($"The tracking region starts at an invalid address: 0x{blockBase:X16}.");
            return;
        }

        var (first, firstEnd) = pages.FindFirstSetRange();
        var (_, last) = pages.FindLastSetRange();
        if (first == PagesPerBlock)
        {
            OnFatal("The region watch mask has no bits set.");
            return;
        }

        if (firstEnd == last)
        {
            UpdatePages(blockBase + (ulong)first * PageBytes, (ulong)(last - first) * PageBytes, track, isRead);
            return;
        }

        var block = track ? GetOrCreateBlock(blockBase) : FindBlock(blockBase);
        if (block == null)
        {
            OnFatal($"Cannot remove tracking for an unknown region at 0x{blockBase:X16}.");
            return;
        }

        UpdateBlock(block, blockBase, first, last, track, isRead, true, pages);
    }

    private static ulong GetPageStart(ulong address) => address & ~(PageBytes - 1);

    private static ulong GetPageRangeEnd(ulong address, ulong size)
    {
        if (address >= SpaceBytes || size == 0 || size > SpaceBytes - address)
        {
            OnFatal($"The memory range is invalid: vaddr=0x{address:X16}, size=0x{size:X16}.");
        }

        return GetPageStart(address + size - 1) + PageBytes;
    }
}
