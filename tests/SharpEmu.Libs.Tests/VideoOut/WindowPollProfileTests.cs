// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class WindowPollProfileTests
{
    [Fact]
    public void PollSamplesRequireFrameTracingInsideARenderScope()
    {
        Assert.False(WindowPollProfile.Enabled);
        using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.WindowLoop))
            Assert.Equal(RenderPhaseProfile.FrameTraceEnabled, WindowPollProfile.Enabled);
        Assert.False(WindowPollProfile.Enabled);
    }

    [Fact]
    public void CountersKeepTheLongestPollsAndAllTotals()
    {
        var counters = new WindowPollProfile.Counters();
        long[] durations = [2, 18, 1, 40, 3, 25];
        for (var index = 0; index < durations.Length; index++)
        {
            var startedAt = (index + 1) * Stopwatch.Frequency;
            counters.Record(startedAt, startedAt + durations[index] * Stopwatch.Frequency / 1000,
                index % 2 == 0, (uint)(0x200 + index));
        }

        Assert.Equal(6, counters.Calls);
        Assert.Equal(3, counters.ReturnedEvents);
        Assert.Equal(6, counters.AtLeastOneMillisecond);
        Assert.Equal(3, counters.AtLeastSixteenMilliseconds);
        Assert.Equal(89 * Stopwatch.Frequency / 1000, counters.TotalTicks);
        Assert.Equal(WindowPollProfile.Counters.Capacity, counters.SampleCount);
        Assert.Equal(new long[] { 40, 25, 18, 3 },
            counters.Longest.Select(sample => sample.ElapsedTicks * 1000 / Stopwatch.Frequency));
        Assert.False(counters.Longest[0].ReturnedEvent);
        Assert.Equal(0u, counters.Longest[0].EventType);
        Assert.Equal(4 * Stopwatch.Frequency, counters.Longest[0].StartedAt);
        Assert.Equal(0x204u, counters.Longest[3].EventType);
    }

    [Fact]
    public void ZeroDurationPollsRemainBoundedAndDoNotCountAsStalls()
    {
        var counters = new WindowPollProfile.Counters();
        for (var index = 0; index < 1000; index++)
            counters.Record(index, index, false, uint.MaxValue);
        Assert.Equal(1000, counters.Calls);
        Assert.Equal(0, counters.TotalTicks);
        Assert.Equal(0, counters.AtLeastOneMillisecond);
        Assert.Equal(0, counters.AtLeastSixteenMilliseconds);
        Assert.Equal(WindowPollProfile.Counters.Capacity, counters.SampleCount);
        Assert.All(counters.Longest, sample => Assert.Equal(0u, sample.EventType));
    }

    [Fact]
    public void NegativeDurationDoesNotReduceTotalsOrCountAsAStall()
    {
        var counters = new WindowPollProfile.Counters();
        counters.Record(100, 99, false, uint.MaxValue);

        Assert.Equal(1, counters.Calls);
        Assert.Equal(0, counters.TotalTicks);
        Assert.Equal(0, counters.AtLeastOneMillisecond);
        Assert.Equal(0, counters.AtLeastSixteenMilliseconds);
        Assert.Equal(0, counters.Longest[0].ElapsedTicks);
        Assert.Equal(0u, counters.Longest[0].EventType);
    }

    [Fact]
    public void EqualDurationsKeepTheFirstSamplesWhenFull()
    {
        var counters = new WindowPollProfile.Counters();
        for (var index = 0; index < 10; index++)
            counters.Record(index, index + Stopwatch.Frequency, true, (uint)index);

        Assert.Equal(10, counters.Calls);
        Assert.Equal(10, counters.ReturnedEvents);
        Assert.Equal(10 * Stopwatch.Frequency, counters.TotalTicks);
        Assert.Equal(WindowPollProfile.Counters.Capacity, counters.SampleCount);
        Assert.Equal(new uint[] { 0, 1, 2, 3 },
            counters.Longest.Select(sample => sample.EventType));
    }
}
