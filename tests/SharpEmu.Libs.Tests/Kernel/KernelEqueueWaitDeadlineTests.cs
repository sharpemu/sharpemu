// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelEqueueWaitDeadlineTests
{
    [Fact]
    public void InfiniteWaitHasNoDeadlineByDefault()
    {
        var deadline = KernelEventQueueCompatExports.ComputeWaitEqueueDeadline(0, hasTimeout: false, timeoutUsec: 0);
        Assert.Equal(0, deadline);
    }

    [Fact]
    public void InfiniteWaitGetsBoundedDeadlineWhenConfigured()
    {
        var before = System.Diagnostics.Stopwatch.GetTimestamp();
        var deadline = KernelEventQueueCompatExports.ComputeWaitEqueueDeadline(2000, hasTimeout: false, timeoutUsec: 0);
        var after = System.Diagnostics.Stopwatch.GetTimestamp();

        Assert.NotEqual(0, deadline);
        Assert.True(deadline > before);
        Assert.InRange(deadline, before + 1900 * (System.Diagnostics.Stopwatch.Frequency / 1000L),
            after + 2100 * (System.Diagnostics.Stopwatch.Frequency / 1000L));
    }

    [Fact]
    public void ExplicitTimeoutIsUsedRegardlessOfInfiniteBound()
    {
        var deadline = KernelEventQueueCompatExports.ComputeWaitEqueueDeadline(2000, hasTimeout: true, timeoutUsec: 100_000);
        Assert.NotEqual(0, deadline);
    }
}
