// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class HighResolutionWaitTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void WaitBlocksForAboutTheRequestedTime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var wait = new HighResolutionWait();
        if (!wait.IsAvailable)
        {
            return;
        }

        const int rounds = 100;
        var waitsMs = new double[rounds];
        for (var round = 0; round < rounds; round++)
        {
            var start = Stopwatch.GetTimestamp();
            Assert.True(wait.Wait(250_000));
            waitsMs[round] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        // The median, not the average: on a busy machine (shared CI runners) a few waits are preempted for several
        // milliseconds, which says nothing about the timer. It must really sleep (no spin returning at once) and must
        // not be rounded up to the 15.6 ms scheduler tick.
        Array.Sort(waitsMs);
        var medianMs = waitsMs[rounds / 2];
        Assert.InRange(medianMs, 0.2, 10.0);
    }
}
