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

    [Fact]
    public void ExactBufferRange_UsesResourceBarrier()
    {
        var operation = CreateRangedOperation(0x1800, 0x200);
        VulkanGuestCacheResourceRange[] resources =
        [
            new(
                0x1000,
                0x1000,
                VulkanGuestCacheResourceKind.Buffer),
            new(
                0x9000,
                0x1000,
                VulkanGuestCacheResourceKind.Buffer),
        ];

        var plan = VulkanGuestCacheBarrierPlanner.ResolveResources(
            operation,
            resources);

        Assert.False(plan.UseGlobalBarrier);
        Assert.Equal(1, plan.MatchedResourceCount);
    }

    [Fact]
    public void UntrackedImageLayout_UsesGlobalFallback()
    {
        var operation = CreateRangedOperation(0x1800, 0x200);
        VulkanGuestCacheResourceRange[] resources =
        [
            new(
                0x1000,
                0x1000,
                VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout),
        ];

        var plan = VulkanGuestCacheBarrierPlanner.ResolveResources(
            operation,
            resources);

        Assert.True(plan.UseGlobalBarrier);
        Assert.Equal(1, plan.MatchedResourceCount);
    }

    [Fact]
    public void BufferAndCachedTextureOverlap_UsesGlobalFallback()
    {
        var operation = CreateRangedOperation(0x1800, 0x200);
        VulkanGuestCacheResourceRange[] resources =
        [
            new(
                0x1000,
                0x1000,
                VulkanGuestCacheResourceKind.Buffer),
            new(
                0x1700,
                0x400,
                VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout),
        ];

        var plan = VulkanGuestCacheBarrierPlanner.ResolveResources(
            operation,
            resources);

        Assert.True(plan.UseGlobalBarrier);
        Assert.Equal(2, plan.MatchedResourceCount);
    }

    [Fact]
    public void UnknownImageSpan_UsesGlobalFallback()
    {
        var operation = CreateRangedOperation(0x5000, 0x100);
        VulkanGuestCacheResourceRange[] resources =
        [
            new(
                0,
                ulong.MaxValue,
                VulkanGuestCacheResourceKind.ImageWithoutTrackedLayout),
        ];

        var plan = VulkanGuestCacheBarrierPlanner.ResolveResources(
            operation,
            resources);

        Assert.True(plan.UseGlobalBarrier);
        Assert.Equal(1, plan.MatchedResourceCount);
    }

    [Fact]
    public void ExactDepthRange_UsesResourceBarrier()
    {
        var operation = CreateRangedOperation(0x2800, 0x200);
        VulkanGuestCacheResourceRange[] resources =
        [
            new(
                0x2000,
                0x1000,
                VulkanGuestCacheResourceKind.DepthImage),
        ];

        var plan = VulkanGuestCacheBarrierPlanner.ResolveResources(
            operation,
            resources);

        Assert.False(plan.UseGlobalBarrier);
        Assert.Equal(1, plan.MatchedResourceCount);
    }

    [Fact]
    public void AllMemoryOperation_UsesGlobalFallback()
    {
        var operation = CreateRangedOperation(0, ulong.MaxValue) with
        {
            CoversAllMemory = true,
        };
        VulkanGuestCacheResourceRange[] resources =
        [
            new(
                0x1000,
                0x1000,
                VulkanGuestCacheResourceKind.Buffer),
        ];

        var plan = VulkanGuestCacheBarrierPlanner.ResolveResources(
            operation,
            resources);

        Assert.True(plan.UseGlobalBarrier);
        Assert.Equal(0, plan.MatchedResourceCount);
    }

    [Fact]
    public void UnmappedRange_UsesGlobalFallback()
    {
        var operation = CreateRangedOperation(0x5000, 0x100);
        VulkanGuestCacheResourceRange[] resources =
        [
            new(
                0x1000,
                0x1000,
                VulkanGuestCacheResourceKind.Buffer),
        ];

        var plan = VulkanGuestCacheBarrierPlanner.ResolveResources(
            operation,
            resources);

        Assert.True(plan.UseGlobalBarrier);
        Assert.Equal(0, plan.MatchedResourceCount);
    }

    private static GuestGpuCacheOperation CreateRangedOperation(
        ulong baseAddress,
        ulong sizeBytes) => new(
            GuestGpuCacheDomain.ShaderL2,
            GuestGpuCacheAction.MakeVisible | GuestGpuCacheAction.Invalidate,
            baseAddress,
            sizeBytes,
            CoversAllMemory: false,
            RawCbDbControl: 0,
            RawGcrControl: 1u << 14);
}
