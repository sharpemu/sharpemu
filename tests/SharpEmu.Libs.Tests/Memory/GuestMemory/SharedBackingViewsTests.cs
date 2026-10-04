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
public sealed class SharedBackingViewsTests
{
    private static byte[] Pattern(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    [Fact]
    public unsafe void SingleMappingReadsDoNotAllocateTemporarySegments()
    {
        if (!Supported) return;
        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(baseAddress, Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, Segment, HostPageProtection.ReadWrite, out _));
        try
        {
            *(ulong*)(baseAddress + Segment - 8) = Marker;
            var bytes = new byte[8];
            var succeeded = true;
            void ReadMarker(int count)
            {
                for (var index = 0; index < count; index++)
                    succeeded &= store.TryReadBacking(baseAddress + Segment - 8, bytes);
            }

            var allocatedBytes = AllocationMeasurement.SteadyState(() => ReadMarker(256), () => ReadMarker(1024));

            Assert.True(succeeded);
            Assert.Equal(Marker, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            Assert.Equal(0L, allocatedBytes);
            bytes.AsSpan().Fill(0xA5);
            Assert.False(store.TryReadBacking(baseAddress + Segment - 4, bytes));
            Assert.True(bytes.SequenceEqual(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5 }));
        }
        finally
        {
            Assert.True(store.Unmap(baseAddress, Segment, out _));
            Assert.True(host.JoinHoles(baseAddress, hole));
            Assert.True(host.FreeHole(baseAddress, hole));
        }
    }

    private static ulong[] Read(SharedBackingViews store, ulong address, int count)
    {
        var bytes = new byte[count * 8];
        Assert.True(store.TryReadBacking(address, bytes));
        var values = new ulong[count];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }

    [Fact]
    public unsafe void ClearCommitsBackingBeforeTheFirstViewIsMapped()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        Assert.True(store.Clear(Segment, Segment));

        var hole = HoleSize(host);
        var baseAddress = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(baseAddress, Segment));
        Assert.True(store.TryMapReservedRange(baseAddress, Segment, Segment, HostPageProtection.ReadWrite, out _));
        Assert.Equal(0UL, *(ulong*)baseAddress);

        Assert.True(store.Unmap(baseAddress, Segment, out _));
        Assert.True(host.JoinHoles(baseAddress, hole));
        Assert.True(host.FreeHole(baseAddress, hole));
    }

    [Fact]
    public void ClearDoesNotDereferenceBackingWhenCommitFails()
    {
        if (!Supported)
        {
            return;
        }

        var host = new FailingHostViews(HostViewMemory.Create());
        using var store = new SharedBackingViews(host, BackingSize);
        host.Log.Clear();
        host.FailNext(Op.CommitBacking);

        Assert.False(store.Clear(0, Segment));
        Assert.Equal(new[] { Op.CommitBacking }, host.Log);
    }

    [Fact]
    public unsafe void CopyInAndOut_ShareBytesWithTheView()
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
    public unsafe void TwoViewsOfOneOffset_ShareBytesAndTheSecondSurvives()
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
        Assert.False(store.ContainsWithoutLock(first, Segment));
        Assert.True(store.ContainsWithoutLock(second, Segment));
        Assert.Equal(Marker, *(ulong*)second);

        Assert.True(store.Unmap(second, Segment, out _));
        Assert.True(host.JoinHoles(first, hole));
        Assert.True(host.JoinHoles(second, hole));
        Assert.True(host.FreeHole(first, hole));
        Assert.True(host.FreeHole(second, hole));
    }

    // Single-view transfers run without the lock against a snapshot of the views; they
    // must stay exact on a stable view and fail or succeed cleanly on a view that is
    // being unmapped and remapped at the same time.
    [Fact]
    public async Task LockFreeTransfers_StayConsistentWhileOtherViewsChange()
    {
        if (!Supported)
        {
            return;
        }

        var host = HostViewMemory.Create();
        using var store = new SharedBackingViews(host, BackingSize);
        var hole = HoleSize(host);
        var stable = ReserveFreeHole(host, hole);
        var churn = ReserveFreeHole(host, hole);
        Assert.True(host.SplitHole(stable, Segment));
        Assert.True(host.SplitHole(churn, Segment));
        Assert.True(store.TryMapReservedRange(stable, Segment, 0, HostPageProtection.ReadWrite, out _));

        var stop = 0;
        var failures = 0;
        var workers = Enumerable.Range(0, 2).Select(worker => Task.Run(() =>
        {
            Span<byte> bytes = stackalloc byte[8];
            var slot = stable + (ulong)(worker * 64);
            for (var value = 1UL; Volatile.Read(ref stop) == 0; value++)
            {
                if (!store.TryWriteBacking(slot, BitConverter.GetBytes(value)) ||
                    !store.TryReadBacking(slot, bytes) ||
                    BitConverter.ToUInt64(bytes) != value)
                {
                    Interlocked.Increment(ref failures);
                }

                _ = store.TryReadBacking(churn + 8, bytes);
            }
        })).ToArray();

        for (var round = 0; round < 200; round++)
        {
            Assert.True(store.TryMapReservedRange(churn, Segment, Segment, HostPageProtection.ReadWrite, out _));
            Assert.True(store.Unmap(churn, Segment, out _));
        }

        Volatile.Write(ref stop, 1);
        await Task.WhenAll(workers);
        Assert.Equal(0, Volatile.Read(ref failures));

        Assert.True(store.Unmap(stable, Segment, out _));
        Assert.True(host.JoinHoles(stable, hole));
        Assert.True(host.JoinHoles(churn, hole));
        Assert.True(host.FreeHole(stable, hole));
        Assert.True(host.FreeHole(churn, hole));
    }

    [Fact]
    public unsafe void Transfers_WorkWhileTheViewIsNoAccessOrReadOnly()
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
    public unsafe void UnmapThenRemap_KeepsBackingContents()
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
    public unsafe void PartialUnmap_SplitsTheRecordAndTransfersNeverCopyAcrossTheGap()
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
        Assert.True(store.ContainsWithoutLock(baseAddress, Segment));
        Assert.False(store.ContainsWithoutLock(middle, Segment));
        Assert.True(store.ContainsWithoutLock(right, Segment));
        Assert.False(store.ContainsWithoutLock(baseAddress, 3 * Segment));
        Assert.Equal(store.Contains(baseAddress, 3 * Segment), store.ContainsWithoutLock(baseAddress, 3 * Segment));
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
    public unsafe void InjectedFailures_RollBackViewsAndRecordsTogether()
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
        Assert.False(store.TryCopyBacking(baseAddress, baseAddress, 8));
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
