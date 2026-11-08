// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class BarrierTallyTests
{
    [Fact]
    public void AllCommandsOrEveryGraphicsAndComputeStageIsAFullSource()
    {
        Assert.True(BarrierTally.IsFullSource(PipelineStageFlags.AllCommandsBit));
        Assert.True(BarrierTally.IsFullSource(PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit));
        Assert.True(BarrierTally.IsFullSource(PipelineStageFlags.AllCommandsBit | PipelineStageFlags.HostBit));
        Assert.False(BarrierTally.IsFullSource(PipelineStageFlags.AllGraphicsBit));
        Assert.False(BarrierTally.IsFullSource(PipelineStageFlags.ComputeShaderBit));
        Assert.False(BarrierTally.IsFullSource(PipelineStageFlags.TransferBit));
    }

    [Fact]
    public void OnlyNonAttachmentReadsInTheGeneralLayoutCountAsReadAfterRead()
    {
        var sampled = new ImageAccessState(PipelineStageFlags.FragmentShaderBit, AccessFlags.ShaderReadBit, ImageLayout.General);
        Assert.True(sampled.ReadsAfterRead(AccessFlags.TransferReadBit));
        Assert.True(sampled.ReadsAfterRead(AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit));
        Assert.False(sampled.ReadsAfterRead(AccessFlags.ShaderWriteBit));
        Assert.False(sampled.ReadsAfterRead(AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentReadBit));
        Assert.False((sampled with { Layout = ImageLayout.Undefined }).ReadsAfterRead(AccessFlags.ShaderReadBit));
        Assert.False((sampled with { Access = AccessFlags.TransferWriteBit }).ReadsAfterRead(AccessFlags.ShaderReadBit));
    }

    [Fact]
    public void OnlyTheSameAttachmentAccessRepeatsAnAttachment()
    {
        const AccessFlags color = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit;
        var target = new ImageAccessState(PipelineStageFlags.AllGraphicsBit, color, ImageLayout.General);
        Assert.True(target.RepeatsAttachmentAccess(color));
        Assert.False(target.RepeatsAttachmentAccess(color | AccessFlags.ShaderReadBit));
        Assert.False(target.RepeatsAttachmentAccess(AccessFlags.ColorAttachmentReadBit));
        Assert.False((target with { Access = color | AccessFlags.ShaderWriteBit }).RepeatsAttachmentAccess(color | AccessFlags.ShaderWriteBit));
    }
}
