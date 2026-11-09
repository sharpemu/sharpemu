// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class OpenBatchPolicyTests
{
    private const long Opened = 1_000_000;

    // A clock that moves on by a fixed step each time it is read.
    private sealed class SteppingClock(long start, long step)
    {
        private long _now = start;

        public int Reads { get; private set; }

        public long Read()
        {
            Reads++;
            var now = _now;
            _now += step;
            return now;
        }
    }

    [Fact]
    public void ABatchStaysOpenWhileTheCommandThreadHasWork()
    {
        var clock = new SteppingClock(Opened, 1);
        Assert.True(OpenBatchPolicy.KeepOpen(Opened, clock.Read, static () => true, static () => false));
    }

    [Fact]
    public void ABatchAsOldAsTheLimitIsSubmitted()
    {
        var clock = new SteppingClock(Opened + OpenBatchPolicy.MaxAgeTicks, 1);
        Assert.False(OpenBatchPolicy.KeepOpen(Opened, clock.Read, static () => true, static () => false));
    }

    // A guest queue waiting on memory may wait for a label the open batch writes.
    [Fact]
    public void ABatchIsSubmittedWhileAGuestQueueWaitsOnMemory()
    {
        var clock = new SteppingClock(Opened, 1);
        Assert.False(OpenBatchPolicy.KeepOpen(Opened, clock.Read, static () => true, static () => true));
    }

    [Fact]
    public void ABatchIsSubmittedWhenNoWorkArrivesWithinTheGrace()
    {
        var step = Math.Max(1, OpenBatchPolicy.GraceTicks / 8);
        var clock = new SteppingClock(Opened, step);
        Assert.False(OpenBatchPolicy.KeepOpen(Opened, clock.Read, static () => false, static () => false));
        Assert.InRange(clock.Reads, 2, 16);
    }

    [Fact]
    public void WorkArrivingWithinTheGraceKeepsTheBatchOpen()
    {
        var clock = new SteppingClock(Opened, 1);
        var polls = 0;
        Assert.True(OpenBatchPolicy.KeepOpen(Opened, clock.Read, () => ++polls >= 3, static () => false));
        Assert.Equal(3, polls);
    }
}
