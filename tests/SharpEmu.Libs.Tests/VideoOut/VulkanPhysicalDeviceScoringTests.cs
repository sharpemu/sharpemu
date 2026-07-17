// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPhysicalDeviceScoringTests
{
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AmdVendorId = 0x1002;
    private const uint IntelVendorId = 0x8086;
    private const uint QualcommVendorId = 0x5143;

    private static int Score(PhysicalDeviceType type, uint vendorId = 0)
        => VulkanVideoPresenter.ScorePhysicalDevice(
            new PhysicalDeviceProperties { DeviceType = type, VendorID = vendorId },
            name: string.Empty,
            deviceOverride: null);

    [Fact]
    public void RealIntegratedGpuOutranksSoftwareRasterizer()
    {
        // Regression for #325: a Cpu-type software rasterizer (Mesa lavapipe)
        // must not be preferred over a real integrated GPU.
        Assert.True(Score(PhysicalDeviceType.IntegratedGpu) > Score(PhysicalDeviceType.Cpu));
    }

    [Theory]
    [InlineData(IntelVendorId)]
    [InlineData(QualcommVendorId)]
    [InlineData(0)] // Apple/MoltenVK reports Apple Silicon as an integrated GPU.
    public void NonAmdIntegratedGpuBeatsSoftware(uint vendorId)
    {
        Assert.True(Score(PhysicalDeviceType.IntegratedGpu, vendorId) > Score(PhysicalDeviceType.Cpu));
    }

    [Fact]
    public void DiscreteGpuOutranksIntegratedGpu()
    {
        Assert.True(Score(PhysicalDeviceType.DiscreteGpu) > Score(PhysicalDeviceType.IntegratedGpu));
    }

    [Fact]
    public void NvidiaDiscreteGpuScoresHighest()
    {
        var nvidia = Score(PhysicalDeviceType.DiscreteGpu, NvidiaVendorId);
        Assert.True(nvidia > Score(PhysicalDeviceType.DiscreteGpu));
        Assert.True(nvidia > Score(PhysicalDeviceType.IntegratedGpu));
    }

    [Fact]
    public void AmdIntegratedGpuStaysLastResort()
    {
        // #97: AMD's integrated driver crashes compiling translated shaders, so
        // it must rank below every other candidate, including software.
        var amd = Score(PhysicalDeviceType.IntegratedGpu, AmdVendorId);
        Assert.True(amd < Score(PhysicalDeviceType.Cpu));
        Assert.True(amd < Score(PhysicalDeviceType.IntegratedGpu, IntelVendorId));
        Assert.True(amd < Score(PhysicalDeviceType.DiscreteGpu));
    }

    [Fact]
    public void AmdDiscreteGpuIsNotPenalized()
    {
        // The #97 penalty is specific to AMD integrated parts; an AMD discrete
        // card must keep the full discrete score.
        Assert.Equal(Score(PhysicalDeviceType.DiscreteGpu), Score(PhysicalDeviceType.DiscreteGpu, AmdVendorId));
    }

    [Fact]
    public void DeviceOverridePinsMatchingAdapterAndExcludesOthers()
    {
        var properties = new PhysicalDeviceProperties { DeviceType = PhysicalDeviceType.Cpu };
        Assert.Equal(1000, VulkanVideoPresenter.ScorePhysicalDevice(properties, "Radeon Graphics", "radeon"));
        Assert.Equal(-1000, VulkanVideoPresenter.ScorePhysicalDevice(properties, "Radeon Graphics", "nvidia"));
    }
}
