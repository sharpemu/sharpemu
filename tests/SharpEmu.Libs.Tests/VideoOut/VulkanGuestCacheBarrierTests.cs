// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestCacheBarrierTests
{
    [Fact]
    public void ShaderDomains_UseShaderConsumerBarrier()
    {
        var operation = new GuestGpuCacheOperation(
            GuestGpuCacheDomain.ShaderL1 | GuestGpuCacheDomain.ShaderL2,
            GuestGpuCacheAction.MakeVisible | GuestGpuCacheAction.Invalidate,
            0x1000,
            0x2000,
            CoversAllMemory: false,
            RawCbDbControl: 0,
            RawGcrControl: 0);

        var plan = VulkanGuestCacheBarrierPlanner.Resolve(operation);

        Assert.Equal(
            PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit,
            plan.DestinationStages);
        Assert.Equal(
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            plan.DestinationAccess);
    }

    [Fact]
    public void RenderDomains_UseConservativeMemoryBarrier()
    {
        var operation = new GuestGpuCacheOperation(
            GuestGpuCacheDomain.Color | GuestGpuCacheDomain.Depth,
            GuestGpuCacheAction.MakeVisible | GuestGpuCacheAction.Invalidate,
            0,
            ulong.MaxValue,
            CoversAllMemory: true,
            RawCbDbControl: 0,
            RawGcrControl: 0);

        var plan = VulkanGuestCacheBarrierPlanner.Resolve(operation);

        Assert.Equal(PipelineStageFlags.AllCommandsBit, plan.DestinationStages);
        Assert.Equal(
            AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            plan.DestinationAccess);
    }
}
