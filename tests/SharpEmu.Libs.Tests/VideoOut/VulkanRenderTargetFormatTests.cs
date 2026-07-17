// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRenderTargetFormatTests
{
    [Theory]
    [InlineData(0u, Format.A2B10G10R10UnormPack32)]
    [InlineData(1u, Format.A2R10G10B10UnormPack32)]
    public void Color2101010HonorsComponentSwap(uint componentSwap, Format expected)
    {
        Assert.True(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat: 9,
            numberType: 0,
            componentSwap,
            out var result));
        Assert.Equal(expected, result.Format);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    public void Color2101010RejectsUnsupportedComponentSwap(uint componentSwap)
    {
        Assert.False(VulkanVideoPresenter.TryDecodeRenderTargetFormat(
            dataFormat: 9,
            numberType: 0,
            componentSwap,
            out _));
    }
}
