// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanFormatConversionTests
{
    [Theory]
    [InlineData(Format.R8G8B8A8Unorm, Format.A2R10G10B10UnormPack32, true)]
    [InlineData(Format.R8G8B8A8Unorm, Format.A2B10G10R10UnormPack32, true)]
    [InlineData(Format.A2R10G10B10UnormPack32, Format.R8G8B8A8Unorm, true)]
    [InlineData(Format.A2B10G10R10UnormPack32, Format.R8G8B8A8Unorm, true)]
    public void RequiresRealFormatConversion_FlagsTheBitIncompatiblePair(
        Format from,
        Format to,
        bool expected)
    {
        Assert.Equal(expected, VulkanVideoPresenter.RequiresRealFormatConversion(from, to));
    }

    [Theory]
    [InlineData(Format.R8G8B8A8Unorm, Format.B8G8R8A8Unorm)]
    [InlineData(Format.R8G8B8A8Unorm, Format.R8G8B8A8Srgb)]
    [InlineData(Format.R8G8B8A8Unorm, Format.R8G8B8A8Unorm)]
    [InlineData(Format.A2R10G10B10UnormPack32, Format.A2B10G10R10UnormPack32)]
    [InlineData(Format.R16G16B16A16Sfloat, Format.R32G32Sfloat)]
    public void RequiresRealFormatConversion_LeavesEveryOtherPairAlone(Format from, Format to)
    {
        Assert.False(VulkanVideoPresenter.RequiresRealFormatConversion(from, to));
    }

    [Fact]
    public void BitCastOfOpaqueBlackRgba8AsA2r10g10b10_ProducesTheObservedRed()
    {
        const uint opaqueBlackRgba8 = 0xFF000000u; // bytes 00 00 00 FF, little-endian

        var alpha2Bit = (opaqueBlackRgba8 >> 30) & 0x3u;
        var red10Bit = (opaqueBlackRgba8 >> 20) & 0x3FFu;
        var green10Bit = (opaqueBlackRgba8 >> 10) & 0x3FFu;
        var blue10Bit = opaqueBlackRgba8 & 0x3FFu;

        Assert.Equal(3u, alpha2Bit);
        Assert.Equal(1008u, red10Bit);
        Assert.Equal(0u, green10Bit);
        Assert.Equal(0u, blue10Bit);

        var redAsFloat = red10Bit / 1023.0;
        Assert.True(
            Math.Abs(redAsFloat - 0.9853372434443793) < 0.0001,
            $"expected ~0.9853 (matches the red observed live), got {redAsFloat}");
    }
}
