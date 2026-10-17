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
            Assert.Equal(expected.GetOverlappingRanges(address, size), actual.GetOverlappingRanges(address, size));
            Assert.Equal(expected.GetOverlappingRanges(0, ulong.MaxValue), actual.GetOverlappingRanges(0, ulong.MaxValue));
            Assert.Equal(expected.IsEmpty, actual.IsEmpty);
        }
        actual.Clear();
        Assert.True(actual.IsEmpty);
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
