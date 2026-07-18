// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanSubgroupPolicyTests
{
    [Fact]
    public void ShaderWithoutWaveOperations_DoesNotRequireSubgroups()
    {
        Assert.True(
            VulkanVideoPresenter.TryResolveWave32Mode(
                default,
                ShaderStageFlags.FragmentBit,
                default,
                out var mode,
                out var error),
            error);
        Assert.Equal(VulkanWave32Mode.None, mode);
    }

    [Fact]
    public void FixedWave32FragmentStage_UsesNativeSubgroupSize()
    {
        var capabilities = Capabilities(
            subgroupSize: 32,
            supportedStages: ShaderStageFlags.FragmentBit,
            supportedOperations: SubgroupFeatureFlags.BasicBit);

        Assert.True(
            VulkanVideoPresenter.TryResolveWave32Mode(
                LaneIdentityRequirements(),
                ShaderStageFlags.FragmentBit,
                capabilities,
                out var mode,
                out var error),
            error);
        Assert.Equal(VulkanWave32Mode.Native, mode);
    }

    [Fact]
    public void Wave64StageWithPermittedSizeControl_RequiresWave32()
    {
        var capabilities = Capabilities(
            subgroupSize: 64,
            supportedStages: ShaderStageFlags.FragmentBit,
            supportedOperations: SubgroupFeatureFlags.BasicBit,
            subgroupSizeControl: true,
            minSubgroupSize: 32,
            maxSubgroupSize: 64,
            requiredSubgroupSizeStages: ShaderStageFlags.FragmentBit);

        Assert.True(
            VulkanVideoPresenter.TryResolveWave32Mode(
                LaneIdentityRequirements(),
                ShaderStageFlags.FragmentBit,
                capabilities,
                out var mode,
                out var error),
            error);
        Assert.Equal(VulkanWave32Mode.Required, mode);
    }

    [Fact]
    public void Wave64FragmentWithoutRequiredSizeSupport_IsRejected()
    {
        var capabilities = Capabilities(
            subgroupSize: 64,
            supportedStages: ShaderStageFlags.VertexBit |
                ShaderStageFlags.FragmentBit |
                ShaderStageFlags.ComputeBit,
            supportedOperations: SubgroupFeatureFlags.BasicBit,
            subgroupSizeControl: true,
            minSubgroupSize: 32,
            maxSubgroupSize: 64,
            requiredSubgroupSizeStages: ShaderStageFlags.ComputeBit);

        Assert.False(
            VulkanVideoPresenter.TryResolveWave32Mode(
                LaneIdentityRequirements(),
                ShaderStageFlags.FragmentBit,
                capabilities,
                out _,
                out var error));
        Assert.Contains("size 32 cannot be required", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedStage_IsRejected()
    {
        var capabilities = Capabilities(
            subgroupSize: 32,
            supportedStages: ShaderStageFlags.ComputeBit,
            supportedOperations: SubgroupFeatureFlags.BasicBit);

        Assert.False(
            VulkanVideoPresenter.TryResolveWave32Mode(
                LaneIdentityRequirements(),
                ShaderStageFlags.VertexBit,
                capabilities,
                out _,
                out var error));
        Assert.Contains("unsupported for shader stage", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void MissingRequiredSubgroupOperation_IsRejected(int featureValue)
    {
        var requirements = new Gen5ShaderSubgroupRequirements(
            Gen5ShaderSubgroupFeatures.LaneIdentity |
            (Gen5ShaderSubgroupFeatures)featureValue);
        var capabilities = Capabilities(
            subgroupSize: 32,
            supportedStages: ShaderStageFlags.ComputeBit,
            supportedOperations: SubgroupFeatureFlags.BasicBit);

        Assert.False(
            VulkanVideoPresenter.TryResolveWave32Mode(
                requirements,
                ShaderStageFlags.ComputeBit,
                capabilities,
                out _,
                out var error));
        Assert.Contains("lacks subgroup operations", error, StringComparison.Ordinal);
    }

    private static Gen5ShaderSubgroupRequirements LaneIdentityRequirements() =>
        new(Gen5ShaderSubgroupFeatures.LaneIdentity);

    private static VulkanSubgroupCapabilities Capabilities(
        uint subgroupSize,
        ShaderStageFlags supportedStages,
        SubgroupFeatureFlags supportedOperations,
        bool subgroupSizeControl = false,
        uint minSubgroupSize = 0,
        uint maxSubgroupSize = 0,
        ShaderStageFlags requiredSubgroupSizeStages = ShaderStageFlags.None) =>
        new(
            subgroupSize,
            supportedStages,
            supportedOperations,
            subgroupSizeControl,
            minSubgroupSize,
            maxSubgroupSize,
            requiredSubgroupSizeStages);
}
