// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Locks in the physical-device type ranking used to pick a Vulkan adapter. The regression these guard
// against is a software rasterizer (Cpu) outscoring a real integrated GPU, which would silently drop
// the emulator into software rendering on integrated-only hosts (Mesa lavapipe, Apple Silicon).
public sealed class VulkanDeviceScoringTests
{
    [Fact]
    public void IntegratedGpu_RanksAboveSoftwareRasterizer()
    {
        Assert.True(
            VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.IntegratedGpu) >
            VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.Cpu));
    }

    [Fact]
    public void EveryRealDeviceType_OutranksSoftwareRasterizer()
    {
        var cpu = VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.Cpu);
        Assert.True(VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.DiscreteGpu) > cpu);
        Assert.True(VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.VirtualGpu) > cpu);
        Assert.True(VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.IntegratedGpu) > cpu);
        Assert.True(VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.Other) > cpu);
    }

    [Fact]
    public void DiscreteGpu_RanksHighest()
    {
        var discrete = VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.DiscreteGpu);
        Assert.True(discrete > VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.VirtualGpu));
        Assert.True(discrete > VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.IntegratedGpu));
        Assert.True(discrete > VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.Cpu));
    }

    // Even at the bottom of the ranking, a Cpu device must beat the int.MinValue seed the selection loop
    // starts from, so software rendering is still chosen when it is the only adapter that can present.
    [Fact]
    public void SoftwareRasterizer_StaysSelectableAsLastResort()
    {
        Assert.True(VulkanVideoPresenter.ScoreDeviceType(PhysicalDeviceType.Cpu) > int.MinValue);
    }
}
