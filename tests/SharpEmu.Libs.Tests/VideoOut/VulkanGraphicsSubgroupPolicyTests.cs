// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGraphicsSubgroupPolicyTests
{
    [Theory]
    [InlineData(32u, null, true)]
    [InlineData(64u, null, false)]
    [InlineData(64u, "1", true)]
    [InlineData(32u, "0", false)]
    [InlineData(32u, "invalid", true)]
    public void ResolvesNativeSizeAndDiagnosticOverride(
        uint nativeSubgroupSize,
        string? overrideValue,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanGraphicsSubgroupPolicy.ShouldUseNativeGraphicsSubgroups(
                nativeSubgroupSize,
                ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                overrideValue));
    }

    [Theory]
    [InlineData(ShaderStageFlags.ComputeBit, false)]
    [InlineData(ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit, false)]
    [InlineData(ShaderStageFlags.VertexBit, false)]
    [InlineData(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, true)]
    public void AutomaticGraphicsSubgroupsRequireBothShaderStages(ShaderStageFlags supportedStages, bool expected)
    {
        Assert.Equal(expected, VulkanGraphicsSubgroupPolicy.ShouldUseNativeGraphicsSubgroups(32, supportedStages, null));
    }

    // A forced SHARPEMU_GRAPHICS_SUBGROUPS=1 on a 64-lane device enables graphics subgroups, not pixel reductions.
    [Theory]
    [InlineData(32u, true, true, true)]
    [InlineData(64u, true, true, false)]
    [InlineData(32u, false, true, false)]
    [InlineData(32u, true, false, false)]
    public void PixelSubgroupArithmeticNeedsANative32LaneSubgroup(
        uint nativeSubgroupSize,
        bool graphicsSubgroups,
        bool fragmentArithmetic,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanGraphicsSubgroupPolicy.ShouldUsePixelSubgroupArithmetic(nativeSubgroupSize, graphicsSubgroups, fragmentArithmetic));
    }
}
