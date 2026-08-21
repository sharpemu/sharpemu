// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
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

    [Fact]
    public void SubmissionDependencyIncludesPriorLogicalQueueWork()
    {
        var required = new GuestGpuLabelDependency(0, 11);
        var prior = new GuestGpuLabelDependency(7, 0);

        var dependency =
            VulkanVideoPresenter.ResolveGpuLabelSubmissionDependency(
                required,
                prior);

        Assert.Equal(new GuestGpuLabelDependency(7, 11), dependency);
    }

    [Theory]
    [InlineData(7ul, 0ul)]
    [InlineData(0ul, 11ul)]
    public void SubmissionDependencyRetainsThePriorPhysicalQueueToken(
        ulong graphicsTimeline,
        ulong computeTimeline)
    {
        var prior = new GuestGpuLabelDependency(
            graphicsTimeline,
            computeTimeline);

        var dependency =
            VulkanVideoPresenter.ResolveGpuLabelSubmissionDependency(
                default,
                prior);

        Assert.Equal(prior, dependency);
    }
}
