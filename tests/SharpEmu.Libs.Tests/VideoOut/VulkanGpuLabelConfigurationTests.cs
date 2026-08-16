// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

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
}
