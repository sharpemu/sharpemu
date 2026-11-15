// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Linked compiles share the free commit: small ones run beside a large one, large ones one at a time.
public sealed class LinkedCompileAdmissionTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    // A 19 MB module (13 GB estimated) and a 0.5 MB one (0.3 GB), with 40 GB free.
    [Fact]
    public void ASmallCompileRunsBesideALargeOne()
    {
        var admission = new LinkedCompileAdmission(() => 40 * GiB);
        Assert.True(admission.TryAdmit(19 * MiB, out _, out _));
        Assert.True(admission.TryAdmit(MiB / 2, out _, out var reserved));
        Assert.Equal(LinkedCompileAdmission.Estimate(19 * MiB), reserved);
    }

    [Fact]
    public void ALargeCompileWaitsWhileAnotherLargeOneRuns()
    {
        var admission = new LinkedCompileAdmission(() => 400 * GiB);
        Assert.True(admission.TryAdmit(19 * MiB, out _, out _));
        Assert.False(admission.TryAdmit(8 * MiB, out _, out _));
        admission.Release(19 * MiB);
        Assert.True(admission.TryAdmit(8 * MiB, out _, out _));
    }

    // The estimates of the running compiles count as committed: 17.5 GB free less the large compile's 13 GB leaves too
    // little for a 2 MB compile (1.4 GB) and the 4 GB margin, until the large compile ends.
    [Fact]
    public void TheRunningEstimatesCountAgainstTheFreeCommit()
    {
        var admission = new LinkedCompileAdmission(() => 35 * GiB / 2);
        Assert.True(admission.TryAdmit(19 * MiB, out _, out _));
        Assert.False(admission.TryAdmit(2 * MiB, out var available, out var reserved));
        Assert.Equal(35 * GiB / 2, available);
        Assert.Equal(LinkedCompileAdmission.Estimate(19 * MiB), reserved);
        admission.Release(19 * MiB);
        Assert.True(admission.TryAdmit(2 * MiB, out _, out _));
    }

    [Fact]
    public void ACompileWaitsUntilTheFreeCommitHoldsItsEstimateAndTheMargin()
    {
        var free = 5 * GiB;
        var admission = new LinkedCompileAdmission(() => free);
        Assert.False(admission.TryAdmit(2 * MiB, out _, out _));
        free = 6 * GiB;
        Assert.True(admission.TryAdmit(2 * MiB, out _, out _));
    }

    // With the free commit unknown, one compile runs at a time.
    [Fact]
    public void WithoutTheFreeCommitOneCompileRunsAtATime()
    {
        var admission = new LinkedCompileAdmission(() => -1);
        Assert.True(admission.TryAdmit(MiB, out _, out _));
        Assert.False(admission.TryAdmit(MiB, out _, out _));
        admission.Release(MiB);
        Assert.True(admission.TryAdmit(MiB, out _, out _));
    }
}
