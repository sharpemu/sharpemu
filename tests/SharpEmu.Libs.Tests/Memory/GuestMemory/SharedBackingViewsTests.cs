// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;
using Op = SharpEmu.Libs.Tests.Memory.GuestMemory.FailingHostViews.Op;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[CollectionDefinition(GuestMemoryStateCollection.Name, DisableParallelization = true)]
public sealed class GuestMemoryStateCollection
{
    public const string Name = "GuestMemoryState";
}

[Collection(GuestMemoryStateCollection.Name)]
public sealed unsafe class SharedBackingViewsTests
{
    private static byte[] Pattern(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private static ulong[] Read(SharedBackingViews store, ulong address, int count)
    {
        var bytes = new byte[count * 8];
        Assert.True(store.TryReadBacking(address, bytes));
        var values = new ulong[count];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    [Fact]
    public void CopyInAndOut_ShareBytesWithTheView()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        Assert.True(store.IsAvailable);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(baseAddress, Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out _));

        Assert.True(store.TryWriteBacking(baseAddress + 16, BitConverter.GetBytes(Marker)));
        Assert.Equal(Marker, *(ulong*)(baseAddress + 16));
        *(ulong*)(baseAddress + 32) = ~Marker;
        Assert.Equal(new[] { ~Marker }, Read(store, baseAddress + 32, 1));
        Assert.True(store.Clear(0, Segment));
        Assert.Equal(0UL, *(ulong*)(baseAddress + 16));

        Assert.True(store.Unmap(baseAddress, Segment, out var preserved));
        Assert.True(preserved);
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void TwoViewsOfOneOffset_ShareBytesAndTheSecondSurvives()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var first = ReserveFreeHole(host, hole);
        var second = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(first, Segment));
        Assert.True(host.SplitHole(second, Segment));
        Assert.True(store.TryMapReservedRange(first, Segment, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(store.TryMapReservedRange(second, Segment, 0, HostPageProtection.ReadWrite, out _));

        *(ulong*)first = Marker;
        Assert.Equal(Marker, *(ulong*)second);
        Assert.True(store.Unmap(first, Segment, out _));
        Assert.False(store.Contains(first, Segment));
        Assert.True(store.Contains(second, Segment));
        Assert.Equal(Marker, *(ulong*)second);

        Assert.True(store.Unmap(second, Segment, out _));
        Assert.True(host.JoinHoles(first, hole));
        Assert.True(host.JoinHoles(second, hole));
        Assert.True(host.FreeHole(first, hole));
        Assert.True(host.FreeHole(second, hole));
    }

    [Fact]
    public void Transfers_WorkWhileTheViewIsNoAccessOrReadOnly()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(baseAddress, Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out _));

        Assert.True(host.ChangeAccess(baseAddress, Segment, HostPageProtection.NoAccess));
        Assert.True(store.TryWriteBacking(baseAddress, BitConverter.GetBytes(Marker)));
        Assert.Equal(new[] { Marker }, Read(store, baseAddress, 1));
        Assert.True(host.ChangeAccess(baseAddress, Segment, HostPageProtection.ReadOnly));
        Assert.True(store.TryWriteBacking(baseAddress, BitConverter.GetBytes(~Marker)));
        Assert.Equal(~Marker, *(ulong*)baseAddress);

        Assert.True(store.Unmap(baseAddress, Segment, out _));
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void UnmapThenRemap_KeepsBackingContents()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(baseAddress, Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, 2 * Segment, HostPageProtection.ReadWrite, out _));
        *(ulong*)baseAddress = Marker;

        Assert.True(store.Unmap(baseAddress, Segment, out _));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, 2 * Segment, HostPageProtection.ReadWrite, out _));
        Assert.Equal(Marker, *(ulong*)baseAddress);

        Assert.True(store.Unmap(baseAddress, Segment, out _));
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void PartialUnmap_SplitsTheRecordAndTransfersNeverCopyAcrossTheGap()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        var middle = baseAddress + Segment;
        var right = baseAddress + 2 * Segment;
        Assert.True(host.SplitHole(baseAddress, 3 * Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, 3 * Segment, 0, HostPageProtection.ReadWrite, out _));
        *(ulong*)baseAddress = Marker;
        *(ulong*)right = ~Marker;

        Assert.True(store.Unmap(middle, Segment, out var preserved));
        Assert.True(preserved);
        Assert.True(store.Contains(baseAddress, Segment));
        Assert.False(store.Contains(middle, Segment));
        Assert.True(store.Contains(right, Segment));
        Assert.Equal(Marker, *(ulong*)baseAddress);
        Assert.Equal(~Marker, *(ulong*)right);

        var pattern = Pattern((int)(2 * Segment), 0xA5);
        Assert.False(store.TryReadBacking(baseAddress, pattern));
        Assert.All(pattern, value => Assert.Equal(0xA5, value));
        Assert.False(store.TryWriteBacking(baseAddress, Pattern((int)(2 * Segment), 0x5A)));
        Assert.Equal(Marker, *(ulong*)baseAddress);
        Assert.Equal(Marker, *(ulong*)store.AliasBase);

        Assert.True(store.TryMapReservedRange(middle, Segment, Segment, HostPageProtection.ReadWrite, out _));
        Assert.True(store.TryReadBacking(baseAddress, new byte[3 * Segment]));

        Assert.True(store.Unmap(baseAddress, 3 * Segment, out preserved));
        Assert.True(preserved);
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void Rejections_MakeNoHostCall()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        host.Log.Clear();

        Assert.False(store.Unmap(baseAddress, Segment, out _));
        Assert.False(store.TryReadBacking(baseAddress, new byte[16]));
        Assert.False(store.TryMapReservedRange(baseAddress, Segment, BackingSize - Segment / 2, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.OffsetOutOfBounds, failure);
        Assert.Empty(host.Log);

        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void InjectedFailures_RollBackViewsAndRecordsTogether()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        var middle = baseAddress + Segment;
        Assert.True(host.SplitHole(baseAddress, 3 * Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, 3 * Segment, 0, HostPageProtection.ReadWrite, out _));
        *(ulong*)baseAddress = Marker;

        host.Log.Clear();
        host.FailNext(Op.MapView, afterCalls: 1);
        Assert.False(store.Unmap(middle, Segment, out _));
        Assert.True(store.Contains(baseAddress, 3 * Segment));
        Assert.Equal(Marker, *(ulong*)baseAddress);
        Assert.Equal(
            new[] { Op.UnmapView, Op.SplitHole, Op.SplitHole, Op.MapView, Op.MapView, Op.UnmapView, Op.JoinHoles, Op.MapView },
            host.Log);

        Assert.True(store.Unmap(baseAddress, 3 * Segment, out _));
        Assert.True(host.SplitHole(baseAddress, Segment));
        Assert.True(host.SplitHole(middle, Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(store.TryMapReservedRange(middle, Segment, Segment, HostPageProtection.ReadWrite, out _));

        host.Log.Clear();
        host.FailNext(Op.UnmapView, afterCalls: 1);
        Assert.False(store.Unmap(baseAddress, 2 * Segment, out _));
        Assert.True(store.Contains(baseAddress, 2 * Segment));
        Assert.Equal(new[] { Op.UnmapView, Op.UnmapView, Op.MapView }, host.Log);

        host.FailNext(Op.MapView, failure: HostViewFailure.ProtectFailed);
        Assert.False(store.TryMapReservedRange(baseAddress + 2 * Segment, Segment, 0, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.ProtectFailed, failure);
        Assert.False(store.Contains(baseAddress + 2 * Segment, Segment));

        Assert.True(store.Unmap(baseAddress, 2 * Segment, out _));
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void Dispose_UnmapsRecordedViewsBeforeReleasingTheBacking()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(baseAddress, Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out _));

        store.Dispose();
        store.Dispose();
        Assert.False(store.IsAvailable);
        Assert.False(store.Clear(0, Segment));
        Assert.False(store.TryWriteBacking(baseAddress, new byte[8]));
        Assert.False(store.TryReadBacking(baseAddress, new byte[8]));
        Assert.False(store.Unmap(baseAddress, Segment, out _));
        Assert.False(store.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.BackingUnavailable, failure);

        using var fresh = new SharedBackingViews(host, BackingSize);
        Assert.True(fresh.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(fresh.Unmap(baseAddress, Segment, out _));
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void DisposeDuringMap_LeavesNoUnrecordedView()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(baseAddress, Segment));

        host.AfterMapView = () => store.Dispose();
        Assert.False(store.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out var failure));
        Assert.Equal(HostViewFailure.BackingUnavailable, failure);
        Assert.Equal(Op.UnmapView, host.Log[^1]);
        host.AfterMapView = null;

        using var fresh = new SharedBackingViews(host, BackingSize);
        Assert.True(fresh.TryMapReservedRange(baseAddress, Segment, 0, HostPageProtection.ReadWrite, out _));
        Assert.True(fresh.Unmap(baseAddress, Segment, out _));
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }
}
