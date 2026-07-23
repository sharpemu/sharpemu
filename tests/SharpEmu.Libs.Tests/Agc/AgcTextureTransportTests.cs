// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcTextureTransportTests
{
    [Theory]
    [InlineData(10u, 4u, 4u)]
    [InlineData(10u, 0u, 1u)]
    [InlineData(9u, 4u, 1u)]
    [InlineData(13u, 4u, 1u)]
    public void GetTextureVolumeDepth_OnlyUsesDescriptorDepthFor3D(
        uint type,
        uint descriptorDepth,
        uint expectedDepth)
    {
        Assert.Equal(
            expectedDepth,
            AgcExports.GetTextureVolumeDepth(type, descriptorDepth));
    }

    [Fact]
    public void GetTextureByteCount_MultipliesUncompressedVolumeDepth()
    {
        Assert.Equal(
            4UL * 8 * 6 * 5,
            AgcExports.GetTextureByteCount(
                format: 10,
                width: 8,
                height: 6,
                depth: 5));
    }

    [Fact]
    public void GetTextureByteCount_MultipliesBlockCompressedVolumeDepth()
    {
        // Format 169 uses one eight-byte BC block for each 4x4 texel block.
        Assert.Equal(
            2UL * 2 * 8 * 3,
            AgcExports.GetTextureByteCount(
                format: 169,
                width: 7,
                height: 5,
                depth: 3));
    }

    [Fact]
    public void GetTextureByteCount_LeavesTwoDimensionalSizingUnchanged()
    {
        Assert.Equal(
            AgcExports.GetTextureByteCount(10, 8, 6),
            AgcExports.GetTextureByteCount(10, 8, 6, depth: 1));
        Assert.Equal(
            AgcExports.GetTextureByteCount(10, 8, 6),
            AgcExports.GetTextureByteCount(10, 8, 6, depth: 0));
    }

    [Fact]
    public void GuestDrawTexture_CarriesRawTypeAndNormalizedDepth()
    {
        var texture = new GuestDrawTexture(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            Type: 10,
            Depth: 5);

        Assert.Equal(10u, texture.Type);
        Assert.Equal(5u, texture.Depth);
    }

    [Fact]
    public void TextureContentIdentity_DistinguishesTypeAndDepth()
    {
        var twoDimensional = CreateIdentity(type: 9, depth: 1);
        var threeDimensional = CreateIdentity(type: 10, depth: 1);
        var deeperThreeDimensional = CreateIdentity(type: 10, depth: 5);

        Assert.NotEqual(twoDimensional, threeDimensional);
        Assert.NotEqual(threeDimensional, deeperThreeDimensional);
    }

    private static TextureContentIdentity CreateIdentity(uint type, uint depth) =>
        new(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            DstSelect: 0xFAC,
            TileMode: 0,
            Pitch: 8,
            Sampler: default,
            Type: type,
            Depth: depth);
}
