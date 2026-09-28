// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

public sealed class OwnedRangeTreeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(719)]
    public void UpdatesAndBoundarySearchesMatchOrderedRecords(int seed)
    {
        var tree = new OwnedRangeTree();
        var expected = new SortedDictionary<ulong, OwnedRange>();
        var random = new System.Random(seed);
        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var address = (ulong)random.Next(1, 300) * 0x4000;
            var range = new OwnedRange(address, 0x4000, (RangeKind)(iteration % 2));
            if (random.Next(2) == 0)
                Assert.Equal(expected.TryAdd(address, range), tree.Add(range));
            else
            {
                expected.Remove(address);
                tree.Remove(range);
            }
            var query = (ulong)random.Next(0, 302) * 0x4000 + (ulong)random.Next(2);
            Assert.Equal(expected.Values.LastOrDefault(value => value.Address <= query), tree.FindAtOrBelow(query));
            Assert.Equal(expected.Values.FirstOrDefault(value => value.Address >= query), tree.FindAtOrAbove(query));
            Assert.Equal(expected.Values.ToArray(), tree.ToArray());
        }
        tree.Clear();
        Assert.Empty(tree);
        Assert.Equal(default, tree.FindAtOrBelow(ulong.MaxValue));
        Assert.Equal(default, tree.FindAtOrAbove(0));
    }

    [Fact]
    public void OrderedInsertionsAndRemovalsPreserveLookupWithoutAllocations()
    {
        var tree = new OwnedRangeTree();
        for (ulong address = 1; address <= 10000; address++)
            Assert.True(tree.Add(new OwnedRange(address, 1, RangeKind.Backed)));
        for (ulong address = 2; address <= 10000; address += 2)
            tree.Remove(new OwnedRange(address, 1, RangeKind.Backed));
        for (ulong address = 2; address <= 10000; address += 2)
            Assert.Equal(address - 1, tree.FindAtOrBelow(address).Address);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (ulong address = 1; address < 10000; address++)
        {
            _ = tree.FindAtOrBelow(address);
            _ = tree.FindAtOrAbove(address);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        for (ulong address = 9999; address > 0; address -= 2)
        {
            tree.Remove(new OwnedRange(address, 1, RangeKind.Backed));
            if (address == 1) break;
        }
        Assert.Empty(tree);
    }
}
