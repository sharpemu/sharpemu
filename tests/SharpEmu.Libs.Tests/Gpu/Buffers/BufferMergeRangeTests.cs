// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class BufferMergeRangeTests
{
    private const ulong PageSize = GuestBufferCache.CachingPageSize;

    [Fact]
    public void ScoreAtThresholdOnlyUnionsBounds()
    {
        var range = new BufferMergeRange(10 * PageSize, 12 * PageSize);
        Assert.False(range.IncludeBuffer(9 * PageSize, 11 * PageSize, 8));
        Assert.False(range.IncludeBuffer(11 * PageSize, 13 * PageSize, 8));
        Assert.Equal(9 * PageSize, range.Begin);
        Assert.Equal(13 * PageSize, range.End);
        Assert.False(range.HasStreamExpansion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThresholdCrossingGrowsAheadOfTheWalk(bool walksLeft)
    {
        // The request sticks out of the buffer on the side the guest walks toward.
        var range = new BufferMergeRange(200 * PageSize, 202 * PageSize);
        Assert.Equal(walksLeft, range.IncludeBuffer(
            (walksLeft ? 201UL : 199UL) * PageSize,
            (walksLeft ? 203UL : 201UL) * PageSize, 17));
        Assert.Equal((walksLeft ? 72UL : 199UL) * PageSize, range.Begin);
        Assert.Equal((walksLeft ? 203UL : 330UL) * PageSize, range.End);
        Assert.True(range.HasStreamExpansion);
    }

    [Fact]
    public void ExpansionIsHalfTheMergedSizeWithinBounds()
    {
        // A 1000-page buffer walked to the right leaps by 500 pages; a 4000-page one by the 1024-page cap.
        var range = new BufferMergeRange(1000 * PageSize, 1002 * PageSize);
        Assert.False(range.IncludeBuffer(1 * PageSize, 1001 * PageSize, 17));
        Assert.Equal(1502 * PageSize, range.End);

        var large = new BufferMergeRange(4000 * PageSize, 4002 * PageSize);
        Assert.False(large.IncludeBuffer(1 * PageSize, 4001 * PageSize, 17));
        Assert.Equal(5026 * PageSize, large.End);
    }

    [Fact]
    public void ExpansionClampsToAddressSpace()
    {
        var range = new BufferMergeRange(10 * PageSize, PageOwnerTable.AddressSpaceSize - PageSize);
        Assert.False(range.IncludeBuffer(9 * PageSize, PageOwnerTable.AddressSpaceSize - 2 * PageSize, 17));
        Assert.Equal(9 * PageSize, range.Begin);
        Assert.Equal(PageOwnerTable.AddressSpaceSize, range.End);
    }

    [Fact]
    public void ExpansionClampsToMinimumBegin()
    {
        var range = new BufferMergeRange(10 * PageSize, 12 * PageSize);
        Assert.True(range.IncludeBuffer(11 * PageSize, 13 * PageSize, 17));
        Assert.Equal(2 * PageSize, range.Begin);
        Assert.Equal(13 * PageSize, range.End);
    }

    [Fact]
    public void EarlierBufferCanExtendBelowExpansionFloor()
    {
        var range = new BufferMergeRange(10 * PageSize, 12 * PageSize);
        Assert.True(range.IncludeBuffer(11 * PageSize, 13 * PageSize, 17));
        range.IncludeEarlierBuffer(PageSize);
        Assert.Equal(PageSize, range.Begin);
        range.IncludeEarlierBuffer(3 * PageSize);
        Assert.Equal(PageSize, range.Begin);
    }

    [Fact]
    public void ExpansionOccursOnlyOnceButLaterBuffersStillExtendBounds()
    {
        var range = new BufferMergeRange(200 * PageSize, 202 * PageSize);
        Assert.False(range.IncludeBuffer(199 * PageSize, 201 * PageSize, 17));
        Assert.False(range.IncludeBuffer(330 * PageSize, 333 * PageSize, 17));
        Assert.Equal(333 * PageSize, range.End);
    }

    [Fact]
    public void ContainedBufferCanConsumeExpansionWithoutGrowingBounds()
    {
        var range = new BufferMergeRange(200 * PageSize, 204 * PageSize);
        Assert.False(range.IncludeBuffer(201 * PageSize, 203 * PageSize, 17));
        Assert.True(range.HasStreamExpansion);
        Assert.False(range.IncludeBuffer(203 * PageSize, 205 * PageSize, 17));
        Assert.Equal(205 * PageSize, range.End);
    }
}
