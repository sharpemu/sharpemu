// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGraphicsPipelinePolicyTests
{
    [Fact]
    public void VertexStageKeepsFixedFunctionInputAndVertexBindings()
    {
        var policy = VulkanGraphicsPipelinePolicy.For(ShaderStageKind.Vertex);

        Assert.Equal(ShaderStage.Vertex, policy.ResourceStage);
        Assert.Equal(ShaderStageFlags.VertexBit, policy.VulkanStage);
        Assert.Equal(
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            policy.PushConstantStages);
        Assert.True(policy.UsesVertexInput);
    }

    [Fact]
    public void MeshStageUsesMeshBindingsAndOmitsFixedFunctionInput()
    {
        var policy = VulkanGraphicsPipelinePolicy.For(ShaderStageKind.Mesh);

        Assert.Equal(ShaderStage.Mesh, policy.ResourceStage);
        Assert.Equal(ShaderStageFlags.MeshBitExt, policy.VulkanStage);
        Assert.Equal(
            ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
            policy.PushConstantStages);
        Assert.False(policy.UsesVertexInput);
    }

    [Theory]
    [InlineData(ShaderStageKind.Vertex, PrimitiveTopology.PatchList, true)]
    [InlineData(ShaderStageKind.Vertex, PrimitiveTopology.TriangleList, false)]
    [InlineData(ShaderStageKind.Mesh, PrimitiveTopology.PatchList, false)]
    [InlineData(ShaderStageKind.Mesh, PrimitiveTopology.TriangleList, false)]
    public void RectangleStagesAreNeverBuiltForMeshStages(
        ShaderStageKind stage,
        PrimitiveTopology topology,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanGraphicsPipelinePolicy.UsesRectangleListStages(stage, topology));
    }

    [Fact]
    public void RectangleParametersMatchThePixelModulesLocationRemapping()
    {
        var controls = new uint[PixelInputInfo.InterpolatorCount];
        controls[2] = 5u | 0x400u;
        controls[7] = 5u;
        var description = new GraphicsPipelineDescription
        {
            Rendering = new PipelineRenderingState(),
            VertexInput = new PipelineVertexInputState(),
            VertexInfo = new VertexInputInfo(),
            VertexProgram = default,
            VertexStage = new ShaderProgramInfo { ParameterExportMask = 1u << 5 },
            PixelInfo = new PixelInputInfo { InputCount = 8, InterpolatorSettings = controls },
            PixelProgram = default,
            PixelStage = new ShaderProgramInfo { PixelParameterInputs = [2, 7] },
            StaticParameters = new PipelineStaticParameters(),
        };

        Assert.Equal(
            [
                new RectangleListShaderParameter(5, 5, Flat: true),
                new RectangleListShaderParameter(5, 7, Flat: false),
            ],
            VulkanGraphicsPipelinePolicy.RectangleListParameters(description));
    }

    [Fact]
    public void RectangleCustomInterpolationIsNotCollapsedToFlat()
    {
        var controls = new uint[PixelInputInfo.InterpolatorCount];
        controls[2] = 5u | 0x400u;
        var description = new GraphicsPipelineDescription
        {
            Rendering = new PipelineRenderingState(),
            VertexInput = new PipelineVertexInputState(),
            VertexInfo = new VertexInputInfo(),
            VertexProgram = default,
            VertexStage = new ShaderProgramInfo { ParameterExportMask = 1u << 5 },
            PixelInfo = new PixelInputInfo
            {
                InputCount = 3,
                InterpolatorSettings = controls,
                CustomInterpolationMask = 1u << 2,
            },
            PixelProgram = default,
            PixelStage = new ShaderProgramInfo { PixelParameterInputs = [2] },
            StaticParameters = new PipelineStaticParameters(),
        };

        Assert.Equal(
            [new RectangleListShaderParameter(5, 5, Flat: false)],
            VulkanGraphicsPipelinePolicy.RectangleListParameters(description));
    }

    [Fact]
    public void RectangleInputBeyondDeclaredCountUsesIdentityLocation()
    {
        var description = new GraphicsPipelineDescription
        {
            Rendering = new PipelineRenderingState(),
            VertexInput = new PipelineVertexInputState(),
            VertexInfo = new VertexInputInfo(),
            VertexProgram = default,
            VertexStage = new ShaderProgramInfo { ParameterExportMask = 1u << 7 },
            PixelInfo = new PixelInputInfo
            {
                InputCount = 3,
                InterpolatorSettings = new uint[PixelInputInfo.InterpolatorCount],
            },
            PixelProgram = default,
            PixelStage = new ShaderProgramInfo { PixelParameterInputs = [7] },
            StaticParameters = new PipelineStaticParameters(),
        };

        Assert.Equal(
            [new RectangleListShaderParameter(7, 7, Flat: false)],
            VulkanGraphicsPipelinePolicy.RectangleListParameters(description));
    }
}
