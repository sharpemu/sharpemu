// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Agc;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPackedGuestImageClearTests
{
    [Fact]
    public void Rgba8Unorm_DecodesNativeChannelOrder()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClear(
            Format.R8G8B8A8Unorm,
            0xFF008080u,
            out var clear);

        Assert.True(ok);
        Assert.Equal(128f / 255f, clear.Float32_0, 6);
        Assert.Equal(128f / 255f, clear.Float32_1, 6);
        Assert.Equal(0f, clear.Float32_2);
        Assert.Equal(1f, clear.Float32_3);
    }

    [Fact]
    public void Bgra8Unorm_DecodesNativeChannelOrder()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClear(
            Format.B8G8R8A8Unorm,
            0xFF0000FFu,
            out var clear);

        Assert.True(ok);
        Assert.Equal(0f, clear.Float32_0);
        Assert.Equal(0f, clear.Float32_1);
        Assert.Equal(1f, clear.Float32_2);
        Assert.Equal(1f, clear.Float32_3);
    }

    [Fact]
    public void Rgba8Srgb_DecodesColorChannelsToLinear()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClear(
            Format.R8G8B8A8Srgb,
            0xFF808080u,
            out var clear);

        Assert.True(ok);
        Assert.Equal(0.215861f, clear.Float32_0, 5);
        Assert.Equal(0.215861f, clear.Float32_1, 5);
        Assert.Equal(0.215861f, clear.Float32_2, 5);
        Assert.Equal(1f, clear.Float32_3);
    }

    [Fact]
    public void R32Uint_PreservesPackedBits()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClear(
            Format.R32Uint,
            0xDEADBEEFu,
            out var clear);

        Assert.True(ok);
        Assert.Equal(0xDEADBEEFu, clear.Uint32_0);
    }

    [Fact]
    public void Rgba16Float_DecodesRepeatedHalfPair()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClear(
            Format.R16G16B16A16Sfloat,
            0xBC003C00u,
            out var clear);

        Assert.True(ok);
        Assert.Equal(1f, clear.Float32_0);
        Assert.Equal(-1f, clear.Float32_1);
        Assert.Equal(1f, clear.Float32_2);
        Assert.Equal(-1f, clear.Float32_3);
    }

    [Fact]
    public void Rgba16Float_DecodesRepeatedTwoDwordTexel()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClearPattern(
            Format.R16G16B16A16Sfloat,
            0x00000000u,
            0x3C000000u,
            0x00000000u,
            0x3C000000u,
            out var clear);

        Assert.True(ok);
        Assert.Equal(0f, clear.Float32_0);
        Assert.Equal(0f, clear.Float32_1);
        Assert.Equal(0f, clear.Float32_2);
        Assert.Equal(1f, clear.Float32_3);
    }

    [Fact]
    public void Rgba16Float_RejectsNonRepeatingTwoDwordTexels()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClearPattern(
            Format.R16G16B16A16Sfloat,
            0x00000000u,
            0x3C000000u,
            0x00000001u,
            0x3C000000u,
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void Rg16Float_DecodesPackedHalfPair()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClear(
            Format.R16G16Sfloat,
            0xBC003C00u,
            out var clear);

        Assert.True(ok);
        Assert.Equal(1f, clear.Float32_0);
        Assert.Equal(-1f, clear.Float32_1);
    }

    [Fact]
    public void Rgba32Float_DecodesRepeatedScalar()
    {
        var ok = VulkanVideoPresenter.TryDecodePackedGuestImageClear(
            Format.R32G32B32A32Sfloat,
            0x3F800000u,
            out var clear);

        Assert.True(ok);
        Assert.Equal(1f, clear.Float32_0);
        Assert.Equal(1f, clear.Float32_1);
        Assert.Equal(1f, clear.Float32_2);
        Assert.Equal(1f, clear.Float32_3);
    }

    [Fact]
    public void UnsupportedClearFormat_CreatesExactRepeatedUpload()
    {
        var ok = VulkanVideoPresenter.TryCreatePackedGuestImageUpload(
            Format.R16G16B16A16Uint,
            width: 2,
            height: 1,
            depth: 1,
            packed: 0x12345678u,
            out var pixels);

        Assert.True(ok);
        Assert.Equal(16, pixels.Length);
        Assert.Equal(
            new byte[]
            {
                0x78, 0x56, 0x34, 0x12,
                0x78, 0x56, 0x34, 0x12,
                0x78, 0x56, 0x34, 0x12,
                0x78, 0x56, 0x34, 0x12,
            },
            pixels);
    }

    [Fact]
    public void DccRegisterClear_DecodesClearWordAfterRecognizedFill()
    {
        var ok = VulkanVideoPresenter.TryDecodeDccMetadataClear(
            Format.R8G8B8A8Unorm,
            new AgcExports.GuestColorMetadataClear(
                FillCode: 0x20,
                ClearWord0: 0xFF008080u,
                ClearWord1: 0),
            out var clear);

        Assert.True(ok);
        Assert.Equal(128f / 255f, clear.Float32_0, 6);
        Assert.Equal(128f / 255f, clear.Float32_1, 6);
        Assert.Equal(0f, clear.Float32_2);
        Assert.Equal(1f, clear.Float32_3);
    }

    [Theory]
    [InlineData(0x00, 0f, 0f, 0f, 0f)]
    [InlineData(0x40, 0f, 0f, 0f, 1f)]
    [InlineData(0x80, 1f, 1f, 1f, 0f)]
    [InlineData(0xC0, 1f, 1f, 1f, 1f)]
    public void DccFixedClear_DecodesRecognizedCodes(
        byte fillCode,
        float red,
        float green,
        float blue,
        float alpha)
    {
        var ok = VulkanVideoPresenter.TryDecodeDccMetadataClear(
            Format.B8G8R8A8Srgb,
            new AgcExports.GuestColorMetadataClear(fillCode, 0, 0),
            out var clear);

        Assert.True(ok);
        Assert.Equal(red, clear.Float32_0);
        Assert.Equal(green, clear.Float32_1);
        Assert.Equal(blue, clear.Float32_2);
        Assert.Equal(alpha, clear.Float32_3);
    }

    [Fact]
    public void DccFixedClear_RejectsUnsupportedTargetFormat()
    {
        var ok = VulkanVideoPresenter.TryDecodeDccMetadataClear(
            Format.R16G16B16A16Sfloat,
            new AgcExports.GuestColorMetadataClear(0xC0, 0, 0),
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void DccClear_RejectsUnknownFillCode()
    {
        var ok = VulkanVideoPresenter.TryDecodeDccMetadataClear(
            Format.R8G8B8A8Unorm,
            new AgcExports.GuestColorMetadataClear(0x60, 0, 0),
            out _);

        Assert.False(ok);
    }
}
