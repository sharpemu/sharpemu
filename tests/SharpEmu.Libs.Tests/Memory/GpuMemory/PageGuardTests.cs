// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;

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
    public void Dispose_WithLiveWatchersIsFatal()
    {
        var guard = new PageGuard(_space);
        guard.AddWatch(0x40000, 0x1000, blockReads: false);

        guard.Dispose();

        Assert.Contains("Cannot release the page guard while page state is active.", _fatals);
    }
}
