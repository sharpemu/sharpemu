// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class SpanSnapshotLastHitTests
{
    [Fact]
    public void Contains_RequiresFullRangeWithinTheCachedSpan()
    {
        var snapshot = new SpanSnapshot([0x1000, 0x4000], [0x2000, 0x5000]);
        Assert.True(snapshot.ContainsWithLastHit(0x1100, 4));
        Assert.True(snapshot.ContainsWithLastHit(0x1800, 0x800));
        Assert.False(snapshot.ContainsWithLastHit(0x1800, 0x801));
        Assert.False(snapshot.ContainsWithLastHit(0x0FFF, 1));
        Assert.False(snapshot.ContainsWithLastHit(0x2000, 1));
        Assert.False(snapshot.ContainsWithLastHit(0x3000, 4));
        Assert.True(snapshot.ContainsWithLastHit(0x4000, 0x1000));
        Assert.True(snapshot.ContainsWithLastHit(0x1000, 0x1000));
    }

    [Fact]
    public void Contains_UsesSnapshotIdentityAfterUnmappingAndRemapping()
    {
        var set = new SpanSet();
        set.Add(0x1000, 0x3000);
        var original = set.Snapshot();
        Assert.True(original.ContainsWithLastHit(0x2000, 4));

        set.Remove(0x2000, 0x1000);
        var unmapped = set.Snapshot();
        Assert.False(unmapped.ContainsWithLastHit(0x2000, 4));
        Assert.True(unmapped.ContainsWithLastHit(0x3000, 4));
        Assert.True(original.ContainsWithLastHit(0x2000, 4));
        Assert.False(unmapped.ContainsWithLastHit(0x2000, 4));

        set.Add(0x2000, 0x1000);
        Assert.True(set.Snapshot().ContainsWithLastHit(0x2000, 4));
        Assert.False(SpanSnapshot.Empty.ContainsWithLastHit(0x2000, 4));
    }

    [Fact]
    public void Contains_MatchesBinarySearchForBoundaryAndOverflowRequests()
    {
        ulong[] starts = [0x1000, 0x4000, ulong.MaxValue - 0x1000];
        ulong[] ends = [0x2000, 0x5000, ulong.MaxValue];
        var snapshot = new SpanSnapshot(starts, ends);
        ulong[] addresses = [0, 0xFFF, 0x1000, 0x1FFF, 0x2000, 0x3000, 0x4000, 0x4FFF,
            ulong.MaxValue - 0x1000, ulong.MaxValue - 1, ulong.MaxValue];
        ulong[] sizes = [0, 1, 4, 0x1000, ulong.MaxValue];
        for (var repetition = 0; repetition < 2; repetition++)
        foreach (var address in addresses)
        foreach (var size in sizes)
        {
            var index = Array.BinarySearch(starts, address);
            if (index < 0) index = ~index - 1;
            var expected = index >= 0 && ends[index] >= unchecked(address + size);
            Assert.Equal(expected, snapshot.ContainsWithLastHit(address, size));
        }
    }

    [Fact]
    public void Contains_ConcurrentCallersCanCacheDifferentSnapshotsAndSpans()
    {
        var left = new SpanSnapshot([0x1000, 0x4000], [0x2000, 0x5000]);
        var right = new SpanSnapshot([0x1000, 0x8000], [0x1800, 0x9000]);
        Parallel.For(0, 64, worker =>
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                Assert.True(left.ContainsWithLastHit(0x1900, 4));
                Assert.False(right.ContainsWithLastHit(0x1900, 4));
                Assert.True(right.ContainsWithLastHit(0x8000, 4));
                Assert.False(left.ContainsWithLastHit(0x8000, 4));
                Assert.True(left.ContainsWithLastHit(worker % 2 == 0 ? 0x1000UL : 0x4000UL, 4));
            }
        });
    }

    // Random spans and queries: the cached answer equals a linear scan of the snapshot's spans, while older snapshots' hits
    // are still remembered on this thread.
    [Fact]
    public void Contains_MatchesALinearScanAcrossSuccessiveSnapshots()
    {
        var random = new System.Random(1234);
        var set = new SpanSet();
        var older = new List<SpanSnapshot>();
        for (var round = 0; round < 40; round++)
        {
            for (var change = 0; change < 6; change++)
            {
                var address = (ulong)random.Next(0, 256) * 0x100;
                var size = (ulong)random.Next(1, 32) * 0x100;
                if (random.Next(3) == 0) set.Remove(address, size);
                else set.Add(address, size);
            }

            var snapshot = set.Snapshot();
            var spans = new List<(ulong Start, ulong End)>();
            set.ForEach((start, size) => spans.Add((start, start + size)));
            older.Add(snapshot);
            for (var query = 0; query < 400; query++)
            {
                var address = (ulong)random.Next(0, 0x11000);
                var size = (ulong)random.Next(0, 0x2000);
                var index = spans.FindLastIndex(span => span.Start <= address);
                var expected = index >= 0 && spans[index].End >= address + size;
                Assert.Equal(expected, snapshot.ContainsWithLastHit(address, size));
                if (query % 50 == 0)
                {
                    // Warm another snapshot's hit on this thread between queries.
                    _ = older[random.Next(older.Count)].ContainsWithLastHit(address, 4);
                }
            }
        }
    }
}
