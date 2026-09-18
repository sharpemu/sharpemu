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
    public void ThresholdCrossingKeepsCurrentGrowthDirection(bool growsLeft)
    {
        var range = new BufferMergeRange(200 * PageSize, 202 * PageSize);
        Assert.Equal(growsLeft, range.IncludeBuffer(
            (growsLeft ? 199UL : 201UL) * PageSize,
            (growsLeft ? 201UL : 203UL) * PageSize, 17));
        Assert.Equal((growsLeft ? 71UL : 200UL) * PageSize, range.Begin);
        Assert.Equal((growsLeft ? 202UL : 331UL) * PageSize, range.End);
        Assert.True(range.HasStreamExpansion);
    }

    [Fact]
    public void ExpansionClampsToAddressSpaceAndMinimumBegin()
    {
        var range = new BufferMergeRange(10 * PageSize, PageOwnerTable.AddressSpaceSize - PageSize);
        Assert.True(range.IncludeBuffer(9 * PageSize, PageOwnerTable.AddressSpaceSize, 17));
        Assert.Equal(2 * PageSize, range.Begin);
        Assert.Equal(PageOwnerTable.AddressSpaceSize, range.End);
    }

    [Fact]
    public void EarlierBufferCanExtendBelowExpansionFloor()
    {
        var range = new BufferMergeRange(10 * PageSize, 12 * PageSize);
        Assert.True(range.IncludeBuffer(9 * PageSize, 11 * PageSize, 17));
        range.IncludeEarlierBuffer(PageSize);
        Assert.Equal(PageSize, range.Begin);
        range.IncludeEarlierBuffer(3 * PageSize);
        Assert.Equal(PageSize, range.Begin);
    }

    [Fact]
    public void ExpansionOccursOnlyOnceButLaterBuffersStillExtendBounds()
    {
        var range = new BufferMergeRange(200 * PageSize, 202 * PageSize);
        Assert.False(range.IncludeBuffer(201 * PageSize, 203 * PageSize, 17));
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
