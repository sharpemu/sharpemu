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
}
