// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;
using Op = SharpEmu.Libs.Tests.Memory.GuestMemory.FailingHostViews.Op;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[Collection(GuestMemoryStateCollection.Name)]
public sealed unsafe partial class GuestSpaceOwnerTests
{
    private const ulong Page = GuestSpaceOwner.GuestPage;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReserveFreeRangeFillsGapsAndRollsBackFailedReservations(bool failSecondGap)
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var granularity = Math.Max(host.Granularity, Page);
        var address = ProbeGuestAddress(host, 5 * granularity);
        Assert.True(owner.TryReserveAddressRange(address + 2 * granularity, granularity));
        host.Log.Clear();
        if (failSecondGap)
        {
            host.FailNext(Op.ReserveHole, afterCalls: 1);
            Assert.False(owner.TryReserveFreeRange(address, 5 * granularity));
            Assert.Equal(new[] { Op.ReserveHole, Op.ReserveHole, Op.FreeHole }, host.Log);
            Assert.True(owner.ContainsFreeRange(address + 2 * granularity, granularity));
            Assert.Equal(address, host.ReserveHole(address, 2 * granularity));
            Assert.True(host.FreeHole(address, 2 * granularity));
        }

        Assert.True(owner.TryReserveFreeRange(address, 5 * granularity));
        Assert.True(owner.ContainsFreeRange(address, 5 * granularity));
        Assert.True(owner.MapShared(address, 5 * granularity, 0, HostPageProtection.ReadWrite, out _));
        *(ulong*)address = Marker;
        Assert.Equal(Marker, *(ulong*)owner.AliasBase);
        Assert.True(owner.UnmapShared(address, 5 * granularity));
        owner.ReleaseAddressRanges();
        Assert.Equal(address, host.ReserveHole(address, 5 * granularity));
        Assert.True(host.FreeHole(address, 5 * granularity));
    }

    [Fact]
    public void ReserveFreeRangeKeepsLiveMappingsInsideHostAlignmentPadding()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var holeSize = HoleSize(host);
        var address = ProbeGuestAddress(host, 3 * holeSize);
        Assert.True(owner.TryReserveAddressRange(address, holeSize), "The initial reservation failed.");
        Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _), "The initial view failed.");
        *(ulong*)address = Marker;
        host.Log.Clear();
        Assert.False(owner.TryReserveFreeRange(address, 3 * holeSize));
        Assert.Empty(host.Log);
        Assert.True(owner.TryReserveFreeRange(address + Page, 3 * holeSize - Page), "The reservation extension failed.");
        Assert.Equal(Marker, *(ulong*)address);
        Assert.True(owner.UnmapShared(address, Page), "The view release failed.");
        Assert.True(owner.TryReserveFreeRange(address, 3 * holeSize), "The complete free range was not retained.");
    }

    [Fact]
    public void ReserveFreeRangeDoesNotReplaceForeignReservations()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var holeSize = HoleSize(host);
        var address = ProbeGuestAddress(host, 3 * holeSize);
        Assert.True(owner.TryReserveAddressRange(address, holeSize));
        var foreignAddress = address + 2 * holeSize;
        Assert.Equal(foreignAddress, host.ReserveHole(foreignAddress, holeSize));
        try
        {
            Assert.False(owner.TryReserveFreeRange(address, 3 * holeSize));
            Assert.True(owner.ContainsFreeRange(address, holeSize));
            Assert.False(owner.ContainsFreeRange(foreignAddress, holeSize));
        }
        finally
        {
            Assert.True(host.FreeHole(foreignAddress, holeSize));
        }
    }

    private static ulong AcquireRange(GuestSpaceOwner owner, IHostViewMemory host, ulong size)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var probe = ProbeGuestAddress(host, size);
            if (owner.TryReserveAddressRange(probe, size))
            {
                return probe;
            }
        }

        Assert.Fail("no range could be acquired after 8 attempts");
        return 0;
    }

    [Fact]
    public void SelfTest_RoundTripsAPrivatePage()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);

        var address = owner.FindFreeAddress(baseAddress, baseAddress + hole, Page, Page);
        Assert.Equal(baseAddress, address);
        Assert.True(owner.AllocatePrivate(address, Page, HostPageProtection.ReadWrite));
        *(ulong*)address = Marker;
        Assert.Equal(Marker, *(ulong*)address);
        Assert.True(owner.FreePrivate(address, Page));
        Assert.True(owner.ContainsFreeRange(address, Page));
        Assert.True(owner.ContainsFreeRange(baseAddress, hole));
    }

    [Fact]
    public void MapShared_TwoRangesShareOneOffset()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var first = AcquireRange(owner, host, hole);
        var second = AcquireRange(owner, host, hole);

        Assert.True(owner.MapShared(first, Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.MapShared(second, Page, 0, HostPageProtection.ReadWrite, out _));
        *(ulong*)first = Marker;
        Assert.Equal(Marker, *(ulong*)second);
        Assert.True(owner.IsBacked(first, Page));

        Assert.True(owner.UnmapShared(first, Page));
        Assert.Equal(Marker, *(ulong*)second);
        Assert.False(owner.IsBacked(first, Page));
        Assert.True(owner.ContainsFreeRange(first, hole));
        Assert.Equal(first, owner.FindFreeAddress(first, first + hole, hole, Page));
        Assert.True(owner.UnmapShared(second, Page));
    }

    [Fact]
    public void OwnershipRejections_NeverReachTheHost()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var owned = AcquireRange(owner, host, hole);
        var foreign = ProbeGuestAddress(host, hole);
        Assert.True(owner.MapShared(owned, Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.AllocatePrivate(owned + Page, Page, HostPageProtection.ReadWrite));
        host.Log.Clear();

        Assert.False(owner.MapShared(foreign, Page, 0, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.AddressUnavailable, failure);
        Assert.False(owner.UnmapShared(owned + 2 * Page, Page));
        Assert.False(owner.FreePrivate(owned + 2 * Page, Page));
        Assert.False(owner.MapShared(owned + Page, Page, 0, HostPageProtection.ReadWrite, out failure));
        Assert.Equal(HostViewFailure.AddressUnavailable, failure);
        Assert.False(owner.AllocatePrivate(owned, Page, HostPageProtection.ReadWrite));
        Assert.False(owner.UnmapShared(owned + Page, Page));
        Assert.False(owner.FreePrivate(owned, Page));
        Assert.Empty(host.Log);

        Assert.True(owner.UnmapShared(owned, Page));
        Assert.True(owner.FreePrivate(owned + Page, Page));
    }

    [Fact]
    public void SplitAndCoalesce_FollowTheHoleOperations()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);
        host.Log.Clear();

        Assert.True(owner.MapShared(baseAddress + Page, Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.Equal(new[] { Op.SplitHole, Op.SplitHole, Op.MapView }, host.Log);
        Assert.True(owner.ContainsFreeRange(baseAddress, Page));
        Assert.True(owner.ContainsFreeRange(baseAddress + 2 * Page, hole - 2 * Page));
        Assert.False(owner.ContainsFreeRange(baseAddress, hole));

        host.Log.Clear();
        Assert.True(owner.UnmapShared(baseAddress + Page, Page));
        Assert.Equal(new[] { Op.UnmapView, Op.JoinHoles }, host.Log);
        Assert.True(owner.ContainsFreeRange(baseAddress, hole));
        Assert.Equal(baseAddress, owner.FindFreeAddress(baseAddress, baseAddress + hole, hole, Page));
    }

    [Fact]
    public void SetTransientAccess_TouchesOnlyMappedOverlaps()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);
        Assert.True(owner.MapShared(baseAddress, Page, 0, HostPageProtection.ReadWrite, out _));
        host.Log.Clear();

        Assert.True(owner.SetTransientAccess(baseAddress, 2 * Page, HostPageProtection.ReadOnly));
        Assert.Equal(new[] { Op.ChangeAccess }, host.Log);
        *(ulong*)owner.AliasBase = Marker;
        Assert.Equal(Marker, *(ulong*)baseAddress);

        host.Log.Clear();
        Assert.True(owner.SetTransientAccess(baseAddress + 2 * Page, Page, HostPageProtection.NoAccess));
        Assert.Empty(host.Log);

        Assert.True(owner.SetAccess(baseAddress, Page, HostPageProtection.ReadWrite));
        Assert.True(owner.UnmapShared(baseAddress, Page));
    }

    [Fact]
    public void FailedTransientAccessReleasesLockAndKeepsMapping()
    {
        if (!Supported) return;
        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var address = AcquireRange(owner, host, HoleSize(host));
        Assert.True(owner.MapShared(address, Page, 0, HostPageProtection.ReadWrite, out _));
        *(ulong*)address = Marker;

        host.FailNext(Op.ChangeAccess);
        Assert.False(owner.SetTransientAccess(address, Page, HostPageProtection.NoAccess));
        Assert.Equal(Marker, *(ulong*)address);
        var protectionApplied = false;
        var retry = new Thread(() => protectionApplied = owner.SetTransientAccess(address, Page, HostPageProtection.ReadOnly))
        {
            IsBackground = true,
        };
        retry.Start();
        Assert.True(retry.Join(TimeSpan.FromSeconds(10)), "The failed protection call retained the owner lock.");
        Assert.True(protectionApplied);
        Assert.Equal(Marker, *(ulong*)address);
        Assert.True(owner.SetTransientAccess(address, Page, HostPageProtection.ReadWrite));
        Assert.True(owner.UnmapShared(address, Page));
    }

    [Fact]
    public void FreePrivate_AcrossTwoAllocationsReleasesBoth()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);
        Assert.True(owner.AllocatePrivate(baseAddress, Page, HostPageProtection.ReadWrite));
        Assert.True(owner.AllocatePrivate(baseAddress + Page, Page, HostPageProtection.ReadWrite));
        host.Log.Clear();

        Assert.True(owner.FreePrivate(baseAddress, 2 * Page));
        Assert.Equal(2, host.Log.Count(op => op == Op.ReleasePrivate));
        Assert.Contains(Op.JoinHoles, host.Log);
        Assert.True(owner.ContainsFreeRange(baseAddress, hole));
        Assert.Equal(baseAddress, owner.FindFreeAddress(baseAddress, baseAddress + hole, hole, Page));
    }

    [Fact]
    public void InjectedHostFailures_ReturnRangesToFree()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);

        host.FailNext(Op.MapView, failure: HostViewFailure.PlaceholderMapFailed);
        Assert.False(owner.MapShared(baseAddress, Page, 0, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.PlaceholderMapFailed, failure);
        Assert.True(owner.ContainsFreeRange(baseAddress, hole));
        Assert.False(owner.IsBacked(baseAddress, Page));

        host.FailNext(Op.MapView, failure: HostViewFailure.ProtectFailed);
        Assert.False(owner.MapShared(baseAddress, Page, 0, HostPageProtection.ReadWrite, out failure));
        Assert.Equal(HostViewFailure.ProtectFailed, failure);
        Assert.True(owner.ContainsFreeRange(baseAddress, hole));

        host.FailNext(Op.CommitPrivate);
        Assert.False(owner.AllocatePrivate(baseAddress, Page, HostPageProtection.ReadWrite));
        Assert.True(owner.ContainsFreeRange(baseAddress, hole));

        Assert.True(owner.MapShared(baseAddress, Page, 0, HostPageProtection.ReadWrite, out _));
        host.FailNext(Op.UnmapView);
        Assert.False(owner.UnmapShared(baseAddress, Page));
        Assert.True(owner.IsBacked(baseAddress, Page));
        Assert.False(owner.ContainsFreeRange(baseAddress, Page));
        Assert.True(owner.UnmapShared(baseAddress, Page));
    }

    [Fact]
    public void AlignmentAndOverflow_AreRejectedWithoutHostCalls()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);
        host.Log.Clear();

        foreach (var (address, size) in new[]
                 {
                     (baseAddress + 0x1000, Page),
                     (baseAddress, Page + 0x1000),
                     (baseAddress, 0UL),
                     (ulong.MaxValue - Page + 1, 2 * Page),
                 })
        {
            Assert.False(owner.MapShared(address, size, 0, HostPageProtection.ReadWrite, out _));
            Assert.False(owner.UnmapShared(address, size));
            Assert.False(owner.AllocatePrivate(address, size, HostPageProtection.ReadWrite));
            Assert.False(owner.FreePrivate(address, size));
            Assert.False(owner.ContainsFreeRange(address, size));
        }

        // Transient access works on host pages; it still rejects misaligned, empty and wrapping ranges.
        Assert.False(owner.SetTransientAccess(baseAddress + 0x800, 0x1000, HostPageProtection.ReadWrite));
        Assert.False(owner.SetTransientAccess(baseAddress, 0, HostPageProtection.ReadWrite));
        Assert.False(owner.SetTransientAccess(ulong.MaxValue - 0x1000 + 1, 0x2000, HostPageProtection.ReadWrite));
        Assert.True(owner.SetTransientAccess(baseAddress + host.PageSize, host.PageSize, HostPageProtection.ReadWrite));

        Assert.False(owner.TryReserveAddressRange(baseAddress + Page, hole));
        Assert.False(owner.TryReserveAddressRange(baseAddress, hole + Page));
        Assert.False(owner.MapShared(baseAddress, Page, 0x1000, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, failure);
        Assert.False(owner.MapShared(baseAddress, Page, BackingSize, HostPageProtection.ReadWrite, out failure));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, failure);
        Assert.Empty(host.Log);
    }

    [Fact]
    public void Dispose_ReleasesViewsPrivatesAndHoles()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);
        Assert.True(owner.MapShared(baseAddress, Page, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(owner.AllocatePrivate(baseAddress + Page, Page, HostPageProtection.ReadWrite));

        owner.Dispose();
        owner.Dispose();
        Assert.False(owner.TryReserveAddressRange(baseAddress, hole));
        Assert.False(owner.MapShared(baseAddress, Page, 0, HostPageProtection.ReadWrite, out _));

        using var fresh = new GuestSpaceOwner(host, BackingSize);
        Assert.True(fresh.TryReserveAddressRange(baseAddress, hole));
        Assert.True(fresh.ContainsFreeRange(baseAddress, hole));
    }

    [Fact]
    public void FailedCoalescing_IsStillReleasedCompletelyAtDisposal()
    {
        if (!Supported)
        {
            return;
        }

        var raw = HostViewMemory.Create();
        var host = new FailingHostViews(raw);
        var owner = new GuestSpaceOwner(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = AcquireRange(owner, host, hole);
        Assert.True(owner.MapShared(baseAddress + Page, Page, 0, HostPageProtection.ReadWrite, out _));

        host.FailNext(Op.JoinHoles);
        Assert.True(owner.UnmapShared(baseAddress + Page, Page));
        Assert.True(owner.ContainsFreeRange(baseAddress + Page, Page));
        Assert.False(owner.ContainsFreeRange(baseAddress, hole));

        owner.Dispose();

        Assert.Equal(baseAddress, raw.ReserveHole(baseAddress, hole));
        Assert.True(raw.FreeHole(baseAddress, hole));
    }
}
