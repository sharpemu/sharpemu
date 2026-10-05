// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

public sealed class AllocationGapTreeTests
{
    [Fact]
    public void UpdatesPreserveLowestAlignedGap()
    {
        var tree = new AllocationGapTree();
        var ranges = new Dictionary<ulong, ulong>();
        var random = new System.Random(501);
        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var address = (ulong)random.Next(1, 64) * 8;
            if (random.Next(3) == 0)
            {
                tree.Remove(address);
                ranges.Remove(address);
            }
            else
            {
                var size = (ulong)random.Next(1, 9);
                tree.Set(address, size);
                ranges[address] = size;
            }
            var start = (ulong)random.Next(1, 512);
            var length = (ulong)random.Next(1, 33);
            var alignment = (ulong)random.Next(1, 17);
            ulong expected = 0;
            for (var candidate = start; candidate + length <= 512; candidate++)
            {
                if (candidate % alignment == 0 && ranges.All(range =>
                    candidate + length <= range.Key || candidate >= range.Key + range.Value))
                {
                    expected = candidate;
                    break;
                }
            }
            Assert.Equal(expected, tree.Find(start, length, alignment, 512));
        }
        tree.Clear();
        Assert.Equal(8UL, tree.Find(1, 8, 8, 16));
    }

    [Fact]
    public void RejectsOverflowAndReusesRemovedLowerRange()
    {
        var tree = new AllocationGapTree();
        tree.Set(8, 8);
        tree.Set(32, 8);
        Assert.Equal(16UL, tree.Find(8, 8, 8, 64));
        tree.Remove(8);
        Assert.Equal(8UL, tree.Find(8, 8, 8, 64));
        tree.Set(8, 16);
        Assert.Equal(24UL, tree.Find(8, 8, 8, 64));
        Assert.Equal(0UL, tree.Find(ulong.MaxValue - 1, 8, 8, ulong.MaxValue));
        Assert.Equal(0UL, tree.Find(1, 8, 0, 64));
        Assert.Equal(0UL, tree.Find(1, 0, 1, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Set(ulong.MaxValue, 1));
    }
}
