// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class MetalRenderTargetFormatTests
{
    [Theory]
    [InlineData(0u, 70u, 0xE4)]
    [InlineData(1u, 80u, 0xE4)]
    [InlineData(2u, 70u, 0x1B)]
    [InlineData(3u, 70u, 0x93)]
    public void Rgba8ComponentSwapSelectsFormatAndExportMapping(
        uint componentSwap,
        uint expectedFormat,
        byte expectedMapping)
    {
        Assert.True(
            MetalGuestFormats.TryDecodeRenderTargetFormat(
                dataFormat: 10,
                numberType: 0,
                componentSwap,
                out var decoded));

        Assert.Equal(expectedFormat, (uint)decoded.Format);
        Assert.Equal(expectedMapping, decoded.ExportMapping.Packed);
    }

    [Theory]
    [InlineData(6u)]
    [InlineData(9u)]
    public void Rgba8SrgbAcceptsGuestAndCompatibilityNumberTypes(uint numberType)
    {
        Assert.True(
            MetalGuestFormats.TryDecodeRenderTargetFormat(
                dataFormat: 10,
                numberType,
                componentSwap: 0,
                out var decoded));

        Assert.Equal(MtlPixelFormat.Rgba8UnormSrgb, decoded.Format);
    }

    [Fact]
    public void UnsupportedComponentSwapIsRejected()
    {
        Assert.False(
            MetalGuestFormats.TryDecodeRenderTargetFormat(
                dataFormat: 10,
                numberType: 0,
                componentSwap: 4,
                out _));
    }
}
