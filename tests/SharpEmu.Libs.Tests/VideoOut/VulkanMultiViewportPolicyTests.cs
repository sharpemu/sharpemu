// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanMultiViewportPolicyTests
{
    [Theory]
    [InlineData(false, 16u, false, 1u)]
    [InlineData(true, 1u, false, 1u)]
    [InlineData(true, 15u, false, 1u)]
    [InlineData(true, 16u, true, 16u)]
    [InlineData(true, 32u, true, 16u)]
    public void DeviceFeatureAndLimitGateEveryGuestViewport(
        bool featureSupported,
        uint maxViewports,
        bool expectedEnabled,
        uint expectedAvailable)
    {
        Assert.Equal(
            expectedEnabled,
            VulkanMultiViewportPolicy.ShouldEnable(featureSupported, maxViewports));
        Assert.Equal(
            expectedAvailable,
            VulkanMultiViewportPolicy.AvailableViewportCount(featureSupported, maxViewports));
    }
}
