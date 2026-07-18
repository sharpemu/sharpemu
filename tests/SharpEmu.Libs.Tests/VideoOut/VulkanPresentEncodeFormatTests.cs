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
}
