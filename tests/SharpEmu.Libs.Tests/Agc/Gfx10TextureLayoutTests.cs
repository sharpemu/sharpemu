// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gfx10TextureLayoutTests
{
    [Fact]
    public void Rgba8Array_HasExact64KbStandardSwizzleLayout()
    {
        var layout = Gfx10TextureLayout.Create(
            width: 32,
            height: 32,
            arrayLayers: 64,
            bytesPerElement: 4);

        Assert.Equal(128u, layout.BlockWidth);
        Assert.Equal(128u, layout.BlockHeight);
        Assert.Equal(128u, layout.PaddedPitch);
        Assert.Equal(128u, layout.PaddedHeight);
        Assert.Equal(0x1_0000UL, layout.SliceSizeBytes);
        Assert.Equal(0x40_0000UL, layout.GuestSpanBytes);
        Assert.Equal(0x4_0000UL, layout.TightSizeBytes);
        Assert.Equal(0x000UL, layout.GetGuestByteOffset(0, 0, 0));
        Assert.Equal(0x004UL, layout.GetGuestByteOffset(1, 0, 0));
        Assert.Equal(0x080UL, layout.GetGuestByteOffset(4, 0, 0));
        Assert.Equal(0x010UL, layout.GetGuestByteOffset(0, 1, 0));
        Assert.Equal(0x300UL, layout.GetGuestByteOffset(8, 8, 0));
        Assert.Equal(0xFFCUL, layout.GetGuestByteOffset(31, 31, 0));
        Assert.Equal(0x3F_0000UL, layout.GetGuestByteOffset(0, 0, 63));
    }

    [Fact]
    public void Rgba16FloatArray_HasExact64KbStandardSwizzleLayout()
    {
        var layout = Gfx10TextureLayout.Create(
            width: 30,
            height: 17,
            arrayLayers: 32,
            bytesPerElement: 8);

        Assert.Equal(128u, layout.BlockWidth);
        Assert.Equal(64u, layout.BlockHeight);
        Assert.Equal(128u, layout.PaddedPitch);
        Assert.Equal(64u, layout.PaddedHeight);
        Assert.Equal(0x1_0000UL, layout.SliceSizeBytes);
        Assert.Equal(0x20_0000UL, layout.GuestSpanBytes);
        Assert.Equal(130_560UL, layout.TightSizeBytes);
        Assert.Equal(0x000UL, layout.GetGuestByteOffset(0, 0, 0));
        Assert.Equal(0x008UL, layout.GetGuestByteOffset(1, 0, 0));
        Assert.Equal(0x040UL, layout.GetGuestByteOffset(2, 0, 0));
        Assert.Equal(0x010UL, layout.GetGuestByteOffset(0, 1, 0));
        Assert.Equal(0x600UL, layout.GetGuestByteOffset(8, 8, 0));
        Assert.Equal(0x1A88UL, layout.GetGuestByteOffset(29, 16, 0));
        Assert.Equal(0x1F_0000UL, layout.GetGuestByteOffset(0, 0, 31));
    }

    [Theory]
    [InlineData(1u, 256u, 256u, 0x001UL, 0x088UL, 0xFFFFUL)]
    [InlineData(2u, 256u, 128u, 0x002UL, 0x180UL, 0xFFFEUL)]
    [InlineData(16u, 64u, 64u, 0x040UL, 0xC00UL, 0xFFF0UL)]
    public void OtherElementSizes_HaveExactBlockGeometryAndOffsets(
        uint bytesPerElement,
        uint expectedBlockWidth,
        uint expectedBlockHeight,
        ulong expectedNextPixelOffset,
        ulong expectedEightByEightOffset,
        ulong expectedLastElementOffset)
    {
        var layout = Gfx10TextureLayout.Create(
            width: expectedBlockWidth,
            height: expectedBlockHeight,
            arrayLayers: 1,
            bytesPerElement);

        Assert.Equal(expectedBlockWidth, layout.BlockWidth);
        Assert.Equal(expectedBlockHeight, layout.BlockHeight);
        Assert.Equal(
            expectedNextPixelOffset,
            layout.GetGuestByteOffset(1, 0, 0));
        Assert.Equal(
            expectedEightByEightOffset,
            layout.GetGuestByteOffset(8, 8, 0));
        Assert.Equal(
            expectedLastElementOffset,
            layout.GetGuestByteOffset(
                expectedBlockWidth - 1,
                expectedBlockHeight - 1,
                0));
    }

    [Fact]
    public void MultiBlockArray_UsesRowMajorBlocksAndPaddedSliceStride()
    {
        var layout = Gfx10TextureLayout.Create(
            width: 129,
            height: 129,
            arrayLayers: 2,
            bytesPerElement: 4);

        Assert.Equal(256u, layout.PaddedPitch);
        Assert.Equal(256u, layout.PaddedHeight);
        Assert.Equal(0x4_0000UL, layout.SliceSizeBytes);
        Assert.Equal(0x1_0000UL, layout.GetGuestByteOffset(128, 0, 0));
        Assert.Equal(0x2_0000UL, layout.GetGuestByteOffset(0, 128, 0));
        Assert.Equal(0x4_0000UL, layout.GetGuestByteOffset(0, 0, 1));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(8u)]
    [InlineData(16u)]
    public void Detile_FullMultiBlockArray_ProducesTightLayerMajorBytes(
        uint bytesPerElement)
    {
        var unitLayout = Gfx10TextureLayout.Create(
            width: 1,
            height: 1,
            arrayLayers: 1,
            bytesPerElement);
        var layout = Gfx10TextureLayout.Create(
            width: unitLayout.BlockWidth + 3,
            height: unitLayout.BlockHeight + 2,
            arrayLayers: 2,
            bytesPerElement);
        var guestBytes = new byte[checked((int)layout.GuestSpanBytes)];
        Array.Fill(guestBytes, (byte)0xE7);
        var expected = new byte[checked((int)layout.TightSizeBytes)];
        var expectedOffset = 0;

        for (uint layer = 0; layer < layout.ArrayLayers; layer++)
        {
            for (uint y = 0; y < layout.Height; y++)
            {
                for (uint x = 0; x < layout.Width; x++)
                {
                    var guestOffset = checked(
                        (int)layout.GetGuestByteOffset(x, y, layer));
                    for (uint component = 0;
                        component < bytesPerElement;
                        component++)
                    {
                        var value = checked((byte)(
                            ((layer * 29) +
                            (y * 11) +
                            (x * 7) +
                            (component * 13)) & 0x7F));
                        guestBytes[checked(guestOffset + (int)component)] = value;
                        expected[expectedOffset++] = value;
                    }
                }
            }
        }

        Assert.Equal(expected, layout.Detile(guestBytes));
    }

    [Fact]
    public void Detile_DropsPitchHeightAndSlicePadding()
    {
        var layout = Gfx10TextureLayout.Create(
            width: 3,
            height: 2,
            arrayLayers: 2,
            bytesPerElement: 4);
        var guestBytes = new byte[checked((int)layout.GuestSpanBytes)];
        Array.Fill(guestBytes, (byte)0xFF);
        var expected = new byte[checked((int)layout.TightSizeBytes)];

        for (uint layer = 0; layer < layout.ArrayLayers; layer++)
        {
            for (uint y = 0; y < layout.Height; y++)
            {
                for (uint x = 0; x < layout.Width; x++)
                {
                    var value = checked((byte)(1 + layer * 32 + y * 8 + x));
                    var guestOffset = checked(
                        (int)layout.GetGuestByteOffset(x, y, layer));
                    guestBytes.AsSpan(guestOffset, 4).Fill(value);
                    var tightOffset = checked(
                        (int)((((ulong)layer * layout.Height + y) *
                        layout.Width + x) * 4));
                    expected.AsSpan(tightOffset, 4).Fill(value);
                }
            }
        }

        Assert.Equal(expected, layout.Detile(guestBytes));
        Assert.DoesNotContain((byte)0xFF, expected);
    }

    [Fact]
    public void Create_RejectsZeroDimensionsAndLayers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10TextureLayout.Create(0, 1, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10TextureLayout.Create(1, 0, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10TextureLayout.Create(1, 1, 0, 4));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(6u)]
    [InlineData(32u)]
    public void Create_RejectsUnsupportedElementSizes(uint bytesPerElement)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10TextureLayout.Create(1, 1, 1, bytesPerElement));
    }

    [Fact]
    public void Create_RejectsPaddedExtentOverflow()
    {
        Assert.Throws<OverflowException>(
            () => Gfx10TextureLayout.Create(uint.MaxValue, 1, 1, 1));
        Assert.Throws<OverflowException>(
            () => Gfx10TextureLayout.Create(1, uint.MaxValue, 1, 1));
    }

    [Fact]
    public void Create_RejectsSliceAndGuestSpanOverflow()
    {
        Assert.Throws<OverflowException>(
            () => Gfx10TextureLayout.Create(
                uint.MaxValue - 63,
                uint.MaxValue - 63,
                1,
                16));
        Assert.Throws<OverflowException>(
            () => Gfx10TextureLayout.Create(
                uint.MaxValue - 255,
                uint.MaxValue - 255,
                2,
                1));
    }

    [Fact]
    public void Detile_RejectsShortGuestSpan()
    {
        var layout = Gfx10TextureLayout.Create(1, 1, 2, 4);
        var guestBytes = new byte[checked((int)layout.GuestSpanBytes - 1)];

        Assert.Throws<ArgumentException>(() => layout.Detile(guestBytes));
    }

    [Fact]
    public void GuestOffset_RejectsCoordinatesOutsideTheResource()
    {
        var layout = Gfx10TextureLayout.Create(3, 2, 2, 4);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GetGuestByteOffset(3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GetGuestByteOffset(0, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GetGuestByteOffset(0, 0, 2));
    }

    [Fact]
    public void Rgba16FloatVolume_HasExact64KbStandard3DSwizzleLayout()
    {
        var layout = Gfx10Texture3DLayout.Create(
            width: 15,
            height: 8,
            depth: 32,
            bytesPerElement: 8);

        Assert.Equal(32u, layout.BlockWidth);
        Assert.Equal(16u, layout.BlockHeight);
        Assert.Equal(16u, layout.BlockDepth);
        Assert.Equal(32u, layout.PaddedPitch);
        Assert.Equal(16u, layout.PaddedHeight);
        Assert.Equal(32u, layout.PaddedDepth);
        Assert.Equal(0x2_0000UL, layout.GuestSpanBytes);
        Assert.Equal(30_720UL, layout.TightSizeBytes);
        Assert.Equal(0x0000UL, layout.GetGuestByteOffset(0, 0, 0));
        Assert.Equal(0x0008UL, layout.GetGuestByteOffset(1, 0, 0));
        Assert.Equal(0x0010UL, layout.GetGuestByteOffset(0, 0, 1));
        Assert.Equal(0x0020UL, layout.GetGuestByteOffset(0, 1, 0));
        Assert.Equal(0x1_0000UL, layout.GetGuestByteOffset(0, 0, 16));
    }

    [Theory]
    [InlineData(1u, 64u, 32u, 32u)]
    [InlineData(2u, 32u, 32u, 32u)]
    [InlineData(4u, 32u, 32u, 16u)]
    [InlineData(8u, 32u, 16u, 16u)]
    [InlineData(16u, 16u, 16u, 16u)]
    public void VolumeElementSizes_HaveExactBlockGeometry(
        uint bytesPerElement,
        uint expectedWidth,
        uint expectedHeight,
        uint expectedDepth)
    {
        var layout = Gfx10Texture3DLayout.Create(
            expectedWidth,
            expectedHeight,
            expectedDepth,
            bytesPerElement);

        Assert.Equal(expectedWidth, layout.BlockWidth);
        Assert.Equal(expectedHeight, layout.BlockHeight);
        Assert.Equal(expectedDepth, layout.BlockDepth);
        Assert.Equal(
            Gfx10TextureLayout.BlockSizeBytes,
            layout.GuestSpanBytes);
    }

    [Fact]
    public void Detile3D_ProducesTightDepthRowMajorBytesAcrossBlocks()
    {
        var layout = Gfx10Texture3DLayout.Create(
            width: 33,
            height: 17,
            depth: 17,
            bytesPerElement: 8);
        var guestBytes = new byte[checked((int)layout.GuestSpanBytes)];
        Array.Fill(guestBytes, (byte)0xE7);
        var expected = new byte[checked((int)layout.TightSizeBytes)];
        var expectedOffset = 0;

        for (uint z = 0; z < layout.Depth; z++)
        {
            for (uint y = 0; y < layout.Height; y++)
            {
                for (uint x = 0; x < layout.Width; x++)
                {
                    var guestOffset = checked(
                        (int)layout.GetGuestByteOffset(x, y, z));
                    for (uint component = 0;
                        component < layout.BytesPerElement;
                        component++)
                    {
                        var value = checked((byte)(
                            ((z * 31) + (y * 13) + (x * 7) + component) &
                            0x7F));
                        guestBytes[checked(guestOffset + (int)component)] = value;
                        expected[expectedOffset++] = value;
                    }
                }
            }
        }

        Assert.Equal(expected, layout.Detile(guestBytes));
        Assert.DoesNotContain((byte)0xE7, expected);
    }
}
