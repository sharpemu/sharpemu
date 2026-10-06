// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// #876: the stall watchdog force-exits after a fixed 20s with no import progress.
// Long-running but genuinely progressing HLE work - the AMPR app0 index walk takes
// ~95s over 156,250 files on Astro Bot - has to be able to say so, and it lives in
// SharpEmu.Libs which cannot reference the CPU backend that owns the watchdog.
// These pin the seam's behaviour: reporting progress pulls the stamp back.
public sealed class EmulationActivityTests
{
    [Fact]
    public void MarkProgress_PullsTheStampBackToNow()
    {
        EmulationActivity.MarkProgress();
        var justMarked = EmulationActivity.TicksSinceProgress();

        Thread.Sleep(30);
        var afterWaiting = EmulationActivity.TicksSinceProgress();

        EmulationActivity.MarkProgress();
        var afterSecondMark = EmulationActivity.TicksSinceProgress();

        // Time passed while we waited...
        Assert.True(
            afterWaiting > justMarked,
            $"the stamp did not age: {justMarked} -> {afterWaiting}");
        // ...and reporting progress reset it.
        Assert.True(
            afterSecondMark < afterWaiting,
            $"MarkProgress did not reset the stamp: {afterWaiting} -> {afterSecondMark}");
    }

    [Fact]
    public void MarkProgress_IsCheapEnoughForPerItemCallsInABulkLoop()
    {
        // The AMPR walk calls this once per file across ~156k files. Bound the
        // cost of a large batch so a future change cannot quietly turn it into
        // something expensive enough to matter on the indexing path.
        const int iterations = 200_000;
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            EmulationActivity.MarkProgress();
        }

        stopwatch.Stop();

        // Generous: the point is to catch an accidental allocation or lock, not
        // to benchmark. A volatile write of a long is a few nanoseconds each.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"{iterations} MarkProgress calls took {stopwatch.Elapsed}, which is too slow "
            + "to call once per file during indexing");
    }
}
