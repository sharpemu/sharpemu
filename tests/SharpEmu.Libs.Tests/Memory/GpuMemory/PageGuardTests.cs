// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

[Collection(GpuMemoryStateCollection.Name)]
public sealed class PageGuardTests : IDisposable
{
    private const GuestPageProtection ReadWrite = GuestPageProtection.Read | GuestPageProtection.Write;
    private const ulong Block = 4UL * 1024 * 1024;

    private readonly Action<string> _previousFatal = PageGuard.OnFatal;
    private readonly List<string> _fatals = new();
    private readonly RecordingAddressSpace _space = new();

    public PageGuardTests()
    {
        PageGuard.OnFatal = _fatals.Add;
    }

    public void Dispose()
    {
        PageGuard.OnFatal = _previousFatal;
    }

    [Fact]
    public void ConstructorRejectsUnsupportedProtectionGranularity()
    {
        using var guard = new PageGuard(new RecordingAddressSpace { ProtectionPageSize = 16384 });
        Assert.Equal("The host page size is not supported: 0x00004000.", Assert.Single(_fatals));
    }

    [Fact]
    public void AddWatch_CoalescesPagesIntoOneProtectCall()
    {
        using var guard = new PageGuard(_space);

        guard.AddWatch(0x10000, 0x3000, blockReads: false);

        Assert.Equal(new[] { (0x10000UL, 0x3000UL, GuestPageProtection.Read) }, _space.Protects);

        guard.AddWatch(0x11000, 0x1000, blockReads: false);
        Assert.Single(_space.Protects);

        guard.RemoveWatch(0x11000, 0x1000, blockReads: false);
        Assert.Single(_space.Protects);

        guard.RemoveWatch(0x10000, 0x3000, blockReads: false);
        Assert.Equal((0x10000UL, 0x3000UL, ReadWrite), _space.Protects[1]);
        Assert.Empty(_fatals);
    }

    [Fact]
    public void AddWatch_BlockReadsRemovesAllAccess()
    {
        using var guard = new PageGuard(_space);

        guard.AddWatch(0x10000, 0x1000, blockReads: true);
        Assert.Equal((0x10000UL, 0x1000UL, GuestPageProtection.None), _space.Protects[0]);

        guard.AddWatch(0x10000, 0x1000, blockReads: false);
        Assert.Single(_space.Protects);

        guard.RemoveWatch(0x10000, 0x1000, blockReads: true);
        Assert.Equal((0x10000UL, 0x1000UL, GuestPageProtection.Read), _space.Protects[1]);

        guard.RemoveWatch(0x10000, 0x1000, blockReads: false);
        Assert.Equal((0x10000UL, 0x1000UL, ReadWrite), _space.Protects[2]);
    }

    [Fact]
    public void AddWatch_SplitsAtBlockBoundaryAndAlignsInput()
    {
        using var guard = new PageGuard(_space);

        guard.AddWatch(Block - 0x800, 0x1000, blockReads: false);

        Assert.Equal(
            new[]
            {
                (Block - 0x1000, 0x1000UL, GuestPageProtection.Read),
                (Block, 0x1000UL, GuestPageProtection.Read),
            },
            _space.Protects);

        guard.RemoveWatch(Block - 0x800, 0x1000, blockReads: false);
    }

    [Fact]
    public void MaskedRestorationRecordsOnlyThePagesWhoseWriteAccessChanged()
    {
        using var guard = new PageGuard(_space);
        var mask = new PageMask();
        mask.SetRange(1, 2);
        mask.SetRange(3, 4);
        guard.AddWatchMask(Block, mask, blockReads: false);
        Assert.Equal(0, guard.GetWriteRestorationVersion(Block + 0x1000));
        guard.RemoveWatchMask(Block, mask, blockReads: false);
        Assert.NotEqual(0, guard.GetWriteRestorationVersion(Block + 0x1000));
        Assert.Equal(0, guard.GetWriteRestorationVersion(Block + 0x2000));
        Assert.NotEqual(0, guard.GetWriteRestorationVersion(Block + 0x3000));
        guard.ClearWriteRestorations(Block + 0x1000, 0x1000);
        Assert.Equal(0, guard.GetWriteRestorationVersion(Block + 0x1000));
        Assert.NotEqual(0, guard.GetWriteRestorationVersion(Block + 0x3000));
    }

    [Fact]
    public void AddWatchMask_ProtectsEachRunSeparately()
    {
        using var guard = new PageGuard(_space);
        var mask = new PageMask();
        mask.SetRange(1, 3);
        mask.SetRange(5, 6);

        guard.AddWatchMask(Block, mask, blockReads: false);

        Assert.Equal(
            new[]
            {
                (Block + 0x1000, 0x2000UL, GuestPageProtection.Read),
                (Block + 0x5000, 0x1000UL, GuestPageProtection.Read),
            },
            _space.Protects);

        guard.RemoveWatchMask(Block, mask, blockReads: false);
        Assert.Equal(3, _space.Protects.Count);
        Assert.Equal((Block + 0x1000, 0x5000UL, ReadWrite), _space.Protects[2]);
    }

    [Fact]
    public void WatchMask_SparsePagesProtectOnePageEachWay()
    {
        using var guard = new PageGuard(_space);
        var mask = new PageMask();
        mask.Set(2);
        mask.Set(900);

        guard.AddWatchMask(Block, mask, blockReads: false);
        guard.RemoveWatchMask(Block, mask, blockReads: false);

        Assert.Equal(
            new[]
            {
                (Block + 0x2000, 0x1000UL, GuestPageProtection.Read),
                (Block + 0x384000, 0x1000UL, GuestPageProtection.Read),
                (Block + 0x2000, 0x1000UL, ReadWrite),
                (Block + 0x384000, 0x1000UL, ReadWrite),
            },
            _space.Protects);
        Assert.Empty(_fatals);
    }

    [Fact]
    public void RemoveWatchMask_MergesAcrossAShortGapWithTheSameAccess()
    {
        using var guard = new PageGuard(_space);
        var mask = new PageMask();
        mask.Set(1);
        mask.Set(10);
        guard.AddWatchMask(Block, mask, blockReads: false);
        Assert.Equal(2, _space.Protects.Count);

        guard.RemoveWatchMask(Block, mask, blockReads: false);

        Assert.Equal(3, _space.Protects.Count);
        Assert.Equal((Block + 0x1000, 0xA000UL, ReadWrite), _space.Protects[2]);
    }

    [Fact]
    public void RemoveWatchMask_DoesNotMergeAcrossAGapWithOtherAccess()
    {
        using var guard = new PageGuard(_space);
        var mask = new PageMask();
        mask.Set(1);
        mask.Set(10);
        guard.AddWatchMask(Block, mask, blockReads: false);
        guard.AddWatch(Block + 0x5000, 0x1000, blockReads: false);
        _space.Protects.Clear();

        guard.RemoveWatchMask(Block, mask, blockReads: false);

        Assert.Equal(
            new[] { (Block + 0x1000, 0x1000UL, ReadWrite), (Block + 0xA000, 0x1000UL, ReadWrite) },
            _space.Protects);
        guard.RemoveWatch(Block + 0x5000, 0x1000, blockReads: false);
    }

    [Fact]
    public void AddWatch_LedgerSpanEndingInsideTheRunSplitsTheProtection()
    {
        using var guard = new PageGuard(_space);
        guard.Permissions.Set(0x10000, 0x2000, ReadWrite);
        guard.Permissions.Set(0x12000, 0x1000, GuestPageProtection.Read | GuestPageProtection.Execute);
        guard.Permissions.Set(0x13000, 0x1000, ReadWrite);

        guard.AddWatch(0x10000, 0x5000, blockReads: false);

        Assert.Equal(
            new[] { (0x10000UL, 0x2000UL, GuestPageProtection.Read), (0x13000UL, 0x2000UL, GuestPageProtection.Read) },
            _space.Protects);
        guard.RemoveWatch(0x10000, 0x5000, blockReads: false);
        Assert.Equal(
            new[] { (0x10000UL, 0x2000UL, ReadWrite), (0x13000UL, 0x2000UL, ReadWrite) },
            _space.Protects.Skip(2));
    }

    [Fact]
    public void WatchMask_ReadAndWriteWatchesOnTheSamePagesKeepTheStrongest()
    {
        using var guard = new PageGuard(_space);
        var mask = new PageMask();
        mask.SetRange(0, 2);
        mask.SetRange(4, 6);
        guard.AddWatch(Block + 0x1000, 0x4000, blockReads: true);
        _space.Protects.Clear();

        guard.AddWatchMask(Block, mask, blockReads: false);
        Assert.Equal(new[] { (Block, 0x1000UL, GuestPageProtection.Read), (Block + 0x5000, 0x1000UL, GuestPageProtection.Read) }, _space.Protects);

        guard.RemoveWatch(Block + 0x1000, 0x4000, blockReads: true);
        Assert.Equal(
            new[] { (Block + 0x1000, 0x1000UL, GuestPageProtection.Read), (Block + 0x2000, 0x2000UL, ReadWrite), (Block + 0x4000, 0x1000UL, GuestPageProtection.Read) },
            _space.Protects.Skip(2));

        guard.RemoveWatchMask(Block, mask, blockReads: false);
        Assert.Equal(new[] { (Block, 0x6000UL, ReadWrite) }, _space.Protects.Skip(5));
        Assert.Empty(_fatals);
    }

    [Fact]
    public void ProtectionCalls_AreCountedByCause()
    {
        using var guard = new PageGuard(_space);
        var sweepCalls = ProtectionCauseCounts.CallsOf(ProtectionCause.SweepReprotect);
        var sweepPages = ProtectionCauseCounts.PagesOf(ProtectionCause.SweepReprotect);
        var releases = ProtectionCauseCounts.CallsOf(ProtectionCause.CpuWriteRelease);
        var reapplies = ProtectionCauseCounts.CallsOf(ProtectionCause.MappingReapply);

        guard.AddWatch(0x60000, 0x3000, blockReads: false, ProtectionCause.SweepReprotect);
        guard.Permissions.Set(0x60000, 0x1000, GuestPageProtection.Read);
        guard.Reapply(0x60000, 0x1000);
        guard.RemoveWatch(0x60000, 0x3000, blockReads: false);

        Assert.Equal(1, ProtectionCauseCounts.CallsOf(ProtectionCause.SweepReprotect) - sweepCalls);
        Assert.Equal(3, ProtectionCauseCounts.PagesOf(ProtectionCause.SweepReprotect) - sweepPages);
        Assert.Equal(1, ProtectionCauseCounts.CallsOf(ProtectionCause.MappingReapply) - reapplies);
        Assert.Equal(1, ProtectionCauseCounts.CallsOf(ProtectionCause.CpuWriteRelease) - releases);
        Assert.Contains("protect_sweep_reprotect=", ProtectionCauseCounts.TakeReport(), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveWatch_OnUnknownPageIsFatal()
    {
        using var guard = new PageGuard(_space);

        guard.RemoveWatch(0x20000, 0x1000, blockReads: false);

        Assert.Contains(_fatals, message => message.StartsWith("Cannot remove tracking for an unknown page", StringComparison.Ordinal));
        Assert.Empty(_space.Protects);
    }

    [Fact]
    public void AddWatch_FailedProtectIsFatal()
    {
        var guard = new PageGuard(_space);
        _space.FailProtect = true;

        guard.AddWatch(0x30000, 0x1000, blockReads: false);

        Assert.Contains(_fatals, message => message.StartsWith("Could not change page access", StringComparison.Ordinal));
        _space.FailProtect = false;
        guard.RemoveWatch(0x30000, 0x1000, blockReads: false);
        guard.Dispose();
    }

    [Fact]
    public void Watchers_NeverWidenTheGuestProtection()
    {
        using var guard = new PageGuard(_space);
        guard.Permissions.Set(0x10000, 0x1000, GuestPageProtection.Read);
        guard.Permissions.Set(0x11000, 0x1000, GuestPageProtection.Read | GuestPageProtection.Execute);
        guard.Permissions.Set(0x12000, 0x1000, GuestPageProtection.None);

        guard.AddWatch(0x10000, 0x3000, blockReads: false);
        Assert.Empty(_space.Protects);

        guard.AddWatch(0x10000, 0x3000, blockReads: true);
        Assert.Equal(new[] { (0x10000UL, 0x2000UL, GuestPageProtection.None) }, _space.Protects);

        guard.RemoveWatch(0x10000, 0x3000, blockReads: true);
        Assert.Equal((0x10000UL, 0x1000UL, GuestPageProtection.Read), _space.Protects[1]);
        Assert.Equal((0x11000UL, 0x1000UL, GuestPageProtection.Read | GuestPageProtection.Execute), _space.Protects[2]);

        guard.RemoveWatch(0x10000, 0x3000, blockReads: false);
        Assert.Equal(3, _space.Protects.Count);
        Assert.Empty(_fatals);
    }

    [Fact]
    public void Reapply_DerivesTheProtectionAgainAfterTheLedgerChanged()
    {
        using var guard = new PageGuard(_space);
        guard.AddWatch(0x20000, 0x2000, blockReads: false);
        Assert.Equal(new[] { (0x20000UL, 0x2000UL, GuestPageProtection.Read) }, _space.Protects);

        guard.Permissions.Set(0x20000, 0x1000, GuestPageProtection.Read | GuestPageProtection.Execute);
        guard.Reapply(0x20000, 0x2000);
        Assert.Equal((0x20000UL, 0x1000UL, GuestPageProtection.Read | GuestPageProtection.Execute), _space.Protects[1]);
        Assert.Equal((0x21000UL, 0x1000UL, GuestPageProtection.Read), _space.Protects[2]);

        guard.RemoveWatch(0x20000, 0x2000, blockReads: false);
        Assert.Equal((0x21000UL, 0x1000UL, ReadWrite), _space.Protects[3]);
        Assert.Equal(4, _space.Protects.Count);

        guard.Permissions.Set(0x30000, 0x1000, GuestPageProtection.None);
        guard.Reapply(0x30000, 0x2000);
        Assert.Equal((0x30000UL, 0x1000UL, GuestPageProtection.None), _space.Protects[4]);
        Assert.Equal((0x31000UL, 0x1000UL, ReadWrite), _space.Protects[5]);
    }

    // The guard looks a ledger record up once for the neighbouring pages it covers. Each page must still
    // get the protection of its own record and watchers: these records end inside pages, touch one
    // another and leave gaps.
    [Theory]
    [InlineData(3)]
    [InlineData(29)]
    public void WatchesAndReapplyDeriveEachPageFromItsOwnRecord(int seed)
    {
        const int pageCount = 64;
        const ulong pageBytes = 0x1000;
        using var guard = new PageGuard(_space);
        var random = new System.Random(seed);
        GuestPageProtection[] choices =
        [
            GuestPageProtection.None, GuestPageProtection.Read, GuestPageProtection.Read | GuestPageProtection.Execute,
            ReadWrite, ReadWrite | GuestPageProtection.Execute,
        ];
        // The guest protection of each half page; a page derives from the record at its start.
        var guest = Enumerable.Repeat(ReadWrite, pageCount * 2).ToArray();
        var writeWatches = new int[pageCount];
        var readWatched = new bool[pageCount];
        var host = new GuestPageProtection?[pageCount];
        var replayed = 0;

        void SetRecords(int count)
        {
            for (var record = 0; record < count; record++)
            {
                var firstHalf = random.Next(pageCount * 2);
                var halves = random.Next(1, Math.Min(12, pageCount * 2 - firstHalf) + 1);
                var protection = choices[random.Next(choices.Length)];
                guard.Permissions.Set(Block + (ulong)firstHalf * 0x800, (ulong)halves * 0x800, protection);
                Array.Fill(guest, protection, firstHalf, halves);
            }

            guard.Reapply(Block, pageCount * pageBytes);
        }

        void Check()
        {
            for (; replayed < _space.Protects.Count; replayed++)
            {
                var (address, size, protection) = _space.Protects[replayed];
                for (var offset = address - Block; offset < address - Block + size; offset += pageBytes)
                    host[offset / pageBytes] = protection;
            }

            for (var page = 0; page < pageCount; page++)
            {
                var watchers = readWatched[page] ? GuestPageProtection.None
                    : writeWatches[page] != 0 ? GuestPageProtection.Read : ReadWrite;
                Assert.Equal(Derive(guest[page * 2], watchers), host[page]);
            }
        }

        void Watch(int start, int end, bool add)
        {
            if (add)
                guard.AddWatch(Block + (ulong)start * pageBytes, (ulong)(end - start) * pageBytes, blockReads: false);
            else
                guard.RemoveWatch(Block + (ulong)start * pageBytes, (ulong)(end - start) * pageBytes, blockReads: false);
            for (var page = start; page < end; page++)
                writeWatches[page] += add ? 1 : -1;
        }

        SetRecords(24);
        Check();
        Watch(0, pageCount, add: true);
        Check();
        Watch(5, 40, add: true);
        Check();

        var mask = new PageMask();
        for (var page = 0; page < pageCount; page++)
        {
            if (random.Next(3) == 0)
            {
                mask.Set(page);
                readWatched[page] = true;
            }
        }

        // At least two runs, so the masked walk runs.
        mask.Set(1);
        mask.Unset(2);
        mask.Set(40);
        readWatched[1] = true;
        readWatched[2] = false;
        readWatched[40] = true;
        guard.AddWatchMask(Block, mask, blockReads: true);
        Check();
        SetRecords(8);
        Check();
        guard.RemoveWatchMask(Block, mask, blockReads: true);
        Array.Clear(readWatched);
        Check();
        Watch(0, pageCount, add: false);
        Check();
        SetRecords(8);
        Check();
        Watch(5, 40, add: false);
        Check();
        Assert.Empty(_fatals);
    }

    private static GuestPageProtection Derive(GuestPageProtection guest, GuestPageProtection watchers)
    {
        if (watchers == GuestPageProtection.None)
            return GuestPageProtection.None;
        var derived = guest & (GuestPageProtection.Read | GuestPageProtection.Execute);
        return (watchers & GuestPageProtection.Write) != 0 ? derived | (guest & GuestPageProtection.Write) : derived;
    }

    // Blocks one protection call so a concurrent Reapply has to queue behind the watcher update.
    private sealed class BlockingAddressSpace : IGuestAddressSpace
    {
        public ulong ProtectionPageSize => 4096;

        public List<(ulong Address, ulong Size, GuestPageProtection Protection)> Protects { get; } = new();

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int BlockThread { get; set; }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
        {
            if (Environment.CurrentManagedThreadId == BlockThread)
            {
                Entered.Set();
                Release.Wait();
            }

            lock (Protects)
            {
                Protects.Add((address, size, protection));
            }

            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) => throw new NotSupportedException();

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => throw new NotSupportedException();

        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress) => throw new NotSupportedException();

        public bool TryEnsureRangeCommitted(ulong address, ulong size) => throw new NotSupportedException();

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => throw new NotSupportedException();

        public bool TryFreeGuestMemory(ulong address) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Reapply_QueuesBehindTheFirstWatcherOfTheBlock()
    {
        var space = new BlockingAddressSpace();
        using var guard = new PageGuard(space);
        guard.Permissions.Set(0x50000, 0x1000, ReadWrite);
        var watcher = new Thread(() => guard.AddWatch(0x50000, 0x1000, blockReads: true));
        space.BlockThread = watcher.ManagedThreadId;
        watcher.Start();
        Assert.True(space.Entered.Wait(5000));

        var reapply = Task.Run(() =>
        {
            guard.Permissions.Set(0x50000, 0x1000, GuestPageProtection.Read);
            guard.Reapply(0x50000, 0x1000);
        });
        Assert.False(await CompletesWithin(reapply, 200));

        space.Release.Set();
        watcher.Join();
        Assert.True(await CompletesWithin(reapply, 5000));

        Assert.Equal(new[] { (0x50000UL, 0x1000UL, GuestPageProtection.None), (0x50000UL, 0x1000UL, GuestPageProtection.None) }, space.Protects);
        guard.RemoveWatch(0x50000, 0x1000, blockReads: true);
        Assert.Equal((0x50000UL, 0x1000UL, GuestPageProtection.Read), space.Protects[2]);
    }

    [Fact]
    public void Dispose_WithLiveWatchersIsFatal()
    {
        var guard = new PageGuard(_space);
        guard.AddWatch(0x40000, 0x1000, blockReads: false);

        guard.Dispose();

        Assert.Contains("Cannot release the page guard while page state is active.", _fatals);
    }
}
