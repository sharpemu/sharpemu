// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageBarrierTests
{
    [Fact]
    public void SampledImageReadStagesCoverEverySupportedShaderConsumer()
    {
        var stages = VulkanVideoPresenter.SampledImageShaderReadStages;

        Assert.Equal(
            PipelineStageFlags.VertexShaderBit,
            stages & PipelineStageFlags.VertexShaderBit);
        Assert.Equal(
            PipelineStageFlags.FragmentShaderBit,
            stages & PipelineStageFlags.FragmentShaderBit);
        Assert.Equal(
            PipelineStageFlags.ComputeShaderBit,
            stages & PipelineStageFlags.ComputeShaderBit);
    }

    [Fact]
    public void OverwriteSourceStageDependsOnWhetherTheImageWasInitialized()
    {
        Assert.Equal(
            PipelineStageFlags.TopOfPipeBit,
            VulkanVideoPresenter.SampledImageOverwriteSourceStages(initialized: false));
        Assert.Equal(
            VulkanVideoPresenter.SampledImageShaderReadStages,
            VulkanVideoPresenter.SampledImageOverwriteSourceStages(initialized: true));
    }
}
