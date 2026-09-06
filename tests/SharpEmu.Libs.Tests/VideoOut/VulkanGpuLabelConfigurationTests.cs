// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGpuLabelConfigurationTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("enabled", true)]
    [InlineData("0", false)]
    public void TimelineIsEnabledUnlessTheUserOptsOut(
        string? setting,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.IsGpuLabelTimelineRequested(setting));
    }

    [Theory]
    [InlineData(3ul, 3ul)]
    [InlineData(1ul, 7ul)]
    public void LabelWaitsOnOrBeforeTheLastSignalAreAccepted(ulong wait, ulong lastSignal)
    {
        VulkanVideoPresenter.CheckLabelWaitOrder(wait, lastSignal);
    }

    [Fact]
    public void LabelWaitAheadOfItsSignalIsFatal()
    {
        var previous = SubmissionScheduler.OnFatal;
        var fatals = new List<string>();
        SubmissionScheduler.OnFatal = fatals.Add;
        try
        {
            Assert.Throws<InvalidOperationException>(() => VulkanVideoPresenter.CheckLabelWaitOrder(8, 7));
        }
        finally
        {
            SubmissionScheduler.OnFatal = previous;
        }

        Assert.Equal("label wait 8 precedes its signal (last signalled 7)", fatals.Single());
    }
}
