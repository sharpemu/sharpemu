// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDepthTextureAliasTests
{
    [Fact]
    public void ExactZ32DescriptorCanSampleDepthImage()
    {
        var texture = CreateTexture(format: 4, numberType: 7, tileMode: 24);

        Assert.True(
            VulkanVideoPresenter.IsCompatibleGuestDepthTextureDescriptor(
                texture,
                depthWidth: 1920,
                depthHeight: 1080,
                depthGuestFormat: 3));
    }

    [Fact]
    public void ColorDescriptorAtSameAddressCannotSampleDepthImage()
    {
        var texture = CreateTexture(format: 12, numberType: 7, tileMode: 27);

        Assert.False(
            VulkanVideoPresenter.IsCompatibleGuestDepthTextureDescriptor(
                texture,
                depthWidth: 1920,
                depthHeight: 1080,
                depthGuestFormat: 3));
    }

    [Theory]
    [InlineData(1919u, 1080u, 24u, 1920u, 9u, 1u, false)]
    [InlineData(1920u, 1079u, 24u, 1920u, 9u, 1u, false)]
    [InlineData(1920u, 1080u, 27u, 1920u, 9u, 1u, false)]
    [InlineData(1920u, 1080u, 24u, 1919u, 9u, 1u, false)]
    [InlineData(1920u, 1080u, 24u, 1920u, 10u, 1u, false)]
    [InlineData(1920u, 1080u, 24u, 1920u, 9u, 2u, true)]
    public void PhysicalIdentityMismatchCannotSampleDepthImage(
        uint width,
        uint height,
        uint tileMode,
        uint pitch,
        uint type,
        uint resourceMipLevels,
        bool arrayedView)
    {
        var texture = CreateTexture(
            format: 4,
            numberType: 7,
            tileMode,
            width,
            height,
            pitch,
            type,
            resourceMipLevels,
            arrayedView);

        Assert.False(
            VulkanVideoPresenter.IsCompatibleGuestDepthTextureDescriptor(
                texture,
                depthWidth: 1920,
                depthHeight: 1080,
                depthGuestFormat: 3));
    }

    [Fact]
    public void ExactZ16DescriptorCanSampleDepthImage()
    {
        var texture = CreateTexture(format: 2, numberType: 0, tileMode: 24);

        Assert.True(
            VulkanVideoPresenter.IsCompatibleGuestDepthTextureDescriptor(
                texture,
                depthWidth: 1920,
                depthHeight: 1080,
                depthGuestFormat: 1));
    }

    private static GuestDrawTexture CreateTexture(
        uint format,
        uint numberType,
        uint tileMode,
        uint width = 1920,
        uint height = 1080,
        uint pitch = 1920,
        uint type = 9,
        uint resourceMipLevels = 1,
        bool arrayedView = false) =>
        new(
            Address: 0x637110000,
            Width: width,
            Height: height,
            Format: format,
            NumberType: numberType,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            ResourceMipLevels: resourceMipLevels,
            Pitch: pitch,
            TileMode: tileMode,
            ArrayedView: arrayedView,
            ArrayLayers: arrayedView ? 2u : 1u,
            Type: type,
            Depth: 1);
}
