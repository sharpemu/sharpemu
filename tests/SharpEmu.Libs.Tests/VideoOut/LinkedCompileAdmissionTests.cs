// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.Logging;
using Xunit;
using Result = SharpEmu.Libs.VideoOut.LinkedCompileAdmission.Result;
using Source = SharpEmu.Libs.VideoOut.LinkedCompileAdmission.Source;

namespace SharpEmu.Libs.Tests.VideoOut;

// Linked compiles share the free commit: small ones run beside a large one, large ones one at a time.
public sealed class LinkedCompileAdmissionTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    private static LinkedCompileAdmission WithCommit(Func<long> availableCommit) =>
        new(() => new(availableCommit(), 8 * GiB, 32 * GiB));

    // A 19 MB module (13 GB estimated) and a 0.5 MB one (0.3 GB), with 40 GB free.
    [Fact]
    public void ASmallCompileRunsBesideALargeOne()
    {
        var admission = WithCommit(() => 40 * GiB);
        Assert.Equal(Result.Admitted, admission.Admit(19 * MiB, out _, out _));
        Assert.Equal(Result.Admitted, admission.Admit(MiB / 2, out _, out var reserved));
        Assert.Equal(LinkedCompileAdmission.Estimate(19 * MiB), reserved);
    }

    [Fact]
    public void ALargeCompileWaitsWhileAnotherLargeOneRuns()
    {
        var admission = WithCommit(() => 400 * GiB);
        Assert.Equal(Result.Admitted, admission.Admit(19 * MiB, out _, out _));
        Assert.Equal(Result.Wait, admission.Admit(8 * MiB, out _, out _));
        admission.Release(19 * MiB);
        Assert.Equal(Result.Admitted, admission.Admit(8 * MiB, out _, out _));
    }

    // The estimates of the running compiles count as committed: 17.5 GB free less the large compile's 13 GB leaves too
    // little for a 2 MB compile (1.4 GB) and the 4 GB margin, until the large compile ends.
    [Fact]
    public void TheRunningEstimatesCountAgainstTheFreeCommit()
    {
        var admission = WithCommit(() => 35 * GiB / 2);
        Assert.Equal(Result.Admitted, admission.Admit(19 * MiB, out _, out _));
        Assert.Equal(Result.Wait, admission.Admit(2 * MiB, out var memory, out var reserved));
        Assert.Equal(35 * GiB / 2, memory.AvailableCommitBytes);
        Assert.Equal(LinkedCompileAdmission.Estimate(19 * MiB), reserved);
        admission.Release(19 * MiB);
        Assert.Equal(Result.Admitted, admission.Admit(2 * MiB, out _, out _));
    }

    [Fact]
    public void ACompileWaitsUntilTheFreeCommitHoldsItsEstimateAndTheMargin()
    {
        var free = 5 * GiB;
        var admission = WithCommit(() => free);
        Assert.Equal(Result.Wait, admission.Admit(2 * MiB, out _, out _));
        free = 6 * GiB;
        Assert.Equal(Result.Admitted, admission.Admit(2 * MiB, out _, out _));
    }

    // With the free commit unknown, one compile runs at a time.
    [Fact]
    public void WithoutTheFreeCommitOneCompileRunsAtATime()
    {
        var admission = WithCommit(() => -1);
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _));
        Assert.Equal(Result.Wait, admission.Admit(MiB, out _, out _));
        admission.Release(MiB);
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _));
    }

    [Fact]
    public void LowRamDefersOnlyAdditionalBackgroundWork()
    {
        var admission = new LinkedCompileAdmission(() => new(40 * GiB, 0, 16 * GiB));
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
        Assert.Equal(Result.WaitForRam, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out var reserved, Source.Demand));
        Assert.Equal(LinkedCompileAdmission.Estimate(MiB), reserved);
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _, Source.BootPrewarm));
        admission.Release(MiB);
        admission.Release(MiB);
        admission.Release(MiB);
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
    }

    [Theory]
    [InlineData(4, 512)]
    [InlineData(16, 1024)]
    [InlineData(64, 2048)]
    public void BackgroundOverlapResumesAtTheUpperWatermark(long ramGiB, long lowMiB)
    {
        var low = lowMiB * MiB;
        var physical = low + 1;
        var admission = new LinkedCompileAdmission(() => new(40 * GiB, physical, ramGiB * GiB));
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _));
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
        admission.Release(MiB);
        physical = low;
        Assert.Equal(Result.WaitForRam, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
        physical = low + low / 2 - 1;
        Assert.Equal(Result.WaitForRam, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
        physical++;
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
    }

    [Fact]
    public void LosingThePhysicalSampleClearsPressureAndPreservesTheCommitGuard()
    {
        var memory = new HostMemoryAvailability(40 * GiB, 0, 16 * GiB);
        var admission = new LinkedCompileAdmission(() => memory);
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _));
        Assert.Equal(Result.WaitForRam, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
        memory = new(40 * GiB, -1, -1);
        Assert.Equal(Result.Admitted, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
        memory = new(-1, -1, -1);
        Assert.Equal(Result.Wait, admission.Admit(MiB, out _, out _, Source.BackgroundPrewarm));
    }

}
