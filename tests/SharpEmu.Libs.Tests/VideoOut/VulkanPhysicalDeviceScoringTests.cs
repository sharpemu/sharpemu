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

    private static PhysicalDeviceProperties Device(PhysicalDeviceType type, uint vendorId = 0)
        => new() { DeviceType = type, VendorID = vendorId };

    private static int Score(PhysicalDeviceType type, uint vendorId = 0)
        => VulkanVideoPresenter.ScorePhysicalDevice(Device(type, vendorId), name: string.Empty, deviceOverride: null);

    [Fact]
    public void RealIntegratedGpuOutranksSoftwareRasterizer()
    {
        Assert.True(Score(PhysicalDeviceType.IntegratedGpu) > Score(PhysicalDeviceType.Cpu));
    }

    [Theory]
    [InlineData(IntelVendorId)]
    [InlineData(QualcommVendorId)]
    [InlineData(0u)]
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
    public void DeviceOverridePinsMatchingAdapterAndExcludesOthers()
    {
        var properties = Device(PhysicalDeviceType.Cpu);
        Assert.Equal(1000, VulkanVideoPresenter.ScorePhysicalDevice(properties, "Radeon Graphics", "radeon"));
        Assert.Equal(-1000, VulkanVideoPresenter.ScorePhysicalDevice(properties, "Radeon Graphics", "nvidia"));
    }

    [Fact]
    public void AmdIntegratedGpuIsLastResortOnWindows()
    {
        var penalty = VulkanVideoPresenter.ComputeDevicePenalty(
            Device(PhysicalDeviceType.IntegratedGpu, AmdVendorId), isWindows: true);
        Assert.True(penalty > 0);
        Assert.True(50 - penalty < 10);
    }

    [Fact]
    public void AmdApuIsNotPenalizedOffWindows()
    {
        var penalty = VulkanVideoPresenter.ComputeDevicePenalty(
            Device(PhysicalDeviceType.IntegratedGpu, AmdVendorId), isWindows: false);
        Assert.Equal(0, penalty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmdDiscreteGpuIsNeverPenalized(bool isWindows)
    {
        Assert.Equal(0, VulkanVideoPresenter.ComputeDevicePenalty(
            Device(PhysicalDeviceType.DiscreteGpu, AmdVendorId), isWindows));
    }

    [Theory]
    [InlineData(IntelVendorId)]
    [InlineData(QualcommVendorId)]
    public void NonAmdIntegratedGpuIsNeverPenalized(uint vendorId)
    {
        Assert.Equal(0, VulkanVideoPresenter.ComputeDevicePenalty(
            Device(PhysicalDeviceType.IntegratedGpu, vendorId), isWindows: true));
    }
}
