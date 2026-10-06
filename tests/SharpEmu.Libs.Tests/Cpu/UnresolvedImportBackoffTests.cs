// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// #619: a title polling an unimplemented import spun the dispatcher as fast as
// the host allowed, allocating a continuation per call - ~9GB/min measured on
// Demon's Souls. Repeats of the same unresolved NID are now throttled. These
// pin the shape of the delay rather than a magic threshold, so tuning the
// constants does not rewrite the test.
public sealed class UnresolvedImportBackoffTests
{
    private const int MaxObservedDelayMs = 50;

    // Comfortably past the point the 1ms-per-threshold-step ramp reaches the cap,
    // so "already capped" is a stable property rather than a boundary.
    private const int FarOutFailureCount = 65536;

    private static int FirstThrottledFailure()
    {
        for (var failures = 1; failures <= 4096; failures++)
        {
            if (DirectExecutionBackend.UnresolvedImportBackoffMs(failures) > 0)
            {
                return failures;
            }
        }

        Assert.Fail("backoff never engages within 4096 consecutive failures");
        return 0;
    }

    [Fact]
    public void EarlyFailures_AreNotThrottledSoOrdinaryProbingIsUntouched()
    {
        var first = FirstThrottledFailure();

        // Everything before the threshold must cost nothing, otherwise a title
        // that probes an absent API a handful of times would be slowed.
        for (var failures = 1; failures < first; failures++)
        {
            Assert.Equal(0, DirectExecutionBackend.UnresolvedImportBackoffMs(failures));
        }
    }

    [Fact]
    public void FirstThrottledFailure_IntroducesTheSmallestPossibleDelay()
    {
        var first = FirstThrottledFailure();

        Assert.Equal(1, DirectExecutionBackend.UnresolvedImportBackoffMs(first));
    }

    [Fact]
    public void Delay_GrowsMonotonicallyAndIsCapped()
    {
        var first = FirstThrottledFailure();
        var previous = 0;

        for (var failures = first; failures <= FarOutFailureCount; failures++)
        {
            var delay = DirectExecutionBackend.UnresolvedImportBackoffMs(failures);

            Assert.True(
                delay >= previous,
                $"delay went backwards at {failures} failures: {previous} -> {delay}");
            Assert.InRange(delay, 0, MaxObservedDelayMs);
            previous = delay;
        }
    }

    [Fact]
    public void Delay_StopsGrowingOnceCapped()
    {
        Assert.Equal(
            DirectExecutionBackend.UnresolvedImportBackoffMs(FarOutFailureCount),
            DirectExecutionBackend.UnresolvedImportBackoffMs(FarOutFailureCount * 2));
        Assert.Equal(
            MaxObservedDelayMs,
            DirectExecutionBackend.UnresolvedImportBackoffMs(FarOutFailureCount));
    }
}
