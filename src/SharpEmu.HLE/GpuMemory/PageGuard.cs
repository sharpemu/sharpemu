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
    private const int MergeGapPages = 64;
    private static long _nextWriteRestorationVersion;

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
        public long[]? WriteRestorations;
    }

    // This lock runs in the fault handler. It must not allocate.
    private readonly ref struct BlockLock
    {
        private readonly PageBlock _block;

        public BlockLock(PageBlock block)
        {
            _block = block;
            if (Interlocked.CompareExchange(ref block.Lock, 1, 0) != 0)
            {
                using var profile = GpuMemoryAccessProfile.MeasureAccess(
                    GpuMemoryAccessProfile.Operation.PageLockWait, GpuMemoryAccessProfile.Operation.FaultPageLockWait);
                do { Thread.SpinWait(1); }
                while (Interlocked.CompareExchange(ref block.Lock, 1, 0) != 0);
            }
        }

        public void Dispose() => Volatile.Write(ref _block.Lock, 0);
    }

    private readonly IGuestAddressSpace _addressSpace;
    private readonly PageBlock?[] _blocks = new PageBlock?[TrackerLayout.BlockCount];
    private readonly object _blockGate = new();

    public PageGuard(IGuestAddressSpace addressSpace)
    {
        GpuMemoryAccessProfile.Initialize();
        ProtectionCauseCounts.Initialize();
        if (addressSpace.ProtectionPageSize != PageBytes)
        {
            OnFatal($"The host page size is not supported: 0x{addressSpace.ProtectionPageSize:X8}.");
        }

        _addressSpace = addressSpace;
    }

    public GuestPermissionLedger Permissions { get; } = new();

    public CpuStackRanges Stacks { get; } = new();

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

    public void AddWatch(ulong address, ulong size, bool blockReads) =>
        AddWatch(address, size, blockReads, DefaultCause(true, blockReads));

    public void AddWatch(ulong address, ulong size, bool blockReads, ProtectionCause cause) =>
        UpdatePages(address, size, true, blockReads, cause);

    public void RemoveWatch(ulong address, ulong size, bool blockReads) =>
        RemoveWatch(address, size, blockReads, DefaultCause(false, blockReads));

    public void RemoveWatch(ulong address, ulong size, bool blockReads, ProtectionCause cause) =>
        UpdatePages(address, size, false, blockReads, cause);

    public void AddWatchMask(ulong blockBase, in PageMask pages, bool blockReads) =>
        AddWatchMask(blockBase, pages, blockReads, DefaultCause(true, blockReads));

    public void AddWatchMask(ulong blockBase, in PageMask pages, bool blockReads, ProtectionCause cause) =>
        UpdateMask(blockBase, pages, true, blockReads, cause);

    public void RemoveWatchMask(ulong blockBase, in PageMask pages, bool blockReads) =>
        RemoveWatchMask(blockBase, pages, blockReads, DefaultCause(false, blockReads));

    public void RemoveWatchMask(ulong blockBase, in PageMask pages, bool blockReads, ProtectionCause cause) =>
        UpdateMask(blockBase, pages, false, blockReads, cause);

    private static ProtectionCause DefaultCause(bool track, bool blockReads) => (track, blockReads) switch
    {
        (true, true) => ProtectionCause.GpuWriteBlock,
        (false, true) => ProtectionCause.GpuReadRelease,
        (true, false) => ProtectionCause.UploadReprotect,
        _ => ProtectionCause.CpuWriteRelease,
    };

    // Watcher state only; the guest's own permissions are the ledger's.
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

    internal long GetWriteRestorationVersion(ulong address)
    {
        var block = FindBlock(address);
        if (block == null) return 0;
        using var held = new BlockLock(block);
        var pageIndex = (int)(address % BlockBytes / PageBytes);
        return (block.Pages[pageIndex].GetAllowedAccess() & GuestPageProtection.Write) != 0
            ? block.WriteRestorations?[pageIndex] ?? 0 : 0;
    }

    // A new mapping must not inherit recovery evidence from the previous mapping.
    internal void ClearWriteRestorations(ulong address, ulong size)
    {
        var end = GetPageRangeEnd(address, size);
        for (var cursor = GetPageStart(address); cursor < end;)
        {
            var blockEnd = Math.Min(end, (cursor / BlockBytes + 1) * BlockBytes);
            var block = FindBlock(cursor);
            if (block != null)
            {
                using var held = new BlockLock(block);
                if (block.WriteRestorations is { } restorations)
                    Array.Clear(restorations, (int)(cursor % BlockBytes / PageBytes), (int)((blockEnd - cursor) / PageBytes));
            }
            cursor = blockEnd;
        }
    }

    internal string DescribeWatchers(ulong address)
    {
        var block = FindBlock(address);
        if (block == null)
            return "read_watch=0 write_watch=0";
        using var held = new BlockLock(block);
        var counts = block.Pages[(int)(address % BlockBytes / PageBytes)];
        return $"read_watch={counts.ReadWatchCount} write_watch={counts.WriteWatchCount}";
    }

    // Applies the derived protection again after the ledger changed; the block lock keeps watcher updates out.
    public void Reapply(ulong address, ulong size)
    {
        var end = GetPageRangeEnd(address, size);
        for (var chunkBegin = GetPageStart(address); chunkBegin < end;)
        {
            var chunkEnd = Math.Min(end, (chunkBegin / BlockBytes + 1) * BlockBytes);
            var block = GetOrCreateBlock(chunkBegin);
            using (new BlockLock(block))
            {
                ApplyDerived(chunkBegin, chunkEnd, block);
            }

            chunkBegin = chunkEnd;
        }
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

    // Host protection is ledger ∩ watchers; execute needs a readable page on x86.
    private static GuestPageProtection Derive(GuestPageProtection guest, GuestPageProtection watchers)
    {
        if (watchers == GuestPageProtection.None)
        {
            return GuestPageProtection.None;
        }

        var derived = guest & (GuestPageProtection.Read | GuestPageProtection.Execute);
        if ((watchers & GuestPageProtection.Write) != 0)
        {
            derived |= guest & GuestPageProtection.Write;
        }

        return derived;
    }

    // Pages a thread runs on as its stack keep the guest's own access (CpuStackRanges).
    private void ApplyAccess(ulong address, ulong size, GuestPageProtection allowedAccess, ProtectionCause cause)
    {
        ProtectionCauseCounts.Record(cause, size);
        var stacks = Stacks.Snapshot;
        var cursor = address;
        var end = address + size;
        for (var index = CpuStackRanges.FirstEndingAfter(stacks, address); index < stacks.Length && stacks[index].Start < end; index++)
        {
            var stackStart = Math.Max(stacks[index].Start, cursor);
            var stackEnd = Math.Min(stacks[index].End, end);
            if (cursor < stackStart)
            {
                Protect(cursor, stackStart - cursor, allowedAccess);
            }

            for (var page = stackStart; page < stackEnd;)
            {
                var guest = Permissions.Lookup(page, out var guestValidEnd);
                var partEnd = Math.Min(guestValidEnd, stackEnd);
                Protect(page, partEnd - page, Derive(guest, GuestPageProtection.Read | GuestPageProtection.Write));
                page = partEnd;
            }

            cursor = stackEnd;
        }

        if (cursor < end)
        {
            Protect(cursor, end - cursor, allowedAccess);
        }
    }

    private void Protect(ulong address, ulong size, GuestPageProtection allowedAccess)
    {
        using var profile = GpuMemoryAccessProfile.MeasureAccess(
            GpuMemoryAccessProfile.Operation.HostProtection, GpuMemoryAccessProfile.Operation.FaultHostProtection, size);
        if (GuestGpuMemoryHook.Traces(address, size))
            GuestGpuMemoryHook.Trace(address, size, $"host-protect derived={allowedAccess}");
        if (!_addressSpace.TryProtect(address, size, allowedAccess))
        {
            OnFatal($"Could not change page access at 0x{address:X16}, new=0x{(uint)allowedAccess:X8}.");
        }
    }

    private struct GuestProtectionCursor
    {
        private ulong _validUntil;
        private GuestPageProtection _protection;

        public GuestPageProtection At(GuestPermissionLedger ledger, ulong address)
        {
            if (address >= _validUntil)
            {
                _protection = ledger.Lookup(address, out _validUntil);
            }

            return _protection;
        }
    }

    private void ApplyDerived(ulong begin, ulong end, PageBlock block)
    {
        var guest = default(GuestProtectionCursor);
        var runStart = begin;
        var runAllowedAccess = GuestPageProtection.None;
        for (var address = begin; address < end; address += PageBytes)
        {
            var watchers = block.Pages[(int)(address % BlockBytes / PageBytes)].GetAllowedAccess();
            var allowedAccess = Derive(guest.At(Permissions, address), watchers);
            if (address != begin && allowedAccess != runAllowedAccess)
            {
                ApplyAccess(runStart, address - runStart, runAllowedAccess, ProtectionCause.MappingReapply);
                runStart = address;
            }

            runAllowedAccess = allowedAccess;
        }

        ApplyAccess(runStart, end - runStart, runAllowedAccess, ProtectionCause.MappingReapply);
    }

    private struct BlockUpdate(ulong blockBase, bool track, bool isRead, ProtectionCause cause)
    {
        public readonly ulong BlockBase = blockBase;
        public readonly bool Track = track;
        public readonly bool IsRead = isRead;
        public readonly ProtectionCause Cause = cause;
        public GuestProtectionCursor Guest;
        public long RestorationVersion;
        public bool Pending;
        public int PendingBegin;
        public int PendingEnd;
        public int UniformEnd;
        public GuestPageProtection PendingAccess;
    }

    private void UpdateBlock(PageBlock block, ulong blockBase, int first, int last, bool track, bool isRead, bool masked, in PageMask mask, ProtectionCause cause)
    {
        using var _ = new BlockLock(block);
        // Allocate before adding protection, never while resolving a fault.
        if (track) block.WriteRestorations ??= new long[PagesPerBlock];
        var update = new BlockUpdate(blockBase, track, isRead, cause);
        if (masked)
        {
            foreach (var (start, end) in mask)
            {
                UpdateRun(block, ref update, start, end);
            }
        }
        else
        {
            UpdateRun(block, ref update, first, last);
        }

        ReleasePending(ref update);
    }

    private void UpdateRun(PageBlock block, ref BlockUpdate update, int start, int end)
    {
        var pages = block.Pages;
        if (update.Pending && update.UniformEnd < start && !ExtendAcrossGap(pages, ref update, start))
        {
            ReleasePending(ref update);
        }

        for (var pageIndex = start; pageIndex < end; pageIndex++)
        {
            var address = update.BlockBase + (ulong)pageIndex * PageBytes;
            var guest = update.Guest.At(Permissions, address);
            var oldAllowedAccess = Derive(guest, pages[pageIndex].GetAllowedAccess());
            pages[pageIndex].ChangeWatchCount(update.Track ? 1 : -1, update.IsRead, address);
            var newAllowedAccess = Derive(guest, pages[pageIndex].GetAllowedAccess());
            if (block.WriteRestorations is { } restorations)
            {
                if ((newAllowedAccess & GuestPageProtection.Write) == 0)
                {
                    restorations[pageIndex] = 0;
                }
                else if ((oldAllowedAccess & GuestPageProtection.Write) == 0)
                {
                    if (update.RestorationVersion == 0)
                        update.RestorationVersion = Interlocked.Increment(ref _nextWriteRestorationVersion);
                    restorations[pageIndex] = update.RestorationVersion;
                }
            }
            if (GuestGpuMemoryHook.Traces(address, PageBytes))
                GuestGpuMemoryHook.Trace(address, PageBytes,
                    $"watch track={update.Track} block_reads={update.IsRead} guest={guest} read_watch={pages[pageIndex].ReadWatchCount} write_watch={pages[pageIndex].WriteWatchCount} old={oldAllowedAccess} new={newAllowedAccess}");

            if (update.Pending)
            {
                if (newAllowedAccess == update.PendingAccess)
                {
                    update.UniformEnd = pageIndex + 1;
                    if (newAllowedAccess != oldAllowedAccess)
                    {
                        update.PendingEnd = pageIndex + 1;
                    }

                    continue;
                }

                ReleasePending(ref update);
            }

            if (newAllowedAccess != oldAllowedAccess)
            {
                update.Pending = true;
                update.PendingBegin = pageIndex;
                update.PendingEnd = pageIndex + 1;
                update.UniformEnd = pageIndex + 1;
                update.PendingAccess = newAllowedAccess;
            }
        }
    }

    private bool ExtendAcrossGap(PageCounts[] pages, ref BlockUpdate update, int start)
    {
        if (start - update.UniformEnd > MergeGapPages)
        {
            return false;
        }

        for (var pageIndex = update.UniformEnd; pageIndex < start; pageIndex++)
        {
            var address = update.BlockBase + (ulong)pageIndex * PageBytes;
            if (Derive(update.Guest.At(Permissions, address), pages[pageIndex].GetAllowedAccess()) != update.PendingAccess)
            {
                return false;
            }
        }

        update.UniformEnd = start;
        return true;
    }

    private void ReleasePending(ref BlockUpdate update)
    {
        if (!update.Pending)
        {
            return;
        }

        update.Pending = false;
        ApplyAccess(
            update.BlockBase + (ulong)update.PendingBegin * PageBytes,
            (ulong)(update.PendingEnd - update.PendingBegin) * PageBytes,
            update.PendingAccess,
            update.Cause);
    }

    private void UpdatePages(ulong address, ulong size, bool track, bool isRead, ProtectionCause cause)
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
            UpdateBlock(block, blockBase, first, last, track, isRead, false, default, cause);
            chunkBegin = chunkEnd;
        }
    }

    private void UpdateMask(ulong blockBase, in PageMask pages, bool track, bool isRead, ProtectionCause cause)
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
            UpdatePages(blockBase + (ulong)first * PageBytes, (ulong)(last - first) * PageBytes, track, isRead, cause);
            return;
        }

        var block = track ? GetOrCreateBlock(blockBase) : FindBlock(blockBase);
        if (block == null)
        {
            OnFatal($"Cannot remove tracking for an unknown region at 0x{blockBase:X16}.");
            return;
        }

        UpdateBlock(block, blockBase, first, last, track, isRead, true, pages, cause);
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
