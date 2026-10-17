// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutDisplayClockTests
{
    [Theory]
    [InlineData(60u)]
    [InlineData(120u)]
    public void RepeatedReadersDoNotAdvanceCountOrChangeEventTimestamps(uint refreshRate)
    {
        var openedAt = Stopwatch.Frequency;
        var interval = VideoOutDisplayClock.RefreshInterval(refreshRate);
        var clock = new VideoOutDisplayClock(openedAt, 1000, 2000, (ulong)Stopwatch.Frequency * 3);
        clock.Advance(openedAt + interval - 1, refreshRate);
        Assert.Equal(0UL, clock.Count);
        Assert.Equal(0UL, clock.ProcessCounter);
        Assert.Equal(openedAt + interval, clock.NextTimestamp(refreshRate));
        for (var reader = 0; reader < 100; reader++)
        {
            clock.Advance(openedAt + 5 * interval + reader, refreshRate);
            Assert.Equal(5UL, clock.Count);
            Assert.Equal(openedAt + 5 * interval, clock.LastTimestamp);
            Assert.Equal(1000UL + (ulong)(5 * interval), clock.ProcessCounter);
            Assert.Equal(2000UL + (ulong)(15 * interval), clock.TimestampCounter);
            Assert.Equal((ulong)((UInt128)clock.ProcessCounter * 1_000_000 / (ulong)Stopwatch.Frequency), clock.ProcessMicroseconds);
        }
        clock.Advance(openedAt + 2 * interval, refreshRate);
        Assert.Equal(5UL, clock.Count);
        Assert.Equal(openedAt + 6 * interval, clock.NextTimestamp(refreshRate));
    }

    [Theory]
    [InlineData(60u, 0)]
    [InlineData(60u, 1)]
    [InlineData(60u, 2)]
    [InlineData(120u, 0)]
    [InlineData(120u, 1)]
    [InlineData(120u, 2)]
    public void VsyncDeadlineUsesPortRefreshRateAndFlipInterval(uint refreshRate, int flipRate)
    {
        var openedAt = Stopwatch.Frequency;
        var interval = VideoOutDisplayClock.RefreshInterval(refreshRate) * (flipRate + 1);
        Assert.Equal(openedAt + interval, GetNextFlipTimestamp(openedAt, openedAt, openedAt + 1, refreshRate, flipRate, 1));
        var late = openedAt + 10 * interval + interval / 2;
        Assert.Equal(late, GetNextFlipTimestamp(openedAt, openedAt, late, refreshRate, flipRate, 1));
        Assert.Equal(openedAt + 11 * interval, GetNextFlipTimestamp(openedAt, late, late + 1, refreshRate, flipRate, 1));
    }

    [Fact]
    public void AsapDoesNotInheritFlipRateLimit()
    {
        var openedAt = Stopwatch.Frequency;
        Assert.Equal(openedAt + 1, GetNextFlipTimestamp(openedAt, openedAt, openedAt + 1, 60, 2, 2));
    }

    [Theory]
    [InlineData(60u, 0)]
    [InlineData(60u, 1)]
    [InlineData(60u, 2)]
    [InlineData(120u, 0)]
    [InlineData(120u, 1)]
    [InlineData(120u, 2)]
    public void MultipleFlipsShareTheNextVblankWithoutMovingItOnRepeatedChecks(uint refreshRate, int flipRate)
    {
        var openedAt = Stopwatch.Frequency;
        var refreshInterval = VideoOutDisplayClock.RefreshInterval(refreshRate);
        var readyAt = openedAt + refreshInterval / 2;
        var expectedBoundary = openedAt + refreshInterval;
        foreach (var timestamp in new[] { readyAt, expectedBoundary - 1, expectedBoundary, expectedBoundary + 1,
            expectedBoundary + 10 * refreshInterval })
        {
            Assert.Equal(expectedBoundary, VideoOutDisplayClock.NextFlipTimestamp(openedAt, timestamp,
                timestamp, refreshRate, flipRate, 4, 1000, 100, 900, readyAt));
        }
        Assert.Equal(expectedBoundary, VideoOutDisplayClock.NextFlipTimestamp(openedAt, expectedBoundary,
            expectedBoundary, refreshRate, flipRate, 4, 1000, 100, 900, readyAt + 1));
        Assert.Equal(expectedBoundary + refreshInterval, VideoOutDisplayClock.NextFlipTimestamp(openedAt, expectedBoundary,
            expectedBoundary, refreshRate, flipRate, 4, 1000, 100, 900, expectedBoundary));
    }

    [Fact]
    public void WindowModeUsesScanlineMarginsAndDoesNotRepeatWithinOneWindow()
    {
        var openedAt = Stopwatch.Frequency;
        var interval = VideoOutDisplayClock.RefreshInterval(60);
        var bottom = interval * 900 / 1000;
        var middle = openedAt + interval / 2;
        Assert.Equal(openedAt + bottom, GetNextFlipTimestamp(openedAt, -1, middle, 60, 0, 3));
        Assert.Equal(openedAt + bottom, GetNextFlipTimestamp(openedAt, openedAt, middle, 60, 0, 3));
        var presented = openedAt + bottom + 1;
        Assert.Equal(openedAt + interval + bottom, GetNextFlipTimestamp(openedAt, presented, presented + 1, 60, 0, 3));
        Assert.Equal(openedAt + interval + 1, GetNextFlipTimestamp(openedAt, openedAt, openedAt + interval + 1, 60, 0, 3));
    }

    [Fact]
    public void WindowModeWithoutMarginsUsesTheNextRefreshWithoutRequiringAnExactTimestamp()
    {
        var openedAt = Stopwatch.Frequency;
        var interval = VideoOutDisplayClock.RefreshInterval(60);
        var late = openedAt + interval + 1;
        Assert.Equal(late, VideoOutDisplayClock.NextFlipTimestamp(openedAt, openedAt,
            late, 60, 0, 3, 1080, 0, int.MaxValue, late));
        Assert.Equal(late, VideoOutDisplayClock.NextFlipTimestamp(openedAt, -1,
            late, 60, 0, 3, 1080, 0, int.MaxValue, late));
    }

    [Fact]
    public void DifferentPortsHaveIndependentVblankOrigins()
    {
        var openedAt = Stopwatch.Frequency;
        var interval = VideoOutDisplayClock.RefreshInterval(60);
        var first = new VideoOutDisplayClock(openedAt, 0, 0, (ulong)Stopwatch.Frequency);
        var second = new VideoOutDisplayClock(openedAt + interval / 2, 0, 0, (ulong)Stopwatch.Frequency);
        first.Advance(openedAt + interval, 60);
        second.Advance(openedAt + interval, 60);
        Assert.Equal(1UL, first.Count);
        Assert.Equal(0UL, second.Count);
    }

    private static long GetNextFlipTimestamp(long openedAt, long lastPresentedAt, long timestamp, uint refreshRate, int flipRate, int flipMode) =>
        VideoOutDisplayClock.NextFlipTimestamp(openedAt, lastPresentedAt, timestamp, refreshRate, flipRate, flipMode, 1000, 100, 900, timestamp);
}
