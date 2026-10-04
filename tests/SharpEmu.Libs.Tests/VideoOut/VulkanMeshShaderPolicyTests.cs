// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanMeshShaderPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void EnablementRequiresExtensionAndMeshFeature(
        bool extensionAvailable,
        bool meshShaderFeature,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanMeshShaderPolicy.ShouldEnable(extensionAvailable, meshShaderFeature));
    }

    [Fact]
    public void DrawAtEveryAdvertisedLimitIsAccepted()
    {
        var capabilities = Capabilities(total: 24, x: 4, y: 3, z: 2);

        Assert.True(VulkanMeshShaderPolicy.SupportsDraw(capabilities, 4, 3, 2));
    }

    [Theory]
    [InlineData(5u, 1u, 1u)]
    [InlineData(1u, 4u, 1u)]
    [InlineData(1u, 1u, 3u)]
    [InlineData(4u, 3u, 2u)]
    public void DrawOutsideAxisOrTotalLimitIsRejected(uint x, uint y, uint z)
    {
        var capabilities = Capabilities(total: 20, x: 4, y: 3, z: 2);

        Assert.False(VulkanMeshShaderPolicy.SupportsDraw(capabilities, x, y, z));
    }

    [Fact]
    public void DisabledCapabilityRejectsOtherwiseValidDraw()
    {
        var capabilities = Capabilities(total: 1, x: 1, y: 1, z: 1) with
        {
            Supported = false,
        };

        Assert.False(VulkanMeshShaderPolicy.SupportsDraw(capabilities, 1, 1, 1));
    }

    [Fact]
    public void TotalCountCheckCannotWrap()
    {
        var capabilities = Capabilities(
            total: uint.MaxValue,
            x: uint.MaxValue,
            y: uint.MaxValue,
            z: uint.MaxValue);

        Assert.False(VulkanMeshShaderPolicy.SupportsDraw(
            capabilities,
            uint.MaxValue,
            uint.MaxValue,
            uint.MaxValue));
    }

    [Fact]
    public void MeshStageMapsToVulkanMeshStageAndBarrier()
    {
        var shaderStage = DescriptorWriter.ShaderStageFlag(ShaderStage.Mesh);

        Assert.Equal(6u, PushData.MeshDrawDwordCount);
        Assert.Equal(ShaderStageFlags.MeshBitExt, shaderStage);
        Assert.Equal(
            PipelineStageFlags.MeshShaderBitExt,
            DescriptorWriter.PipelineStageFlag(shaderStage));
    }

    private static MeshShaderHostCapabilities Capabilities(
        uint total,
        uint x,
        uint y,
        uint z) =>
        new(
            Supported: true,
            MaxWorkGroupTotalCount: total,
            MaxWorkGroupCountX: x,
            MaxWorkGroupCountY: y,
            MaxWorkGroupCountZ: z,
            MaxWorkGroupInvocations: 32,
            MaxWorkGroupSizeX: 32,
            MaxWorkGroupSizeY: 1,
            MaxWorkGroupSizeZ: 1,
            MaxSharedMemorySize: 32 * 1024,
            MaxOutputVertices: 256,
            MaxOutputPrimitives: 256);
}
