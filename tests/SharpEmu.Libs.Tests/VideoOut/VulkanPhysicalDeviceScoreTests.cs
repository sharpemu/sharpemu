// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPhysicalDeviceScoreTests
{
    private const uint NvidiaVendorId = 0x10DE;
    private const uint AmdVendorId = 0x1002;

    private static int Score(PhysicalDeviceType type, uint vendorId = AmdVendorId) =>
        VulkanVideoPresenter.ScorePhysicalDevice(
            new PhysicalDeviceProperties { DeviceType = type, VendorID = vendorId },
            "test device",
            deviceOverride: null);

    [Theory]
    [InlineData(PhysicalDeviceType.DiscreteGpu)]
    [InlineData(PhysicalDeviceType.VirtualGpu)]
    [InlineData(PhysicalDeviceType.IntegratedGpu)]
    [InlineData(PhysicalDeviceType.Other)]
    public void EveryRealDeviceOutranksASoftwareRasterizer(PhysicalDeviceType type)
    {
        // A Cpu-type device is lavapipe/SwiftShader. Losing to it means the guest
        // renders in software while a real GPU sits idle -- the #325 regression.
        Assert.True(Score(type) > Score(PhysicalDeviceType.Cpu));
    }

    [Fact]
    public void DiscreteStillOutranksIntegrated()
    {
        // #97: the integrated AMD driver crashes compiling translated guest shaders.
        Assert.True(Score(PhysicalDeviceType.DiscreteGpu) > Score(PhysicalDeviceType.IntegratedGpu));
    }

    [Fact]
    public void NvidiaOutranksAnyOtherDiscreteGpu()
    {
        Assert.True(
            Score(PhysicalDeviceType.DiscreteGpu, NvidiaVendorId) >
            Score(PhysicalDeviceType.DiscreteGpu, AmdVendorId));
    }

    [Fact]
    public void AppleSiliconGpuBeatsSoftwareEvenThoughMoltenVkReportsItIntegrated()
    {
        // MoltenVK tags every Apple Silicon GPU as integrated; on those hosts the
        // integrated score is the only thing standing between the guest and lavapipe.
        Assert.True(Score(PhysicalDeviceType.IntegratedGpu) > Score(PhysicalDeviceType.Cpu));
    }

    [Fact]
    public void DeviceOverrideSelectsMatchingNameAndRejectsOthers()
    {
        var properties = new PhysicalDeviceProperties
        {
            DeviceType = PhysicalDeviceType.Cpu,
            VendorID = AmdVendorId,
        };

        // An explicit pin wins over the type ranking, even for a software device.
        var pinned = VulkanVideoPresenter.ScorePhysicalDevice(properties, "llvmpipe (LLVM 17)", "llvmpipe");
        var rejected = VulkanVideoPresenter.ScorePhysicalDevice(properties, "llvmpipe (LLVM 17)", "RTX 4090");

        Assert.True(pinned > Score(PhysicalDeviceType.DiscreteGpu, NvidiaVendorId));
        Assert.True(rejected < Score(PhysicalDeviceType.Cpu));
    }
}
