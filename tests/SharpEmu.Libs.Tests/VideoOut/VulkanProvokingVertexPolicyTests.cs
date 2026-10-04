// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanProvokingVertexPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void FeatureRequiresTheExtensionAndLastVertexSupport(
        bool extensionAvailable,
        bool featureSupported,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanProvokingVertexPolicy.ShouldEnable(extensionAvailable, featureSupported));
    }

    [Fact]
    public void PipelineModeMatchesTheGuestSelection()
    {
        Assert.Equal(
            ProvokingVertexModeEXT.FirstVertexExt,
            VulkanProvokingVertexPolicy.Mode(last: false));
        Assert.Equal(
            ProvokingVertexModeEXT.LastVertexExt,
            VulkanProvokingVertexPolicy.Mode(last: true));
    }
}
