// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PerfOverlayFrameRateAverageTests
{
    [Fact]
    public void SlowGuestCadenceDoesNotReadZeroBetweenFrames()
    {
        var average = new PerfOverlay.FrameRateAverage();

        // Two flips in one window, then a window holding none.
        Assert.Equal(2.0, average.Add(2, 1.0), 3);
        Assert.Equal(1.0, average.Add(0, 1.0), 3);
    }

    [Fact]
    public void StallReachesZeroOnceTheAveragingSpanHasPassed()
    {
        var average = new PerfOverlay.FrameRateAverage();
        average.Add(30, 1.0);

        for (var window = 0; window < PerfOverlay.FrameRateAverage.WindowCount - 1; window++)
            Assert.True(average.Add(0, 1.0) > 0);

        Assert.Equal(0, average.Add(0, 1.0));
    }

    [Fact]
    public void UnevenWindowsAreWeightedByTheirLength()
    {
        var average = new PerfOverlay.FrameRateAverage();
        average.Add(60, 1.0);

        Assert.Equal(40.0, average.Add(20, 1.0), 3);
        Assert.Equal(100.0 / 3.5, average.Add(20, 1.5), 3);
    }
}
