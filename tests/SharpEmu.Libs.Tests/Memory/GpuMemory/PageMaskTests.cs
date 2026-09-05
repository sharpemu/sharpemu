// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory.GpuMemory;

public sealed class PageMaskTests
{
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
