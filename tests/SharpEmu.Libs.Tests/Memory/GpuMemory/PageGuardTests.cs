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

    // Blocks one protection call so a concurrent Reapply has to queue behind the watcher update.
    private sealed class BlockingAddressSpace : IGuestAddressSpace
    {
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
