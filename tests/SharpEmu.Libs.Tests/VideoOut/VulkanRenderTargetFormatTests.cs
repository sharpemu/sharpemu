// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRenderTargetFormatTests
{
    [Theory]
    [InlineData(2u, 7u, Format.R16Sfloat)]
    [InlineData(6u, 7u, Format.B10G11R11UfloatPack32)]
    [InlineData(9u, 0u, Format.A2B10G10R10UnormPack32)]
    public void FloatColorTarget_DecodesAndRoundTripsThroughGuestIdentity(
        uint dataFormat,
        uint numberType,
        Format expectedFormat)
    {
        Assert.True(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat,
                numberType,
                out var decoded));
        Assert.Equal(expectedFormat, decoded.Format);
        Assert.Equal(Gen5PixelOutputKind.Float, decoded.OutputKind);

        var guestFormat = VulkanVideoPresenter.GetGuestTextureFormat(dataFormat, numberType);
        Assert.NotEqual(0u, guestFormat);
        Assert.True(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                guestFormat,
                0,
                out var roundTripped));
        Assert.Equal(expectedFormat, roundTripped.Format);
    }

    [Theory]
    [InlineData(2u, 0u)]
    [InlineData(6u, 0u)]
    public void FloatOnlyColorTarget_RejectsUnormNumberType(
        uint dataFormat,
        uint numberType)
    {
        Assert.False(
            VulkanVideoPresenter.TryDecodeRenderTargetFormat(
                dataFormat,
                numberType,
                out _));
    }
}
