// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed class ImagePageOwnerTableTests
{
    private static ResourceSlotIdentifier Owner(uint index) => new(index, 1);

    [Fact]
    public void MultipleOwners_ShareOnePageAndEraseExactly()
    {
        var table = new ImagePageOwnerTable();
        var owners = table.GetOrCreate(17);
        owners.Add(Owner(11));
        owners.Add(Owner(22));

        Assert.NotNull(table.Find(17));
        Assert.Equal(2, table.Find(17)!.Count);
        Assert.True(owners.Remove(Owner(11)));
        Assert.Equal(1, owners.Count);
        Assert.Equal(Owner(22), owners[0]);
        Assert.False(owners.Remove(Owner(33)));
        Assert.Equal(1, owners.Count);
    }

    [Fact]
    public void RangeAcrossBucketBoundary_UsesTwoBuckets()
    {
        var boundary = (ulong)ImagePageOwnerTable.BucketEntries << ImagePageOwnerTable.PageBits;
        Assert.True(ImagePageOwnerTable.TryGetPageRange(boundary - 1, 2, out var first, out var lastExclusive));
        Assert.Equal((ulong)ImagePageOwnerTable.BucketEntries - 1, first);
        Assert.Equal((ulong)ImagePageOwnerTable.BucketEntries + 1, lastExclusive);

        var table = new ImagePageOwnerTable();
        table.GetOrCreate(first).Add(Owner(1));
        table.GetOrCreate(lastExclusive - 1).Add(Owner(2));
        Assert.Equal(2, table.AllocatedBucketCount);
    }

    [Fact]
    public void Queries_DoNotAllocate()
    {
        var table = new ImagePageOwnerTable();
        Assert.Null(table.Find(123));
        Assert.Equal(0, table.AllocatedBucketCount);
        Assert.Null(table.Find(ImagePageOwnerTable.PageCount - 1));
        Assert.Equal(0, table.AllocatedBucketCount);
        Assert.Null(table.Find(ImagePageOwnerTable.PageCount));
        Assert.Equal(0, table.AllocatedBucketCount);
    }

    [Fact]
    public void AddressSpaceBoundaries_AreEnforced()
    {
        Assert.True(ImagePageOwnerTable.TryGetPageRange(ImagePageOwnerTable.AddressSpaceSize - 1, 1, out var first, out var lastExclusive));
        Assert.Equal(ImagePageOwnerTable.PageCount - 1, first);
        Assert.Equal(ImagePageOwnerTable.PageCount, lastExclusive);
        Assert.False(ImagePageOwnerTable.TryGetPageRange(0, 0, out _, out _));
        Assert.False(ImagePageOwnerTable.TryGetPageRange(ImagePageOwnerTable.AddressSpaceSize, 1, out _, out _));
        Assert.False(ImagePageOwnerTable.TryGetPageRange(ImagePageOwnerTable.AddressSpaceSize - 1, 2, out _, out _));
        Assert.False(ImagePageOwnerTable.TryGetPageRange(ulong.MaxValue - 1, 4, out _, out _));

        var table = new ImagePageOwnerTable();
        table.GetOrCreate(ImagePageOwnerTable.PageCount - 1).Add(Owner(99));
        Assert.Equal(Owner(99), table.Find(ImagePageOwnerTable.PageCount - 1)![0]);
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => table.GetOrCreate(ImagePageOwnerTable.PageCount));
        Assert.Contains(fatal.Messages, message => message.Contains("outside the guest address space"));
    }

    [Fact]
    public void OwnerList_GrowsPastInlineCapacityAndShrinksBack()
    {
        var owners = new PageOwnerList();
        for (uint owner = 1; owner <= 18; owner++)
        {
            owners.Add(Owner(owner));
        }

        Assert.Equal(18, owners.Count);
        Assert.True(owners.UsesOverflow);
        Assert.Equal(Owner(1), owners[0]);
        for (var index = 0; index < owners.Count; index++)
        {
            Assert.Equal(Owner((uint)index + 1), owners[index]);
        }

        Assert.True(owners.Remove(Owner(5)));
        Assert.Equal(17, owners.Count);
        Assert.False(owners.Contains(Owner(5)));
        Assert.True(owners.Remove(Owner(18)));
        Assert.Equal(16, owners.Count);
        Assert.False(owners.UsesOverflow);

        uint[] remaining = [1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17];
        var visited = new List<ResourceSlotIdentifier>();
        owners.ForEach(visited.Add);
        Assert.Equal(remaining.Select(Owner), visited);
        Assert.False(owners.Remove(Owner(99)));
        Assert.Equal(16, owners.Count);
    }

    [Fact]
    public void OneMiBGranularity_RegistersWholeRange()
    {
        Assert.Equal(20, ImagePageOwnerTable.PageBits);
        const ulong rangeSize = 64UL * 1024 * 1024;
        Assert.True(ImagePageOwnerTable.TryGetPageRange(0, rangeSize, out var first, out var lastExclusive));
        Assert.Equal(0UL, first);
        Assert.Equal(64UL, lastExclusive);

        var table = new ImagePageOwnerTable();
        for (var page = first; page < lastExclusive; page++)
        {
            table.GetOrCreate(page).Add(Owner(7));
        }

        Assert.Equal(1, table.AllocatedBucketCount);
        for (var page = first; page < lastExclusive; page++)
        {
            var owners = table.Find(page);
            Assert.NotNull(owners);
            Assert.Equal(1, owners!.Count);
            Assert.Equal(Owner(7), owners[0]);
            Assert.True(owners.Remove(Owner(7)));
        }
    }

    [Fact]
    public void SharedCoarsePage_LifecycleIsExact()
    {
        var table = new ImagePageOwnerTable();
        var owners = table.GetOrCreate(2);
        owners.Add(Owner(11));
        owners.Add(Owner(22));
        Assert.Equal(2, owners.Count);
        Assert.True(owners.Remove(Owner(11)));
        Assert.Equal(Owner(22), owners[0]);
        Assert.False(owners.Remove(Owner(11)));
        Assert.True(owners.Remove(Owner(22)));
        Assert.True(owners.IsEmpty);
    }

    [Fact]
    public void OneMiBBoundaries_MapToBothCoarsePages()
    {
        Assert.True(ImagePageOwnerTable.TryGetPageRange(0x0fffff, 2, out var first, out var lastExclusive));
        Assert.Equal(0UL, first);
        Assert.Equal(2UL, lastExclusive);
        Assert.True(ImagePageOwnerTable.TryGetPageRange(ImagePageOwnerTable.AddressSpaceSize - 1, 1, out first, out lastExclusive));
        Assert.Equal(ImagePageOwnerTable.PageCount - 1, first);
        Assert.Equal(ImagePageOwnerTable.PageCount, lastExclusive);
    }

    [Fact]
    public void OwnerList_DistinguishesGenerations()
    {
        var owners = new PageOwnerList();
        owners.Add(new ResourceSlotIdentifier(3, 1));
        Assert.False(owners.Contains(new ResourceSlotIdentifier(3, 2)));
        Assert.False(owners.Remove(new ResourceSlotIdentifier(3, 2)));
        Assert.True(owners.Remove(new ResourceSlotIdentifier(3, 1)));
    }
}
