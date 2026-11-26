// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ShaderWriteBarrierTests
{
    // A vertex or compute stage that writes the arguments of a later indirect draw (GPU-driven culling)
    // must make them visible to the draw's argument read, or the draw reads stale arguments.
    [Theory]
    [InlineData(PipelineStageFlags.VertexShaderBit)]
    [InlineData(PipelineStageFlags.FragmentShaderBit)]
    [InlineData(PipelineStageFlags.ComputeShaderBit)]
    public void ShaderWritesReachIndirectArgumentReads(PipelineStageFlags source)
    {
        var request = VulkanVideoPresenter.ShaderWriteBarrierRequest(source, meshShader: false, taskShader: false, tessellation: true);

        Assert.Equal(source, request.SourceStages);
        Assert.Equal(AccessFlags.ShaderWriteBit, request.SourceAccess);
        Assert.True(request.DestinationStages.HasFlag(PipelineStageFlags.DrawIndirectBit));
        Assert.True(request.DestinationAccess.HasFlag(AccessFlags.IndirectCommandReadBit));
        Assert.True(request.DestinationStages.HasFlag(PipelineStageFlags.TessellationControlShaderBit));
        Assert.False(request.DestinationStages.HasFlag(PipelineStageFlags.MeshShaderBitExt));
    }
}
