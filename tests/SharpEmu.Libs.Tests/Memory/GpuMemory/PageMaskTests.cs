// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class PageMaskTests
{
    [Fact]
    public void CombinedRangeUpdateMatchesSeparateUpdates()
    {
        var random = new System.Random(413);
        for (var iteration = 0; iteration < 3000; iteration++)
        {
            var target = new PageMask();
            var first = new PageMask();
            var second = new PageMask();
            var third = new PageMask();
            for (var page = 0; page < 1024; page++)
            {
                if (random.Next(2) != 0) target.Set(page);
                if (random.Next(2) != 0) first.Set(page);
                if (random.Next(2) != 0) second.Set(page);
                if (random.Next(2) != 0) third.Set(page);
            }
            var expectedTarget = target;
            var expectedFirst = first;
            var expectedSecond = second;
            var expectedThird = third;
            var start = iteration == 0 ? 0 : random.Next(1024);
            var end = iteration == 0 ? 1024 : random.Next(start, 1025);
            expectedTarget.SetRange(start, end);
            expectedFirst.UnsetRange(start, end);
            expectedSecond.UnsetRange(start, end);
            expectedThird.UnsetRange(start, end);
            target.SetRangeAndClear(start, end, ref first, ref second, ref third);
            Assert.True((target ^ expectedTarget).None);
            Assert.True((first ^ expectedFirst).None);
            Assert.True((second ^ expectedSecond).None);
            Assert.True((third ^ expectedThird).None);
        }
    }

    [Fact]
    public void RangeQueryPreservesBitsAndUsesExclusiveEnd()
    {
        var mask = new PageMask();
        Assert.False(mask.AnyInRange(0, TrackerLayout.PagesPerBlock));
        for (var pageIndex = 0; pageIndex < TrackerLayout.PagesPerBlock; pageIndex++)
        {
            mask.Set(pageIndex);
            Assert.True(mask.AnyInRange(pageIndex, pageIndex + 1));
            Assert.True(mask.AnyInRange(0, TrackerLayout.PagesPerBlock));
            Assert.False(mask.AnyInRange(0, pageIndex));
            Assert.False(mask.AnyInRange(pageIndex + 1, TrackerLayout.PagesPerBlock));
            Assert.True(mask.Get(pageIndex));
            mask.Unset(pageIndex);
            Assert.True(mask.None);
        }
        mask.Fill();
        for (var pageIndex = 0; pageIndex < TrackerLayout.PagesPerBlock; pageIndex++)
            Assert.True(mask.AnyInRange(pageIndex, pageIndex + 1));
    }

    [Fact]
    public void RangeQueryMatchesCopiedMasksAcrossAllPageBoundaries()
    {
        var mask = new PageMask();
        foreach (var page in new[] { 0, 63, 64, 127, 512, 700, 1023 }) mask.Set(page);
        for (var start = 0; start <= TrackerLayout.PagesPerBlock; start++)
        {
            for (var end = start; end <= TrackerLayout.PagesPerBlock; end++)
                Assert.Equal(new PageMask(mask, start, end).Any, mask.AnyInRange(start, end));
        }
        Assert.False(mask.AnyInRange(0, TrackerLayout.PagesPerBlock + 1));
        Assert.False(mask.AnyInRange(-1, 1));
        Assert.False(mask.AnyInRange(10, 9));
    }

    [Fact]
    public void SetAndGet_TrackSingleBits()
    {
        var mask = new PageMask();
        mask.Set(0);
        mask.Set(63);
        mask.Set(64);
        mask.Set(1023);

        Assert.True(mask.Get(0));
        Assert.True(mask.Get(63));
        Assert.True(mask.Get(64));
        Assert.True(mask.Get(1023));
        Assert.False(mask.Get(1));
        Assert.False(mask.Get(65));
    }

    [Fact]
    public void SetRange_CrossesWordBoundaries()
    {
        var mask = new PageMask();
        mask.SetRange(60, 200);

        Assert.False(mask.Get(59));
        Assert.True(mask.Get(60));
        Assert.True(mask.Get(127));
        Assert.True(mask.Get(128));
        Assert.True(mask.Get(199));
        Assert.False(mask.Get(200));
        Assert.Equal((60, 200), mask.FindFirstSetRange());
        Assert.Equal((60, 200), mask.FindLastSetRange());
    }

    [Fact]
    public void SetRange_IgnoresEmptyOrOutOfBoundsInput()
    {
        var mask = new PageMask();
        mask.SetRange(5, 5);
        mask.SetRange(10, 1025);

        Assert.Equal((1024, 1024), mask.FindFirstSetRange());
        Assert.Equal((0, 0), mask.FindLastSetRange());
    }

    [Fact]
    public void FirstAndLastRange_FindSeparateRuns()
    {
        var mask = new PageMask();
        mask.SetRange(2, 4);
        mask.SetRange(700, 1024);

        Assert.Equal((2, 4), mask.FindFirstSetRange());
        Assert.Equal((700, 1024), mask.FindFirstSetRangeFrom(4));
        Assert.Equal((700, 1024), mask.FindLastSetRange());
        Assert.Equal((2, 4), mask.FindLastSetRangeBefore(700));
        Assert.Equal((1024, 1024), mask.FindFirstSetRangeFrom(1024));
        Assert.Equal((0, 0), mask.FindLastSetRangeBefore(2));
    }
}
