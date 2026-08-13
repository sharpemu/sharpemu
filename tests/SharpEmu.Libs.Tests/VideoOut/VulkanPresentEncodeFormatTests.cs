// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPresentEncodeFormatTests
{
    [Theory]
    [InlineData(Format.B8G8R8A8Unorm, Format.B8G8R8A8Srgb)]
    [InlineData(Format.R8G8B8A8Unorm, Format.R8G8B8A8Srgb)]
    public void UnormSwapchainFormatsHaveSrgbCounterparts(
        Format swapchainFormat,
        Format expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GetSrgbCounterpart(swapchainFormat));
    }

    [Theory]
    // Already-sRGB swapchains encode on store; the direct blit stays.
    [InlineData(Format.B8G8R8A8Srgb)]
    [InlineData(Format.R8G8B8A8Srgb)]
    // No same-class sRGB counterpart exists; the raw blit must remain.
    [InlineData(Format.A2B10G10R10UnormPack32)]
    [InlineData(Format.R16G16B16A16Sfloat)]
    [InlineData(Format.R5G6B5UnormPack16)]
    [InlineData(Format.Undefined)]
    public void OtherSwapchainFormatsKeepTheDirectBlit(Format swapchainFormat)
    {
        Assert.Equal(
            Format.Undefined,
            VulkanVideoPresenter.GetSrgbCounterpart(swapchainFormat));
    }

    [Theory]
    [InlineData(Format.R16G16B16A16Sfloat)]
    [InlineData(Format.R32G32B32A32Sfloat)]
    public void FloatFlipSourcesNeedLinearToSrgbEncode(Format sourceFormat)
    {
        Assert.True(VulkanVideoPresenter.IsLinearFloatPresentSource(sourceFormat));
    }

    [Theory]
    [InlineData(Format.B8G8R8A8Unorm)]
    [InlineData(Format.R8G8B8A8Unorm)]
    [InlineData(Format.B8G8R8A8Srgb)]
    [InlineData(Format.A2B10G10R10UnormPack32)]
    [InlineData(Format.B10G11R11UfloatPack32)]
    [InlineData(Format.Undefined)]
    public void NonFloatFlipSourcesKeepTheDirectBlit(Format sourceFormat)
    {
        Assert.False(VulkanVideoPresenter.IsLinearFloatPresentSource(sourceFormat));
    }

    // GTA V Enhanced early G-buffer color targets observed as COMPAT failures
    // when missing from TryDecodeRenderTargetFormat (format=2 / format=11).
    [Theory]
    [InlineData(2u, 0u, Format.R16Unorm)]
    [InlineData(2u, 4u, Format.R16Uint)]
    [InlineData(2u, 7u, Format.R16Sfloat)]
    [InlineData(11u, 4u, Format.R32G32Uint)]
    [InlineData(11u, 5u, Format.R32G32Sint)]
    [InlineData(11u, 7u, Format.R32G32Sfloat)]
    public void TryDecodeRenderTargetFormat_DecodesGen5R16AndRg32(
        uint dataFormat,
        uint numberType,
        Format expected)
    {
        Assert.True(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat,
                numberType,
                out var decoded));
        Assert.Equal(expected, decoded.Format);
    }

    [Theory]
    [InlineData(0u, Format.R8G8B8A8Unorm, 0xE4)]
    [InlineData(1u, Format.B8G8R8A8Unorm, 0xE4)]
    [InlineData(2u, Format.R8G8B8A8Unorm, 0x1B)]
    [InlineData(3u, Format.R8G8B8A8Unorm, 0x93)]
    public void TryDecodeRenderTargetFormat_MapsRgba8ComponentSwap(
        uint componentSwap,
        Format expectedFormat,
        byte expectedMapping)
    {
        Assert.True(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat: 10,
                numberType: 0,
                componentSwap,
                out var decoded));

        Assert.Equal(expectedFormat, decoded.Format);
        Assert.Equal(expectedMapping, decoded.ExportMapping.Packed);
    }

    [Theory]
    [InlineData(6u, 0u, Format.R8G8B8A8Srgb)]
    [InlineData(6u, 1u, Format.B8G8R8A8Srgb)]
    // Keep the texture NUMBER_FORMAT value as a compatibility input.
    [InlineData(9u, 0u, Format.R8G8B8A8Srgb)]
    [InlineData(9u, 1u, Format.B8G8R8A8Srgb)]
    public void TryDecodeRenderTargetFormat_DecodesCbSrgbNumberType(
        uint numberType,
        uint componentSwap,
        Format expectedFormat)
    {
        Assert.True(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat: 10,
                numberType,
                componentSwap,
                out var decoded));

        Assert.Equal(expectedFormat, decoded.Format);
        Assert.True(decoded.ExportMapping.IsIdentity);
    }

    [Theory]
    [InlineData(0u, Format.A2B10G10R10UnormPack32)]
    [InlineData(1u, Format.A2R10G10B10UnormPack32)]
    public void TryDecodeRenderTargetFormat_SelectsRgb10HostOrder(
        uint componentSwap,
        Format expectedFormat)
    {
        Assert.True(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat: 9,
                numberType: 0,
                componentSwap,
                out var decoded));

        Assert.Equal(expectedFormat, decoded.Format);
        Assert.True(decoded.ExportMapping.IsIdentity);
    }

    [Fact]
    public void TryDecodeRenderTargetFormat_RejectsInvalidComponentSwap()
    {
        Assert.False(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat: 10,
                numberType: 0,
                componentSwap: 4,
                out _));
    }
}
