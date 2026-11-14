// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed unsafe class ShaderDescriptorLimitsTests
{
    [Fact]
    public void StableComputeBudgetUsesPerStagePerSetAndPoolLimits()
    {
        var limits = new PhysicalDeviceLimits
        {
            MaxPerStageDescriptorSampledImages = 16, MaxDescriptorSetSampledImages = 32,
            MaxPerStageDescriptorStorageImages = 32, MaxDescriptorSetStorageImages = 8,
            MaxPerStageDescriptorSamplers = uint.MaxValue, MaxDescriptorSetSamplers = uint.MaxValue,
            MaxPerStageDescriptorStorageBuffers = uint.MaxValue, MaxDescriptorSetStorageBuffers = uint.MaxValue,
            MaxPerStageResources = 27,
        };
        var budget = ShaderDescriptorLimits.ComputeImageLimits(limits);
        Assert.Equal(16u, budget.SampledImages);
        Assert.Equal(8u, budget.StorageImages);
        Assert.Equal(DescriptorHeap.PoolCapacity.Samplers, budget.Samplers);
        Assert.Equal(DescriptorHeap.PoolCapacity.StorageBuffers, budget.StorageBuffers);
        Assert.Equal(27u, budget.Resources);
        limits.MaxPerStageDescriptorSampledImages = limits.MaxDescriptorSetSampledImages = uint.MaxValue;
        limits.MaxPerStageDescriptorStorageImages = limits.MaxDescriptorSetStorageImages = uint.MaxValue;
        budget = ShaderDescriptorLimits.ComputeImageLimits(limits);
        Assert.Equal(DescriptorHeap.PoolCapacity.SampledImages, budget.SampledImages);
        Assert.Equal(DescriptorHeap.PoolCapacity.StorageImages, budget.StorageImages);
    }

    // The two stages of a pipeline built whole share one set and one pool, so each takes half of both.
    [Fact]
    public void StableGraphicsBudgetHalvesTheSetAndPoolLimits()
    {
        var limits = new PhysicalDeviceLimits
        {
            MaxPerStageDescriptorSampledImages = 16, MaxDescriptorSetSampledImages = 24,
            MaxPerStageDescriptorStorageImages = 4, MaxDescriptorSetStorageImages = 64,
            MaxPerStageDescriptorSamplers = uint.MaxValue, MaxDescriptorSetSamplers = uint.MaxValue,
            MaxPerStageDescriptorStorageBuffers = uint.MaxValue, MaxDescriptorSetStorageBuffers = uint.MaxValue,
            MaxPerStageResources = 27,
        };
        var budget = ShaderDescriptorLimits.GraphicsImageLimits(limits);
        Assert.Equal(12u, budget.SampledImages);
        Assert.Equal(4u, budget.StorageImages);
        Assert.Equal(DescriptorHeap.PoolCapacity.Samplers / 2, budget.Samplers);
        Assert.Equal(DescriptorHeap.PoolCapacity.StorageBuffers / 2, budget.StorageBuffers);
        Assert.Equal(27u, budget.Resources);
    }

    [Fact]
    public void CountsPhysicalDescriptorsAcrossBindingsAndStages()
    {
        var limits = new PhysicalDeviceLimits
        {
            MaxPerStageDescriptorSampledImages = 96, MaxDescriptorSetSampledImages = 128,
            MaxPerStageDescriptorSamplers = 8, MaxDescriptorSetSamplers = 16,
            MaxPerStageResources = 96,
        };
        DescriptorSetLayoutBinding[] bindings =
        [
            new(0, DescriptorType.SampledImage, 87, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.Sampler, 5, ShaderStageFlags.ComputeBit),
        ];
        ShaderDescriptorLimits.Validate(bindings, limits);
        bindings[0].DescriptorCount = 97;
        Assert.Contains("count=97 limit=96", Assert.Throws<InvalidOperationException>(() => ShaderDescriptorLimits.Validate(bindings, limits)).Message);
        bindings[0].DescriptorCount = 87;
        bindings[1] = new(1, DescriptorType.SampledImage, 50, ShaderStageFlags.FragmentBit);
        Assert.Contains("scope=pipeline", Assert.Throws<InvalidOperationException>(() => ShaderDescriptorLimits.Validate(bindings, limits)).Message);
    }
}
