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
        var start = Stopwatch.GetTimestamp();
        for (var round = 0; round < rounds; round++)
        {
            Assert.True(wait.Wait(250_000));
        }

        var averageMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds / rounds;
        // It must really sleep (no spin returning at once) and must not be rounded up to the 15.6 ms scheduler tick.
        Assert.InRange(averageMs, 0.4, 5.0);
    }
}
