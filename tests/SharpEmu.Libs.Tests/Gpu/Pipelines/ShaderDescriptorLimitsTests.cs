// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed unsafe class ShaderDescriptorLimitsTests
{
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
