// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

[Collection(AllocationMeasurementCollection.Name)]
public sealed class ViewRecordTreeTests
{
    [Fact]
    public void PublishedRootKeepsRecordsAcrossUpdatesAndClear()
    {
        var tree = new ViewRecordTree();
        for (ulong address = 1; address <= 256; address++)
            tree.Add(new ViewRecord(address, 1, address, SharpEmu.HLE.Host.HostPageProtection.ReadWrite));
        var snapshot = tree.Snapshot;
        for (ulong address = 1; address <= 256; address++)
        {
            if (address % 2 == 0)
                tree.Remove(new ViewRecord(address, 1, 0, default));
            else
                tree[address] = new ViewRecord(address, 1, 999, default);
        }
        tree.Clear();
        for (ulong address = 1; address <= 256; address++)
        {
            var record = ViewRecordTree.FindAtOrBelow(snapshot, address);
            Assert.Equal(address, record.Address);
            Assert.Equal(address, record.Offset);
        }
        Assert.Null(tree.Snapshot);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(719)]
    public void UpdatesAndBoundarySearchesMatchOrderedRecords(int seed)
    {
        var tree = new ViewRecordTree();
        var expected = new SortedDictionary<ulong, ViewRecord>();
        var random = new System.Random(seed);
        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var address = (ulong)random.Next(1, 300) * 0x4000;
            var range = new ViewRecord(address, 0x4000, (ulong)iteration, SharpEmu.HLE.Host.HostPageProtection.ReadWrite);
            if (random.Next(2) == 0)
            {
                expected[address] = range;
                tree[address] = range;
            }
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
        var tree = new ViewRecordTree();
        for (ulong address = 1; address <= 10000; address++)
            Assert.True(tree.Add(new ViewRecord(address, 1, 0, SharpEmu.HLE.Host.HostPageProtection.ReadWrite)));
        for (ulong address = 2; address <= 10000; address += 2)
            tree.Remove(new ViewRecord(address, 1, 0, SharpEmu.HLE.Host.HostPageProtection.ReadWrite));
        for (ulong address = 2; address <= 10000; address += 2)
            Assert.Equal(address - 1, tree.FindAtOrBelow(address).Address);
        var allocated = AllocationMeasurementCollection.Measure(() =>
        {
            for (ulong address = 1; address < 10000; address++)
            {
                _ = tree.FindAtOrBelow(address);
                _ = tree.FindAtOrAbove(address);
            }
        });
        Assert.Equal(0, allocated);
        for (ulong address = 9999; address > 0; address -= 2)
        {
            tree.Remove(new ViewRecord(address, 1, 0, SharpEmu.HLE.Host.HostPageProtection.ReadWrite));
            if (address == 1) break;
        }
        Assert.Empty(tree);
    }
}
