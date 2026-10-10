// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// The AMPR open-file cache sizes itself from this, so a wrong answer either
// throws away a title's working set or exhausts the descriptor table. Report 0
// for "cannot tell" rather than a number a cache would trust.
public sealed class HostDescriptorLimitTests
{
    [Fact]
    public void SoftLimit_IsEitherUnknownOrAUsableDescriptorCount()
    {
        var limit = HostDescriptorLimit.SoftLimit;

        Assert.True(limit >= 0, $"descriptor limit must not be negative, was {limit}");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, limit);
            return;
        }

        if (limit == 0)
        {
            // getrlimit failed or reported RLIM_INFINITY; callers fall back.
            return;
        }

        // Every Unix the emulator runs on allows at least the POSIX minimum.
        Assert.True(limit >= 20, $"descriptor limit implausibly small: {limit}");
    }

    [Fact]
    public void SoftLimit_IsStableAcrossReads()
    {
        Assert.Equal(HostDescriptorLimit.SoftLimit, HostDescriptorLimit.SoftLimit);
    }

    // The limit has to permit at least as many open handles as the cache keeps,
    // or the cache's own bound is what exhausts the table.
    [Fact]
    public void SoftLimit_AllowsOpeningMoreHandlesThanTheMinimumCacheBound()
    {
        var limit = HostDescriptorLimit.SoftLimit;
        if (limit == 0 || OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(limit > 512, $"descriptor limit {limit} is below the minimum cache bound");
    }
}
