// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class AvlSpanSetTests
{
    [Theory]
    [InlineData(101, 4096)]
    [InlineData(202, 1048576)]
    public void MatchesSortedRangesAfterMergesSplitsAndQueries(int seed, int space)
    {
        var expected = new SpanSet();
        var actual = new AvlSpanSet();
        var random = new System.Random(seed);
        for (var operation = 0; operation < 10000; operation++)
        {
            var address = (ulong)random.Next(space);
            var size = (ulong)random.Next(1, space / 32);
            if (random.Next(2) == 0)
            {
                expected.Add(address, size);
                actual.Add(address, size);
            }
            else
            {
                expected.Remove(address, size);
                actual.Remove(address, size);
            }
            address = (ulong)random.Next(space);
            size = (ulong)random.Next(1, space / 16);
            Assert.Equal(expected.Contains(address, size), actual.Contains(address, size));
            Assert.Equal(expected.Overlaps(address, size), actual.Overlaps(address, size));
            var snapshot = actual.Snapshot();
            Assert.Equal(expected.Contains(address, size), snapshot.Contains(address, size));
            Assert.Equal(expected.Overlaps(address, size), snapshot.Overlaps(address, size));
            Assert.Equal(expected.GetOverlappingRanges(address, size), actual.GetOverlappingRanges(address, size));
            Assert.Equal(expected.GetOverlappingRanges(0, ulong.MaxValue), actual.GetOverlappingRanges(0, ulong.MaxValue));
            Assert.Equal(expected.IsEmpty, actual.IsEmpty);
        }
        actual.Clear();
        Assert.True(actual.IsEmpty);
    }

    [Fact]
    public void PublishedSnapshotsRemainUnchangedAfterMergesSplitsAndClear()
    {
        var set = new AvlSpanSet();
        Assert.Same(SpanSnapshot.Empty, set.Snapshot());
        set.Add(100, 100);
        var first = set.Snapshot();
        set.Add(300, 100);
        var second = set.Snapshot();
        Assert.NotSame(first, second);
        Assert.False(first.Overlaps(300, 1));
        Assert.True(second.Contains(300, 100));
        set.Add(200, 100);
        var merged = set.Snapshot();
        Assert.True(merged.Contains(100, 300));
        Assert.False(second.Contains(200, 1));
        set.Remove(150, 200);
        var split = set.Snapshot();
        Assert.True(split.Contains(100, 50));
        Assert.True(split.Contains(350, 50));
        Assert.False(split.Overlaps(150, 200));
        Assert.True(merged.Contains(100, 300));
        set.Clear();
        Assert.Same(SpanSnapshot.Empty, set.Snapshot());
        Assert.True(split.Contains(350, 50));
        set.Add(500, 100);
        Assert.True(set.Snapshot().Contains(500, 100));
        Assert.False(first.Overlaps(500, 1));
    }

    [Fact]
    public void UnchangedRangesReusePublishedSnapshotWithoutAllocating()
    {
        var set = new AvlSpanSet();
        for (ulong index = 0; index < 2048; index++) set.Add(index * 4, 2);
        var snapshot = set.Snapshot();
        for (var index = 0; index < 100; index++)
        {
            set.Add(0, 1);
            set.Remove(2, 1);
            set.Snapshot();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10000; index++)
        {
            set.Add(0, 1);
            set.Remove(2, 1);
            set.Snapshot();
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Same(snapshot, set.Snapshot());
        for (ulong index = 0; index < 2048; index++)
        {
            Assert.True(snapshot.Contains(index * 4, 2));
            Assert.False(snapshot.Overlaps(index * 4 + 2, 2));
        }
    }

    [Fact]
    public void ChangedSnapshotAllocatesOnlyItsExactArraysAndSnapshot()
    {
        var set = new AvlSpanSet();
        for (ulong index = 0; index < 2048; index++) set.Add(index * 4, 2);
        set.Snapshot();
        set.Remove(0, 2);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var snapshot = set.Snapshot();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(bytes, 2047 * 16L, 2047 * 16L + 256);
        Assert.False(snapshot.Overlaps(0, 4));
        Assert.True(snapshot.Contains(8188, 2));
    }

    [Fact]
    public void OrderedInsertionsAndRemovalsKeepExactCoverage()
    {
        var set = new AvlSpanSet();
        for (ulong index = 0; index < 8192; index++) set.Add(index * 4, 2);
        for (ulong index = 0; index < 8192; index += 2) set.Remove(index * 4, 2);
        var ranges = set.GetOverlappingRanges(0, 32768);
        Assert.Equal(4096, ranges.Count);
        for (var index = 0; index < ranges.Count; index++)
            Assert.Equal(new GuestSpan((ulong)index * 8 + 4, 2), ranges[index]);
        set.Add(0, 32768);
        Assert.Single(set.GetOverlappingRanges(0, 32768));
        set.Remove(1, 32766);
        Assert.Equal(new[] { new GuestSpan(0, 1), new GuestSpan(32767, 1) }, set.GetOverlappingRanges(0, 32768));
        set.Add(ulong.MaxValue - 100, 100);
        Assert.True(set.Contains(ulong.MaxValue - 1, 1));
        Assert.Equal(new[] { new GuestSpan(ulong.MaxValue - 100, 100) }, set.GetOverlappingRanges(ulong.MaxValue - 100, 100));
        set.Remove(ulong.MaxValue - 50, 50);
        Assert.False(set.Overlaps(ulong.MaxValue - 50, 50));
        Assert.Equal(new[] { new GuestSpan(ulong.MaxValue - 100, 50) }, set.GetOverlappingRanges(ulong.MaxValue - 100, 100));
    }
}
