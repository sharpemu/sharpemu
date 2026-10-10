// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// A title polling an unresolved import retries with no backoff, so the import
// dispatcher can see the same failure tens of millions of times. The sampler has to
// stay informative on the occurrences that matter while dropping the rest, and it
// must not lose one NID's diagnostics behind another's noise.
public sealed class ImportLogSamplerTests
{
    private const long Nowhere = 0;

    [Fact]
    public void LogsTheFirstEightOccurrences()
    {
        var sampler = new ImportLogSampler();

        for (var i = 1; i <= 8; i++)
        {
            Assert.True(sampler.ShouldLog("s9e3+YpRnzw", Nowhere), $"occurrence {i} should log");
        }
    }

    [Fact]
    public void SuppressesOccurrencesBetweenSamplePoints()
    {
        var sampler = new ImportLogSampler();
        for (var i = 1; i <= 8; i++)
        {
            _ = sampler.ShouldLog("s9e3+YpRnzw", Nowhere);
        }

        for (var i = 9; i < 10_000; i++)
        {
            Assert.False(sampler.ShouldLog("s9e3+YpRnzw", Nowhere), $"occurrence {i} should be suppressed");
        }
    }

    [Fact]
    public void EmitsAPeriodicHeartbeatSoALiveLoopStaysVisible()
    {
        var sampler = new ImportLogSampler();
        var logged = 0;

        for (var i = 1; i <= 30_000; i++)
        {
            if (sampler.ShouldLog("s9e3+YpRnzw", Nowhere))
            {
                logged++;
            }
        }

        // 8 head occurrences plus one every 10,000.
        Assert.Equal(11, logged);
    }

    [Fact]
    public void CountsEachNidIndependently()
    {
        var sampler = new ImportLogSampler();
        for (var i = 1; i <= 50_000; i++)
        {
            _ = sampler.ShouldLog("s9e3+YpRnzw", Nowhere);
        }

        // A different NID failing for the first time must still be reported, however
        // much noise the previous one produced.
        Assert.True(sampler.ShouldLog("L-Q3LEjIbgA", Nowhere));
    }

    [Fact]
    public void CountsEachDiscriminatorIndependently()
    {
        var sampler = new ImportLogSampler();
        for (var i = 1; i <= 50_000; i++)
        {
            _ = sampler.ShouldLog("s9e3+YpRnzw", discriminator: -2135425020);
        }

        // Same import, different outcome: still worth reporting.
        Assert.True(sampler.ShouldLog("s9e3+YpRnzw", discriminator: -2135425019));
    }

    [Fact]
    public void ResetRestoresTheHeadOccurrences()
    {
        var sampler = new ImportLogSampler();
        for (var i = 1; i <= 50_000; i++)
        {
            _ = sampler.ShouldLog("s9e3+YpRnzw", Nowhere);
        }

        Assert.False(sampler.ShouldLog("s9e3+YpRnzw", Nowhere));

        sampler.Reset();

        Assert.True(sampler.ShouldLog("s9e3+YpRnzw", Nowhere));
    }
}
