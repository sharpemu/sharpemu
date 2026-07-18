// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gfx10Texture3DLayoutTests
{
    [Fact]
    public void Rgba16FloatVolume_HasExact64KbStandardSwizzleLayout()
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
        Assert.Equal(0x7800UL, layout.TightSizeBytes);

        Assert.Equal(0x00008UL, layout.GetGuestByteOffset(1, 0, 0));
        Assert.Equal(0x00040UL, layout.GetGuestByteOffset(2, 0, 0));
        Assert.Equal(0x00200UL, layout.GetGuestByteOffset(4, 0, 0));
        Assert.Equal(0x01000UL, layout.GetGuestByteOffset(8, 0, 0));
        Assert.Equal(0x01240UL, layout.GetGuestByteOffset(14, 0, 0));

        Assert.Equal(0x00020UL, layout.GetGuestByteOffset(0, 1, 0));
        Assert.Equal(0x00100UL, layout.GetGuestByteOffset(0, 2, 0));
        Assert.Equal(0x00800UL, layout.GetGuestByteOffset(0, 4, 0));
        Assert.Equal(0x00920UL, layout.GetGuestByteOffset(0, 7, 0));

        Assert.Equal(0x00010UL, layout.GetGuestByteOffset(0, 0, 1));
        Assert.Equal(0x00080UL, layout.GetGuestByteOffset(0, 0, 2));
        Assert.Equal(0x00400UL, layout.GetGuestByteOffset(0, 0, 4));
        Assert.Equal(0x02000UL, layout.GetGuestByteOffset(0, 0, 8));
        Assert.Equal(0x02490UL, layout.GetGuestByteOffset(0, 0, 15));
        Assert.Equal(0x1_0000UL, layout.GetGuestByteOffset(0, 0, 16));
        Assert.Equal(0x1_2490UL, layout.GetGuestByteOffset(0, 0, 31));

        Assert.Equal(0x1_3FF0UL, layout.GetGuestByteOffset(14, 7, 31));
    }

    [Theory]
    [InlineData(1u, 64u, 32u, 32u, 0xFFFFUL)]
    [InlineData(2u, 32u, 32u, 32u, 0xFFFEUL)]
    [InlineData(4u, 32u, 32u, 16u, 0xFFFCUL)]
    [InlineData(8u, 32u, 16u, 16u, 0xFFF8UL)]
    [InlineData(16u, 16u, 16u, 16u, 0xFFF0UL)]
    public void ElementSizes_HaveExactThickBlockGeometry(
        uint bytesPerElement,
        uint expectedBlockWidth,
        uint expectedBlockHeight,
        uint expectedBlockDepth,
        ulong expectedLastElementOffset)
    {
        var layout = Gfx10Texture3DLayout.Create(
            expectedBlockWidth,
            expectedBlockHeight,
            expectedBlockDepth,
            bytesPerElement);

        Assert.Equal(expectedBlockWidth, layout.BlockWidth);
        Assert.Equal(expectedBlockHeight, layout.BlockHeight);
        Assert.Equal(expectedBlockDepth, layout.BlockDepth);
        Assert.Equal(Gfx10Texture3DLayout.BlockSizeBytes, layout.GuestSpanBytes);
        Assert.Equal(Gfx10Texture3DLayout.BlockSizeBytes, layout.TightSizeBytes);
        Assert.Equal(
            expectedLastElementOffset,
            layout.GetGuestByteOffset(
                expectedBlockWidth - 1,
                expectedBlockHeight - 1,
                expectedBlockDepth - 1));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(8u)]
    [InlineData(16u)]
    public void ElementSizes_HaveExactCoordinateBitMappings(
        uint bytesPerElement)
    {
        var layout = Gfx10Texture3DLayout.Create(
            width: 64,
            height: 32,
            depth: 32,
            bytesPerElement);
        var expectedX = bytesPerElement switch
        {
            1 => new ulong[] { 0x1, 0x2, 0x40, 0x200, 0x1000, 0x8000 },
            2 => new ulong[] { 0x2, 0x40, 0x200, 0x1000, 0x8000 },
            4 => new ulong[] { 0x4, 0x40, 0x200, 0x1000, 0x8000 },
            8 => new ulong[] { 0x8, 0x40, 0x200, 0x1000, 0x8000 },
            16 => new ulong[] { 0x40, 0x200, 0x1000, 0x8000 },
            _ => throw new InvalidOperationException(),
        };
        var expectedY = bytesPerElement switch
        {
            1 or 2 or 4 =>
                new ulong[] { 0x8, 0x20, 0x100, 0x800, 0x4000 },
            8 or 16 => new ulong[] { 0x20, 0x100, 0x800, 0x4000 },
            _ => throw new InvalidOperationException(),
        };
        var expectedZ = bytesPerElement switch
        {
            1 or 2 =>
                new ulong[] { 0x4, 0x10, 0x80, 0x400, 0x2000 },
            4 or 8 or 16 => new ulong[] { 0x10, 0x80, 0x400, 0x2000 },
            _ => throw new InvalidOperationException(),
        };

        for (var bit = 0; bit < expectedX.Length; bit++)
        {
            Assert.Equal(
                expectedX[bit],
                layout.GetGuestByteOffset(1u << bit, 0, 0));
        }

        for (var bit = 0; bit < expectedY.Length; bit++)
        {
            Assert.Equal(
                expectedY[bit],
                layout.GetGuestByteOffset(0, 1u << bit, 0));
        }

        for (var bit = 0; bit < expectedZ.Length; bit++)
        {
            Assert.Equal(
                expectedZ[bit],
                layout.GetGuestByteOffset(0, 0, 1u << bit));
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(8u)]
    [InlineData(16u)]
    public void MultiBlockVolume_UsesZThenYThenXBlockOrdering(
        uint bytesPerElement)
    {
        var unitLayout = Gfx10Texture3DLayout.Create(1, 1, 1, bytesPerElement);
        var layout = Gfx10Texture3DLayout.Create(
            unitLayout.BlockWidth + 1,
            unitLayout.BlockHeight + 1,
            unitLayout.BlockDepth + 1,
            bytesPerElement);

        Assert.Equal(unitLayout.BlockWidth * 2, layout.PaddedPitch);
        Assert.Equal(unitLayout.BlockHeight * 2, layout.PaddedHeight);
        Assert.Equal(unitLayout.BlockDepth * 2, layout.PaddedDepth);
        Assert.Equal(0x8_0000UL, layout.GuestSpanBytes);
        Assert.Equal(
            0x1_0000UL,
            layout.GetGuestByteOffset(unitLayout.BlockWidth, 0, 0));
        Assert.Equal(
            0x2_0000UL,
            layout.GetGuestByteOffset(0, unitLayout.BlockHeight, 0));
        Assert.Equal(
            0x4_0000UL,
            layout.GetGuestByteOffset(0, 0, unitLayout.BlockDepth));
        Assert.Equal(
            0x7_0000UL,
            layout.GetGuestByteOffset(
                unitLayout.BlockWidth,
                unitLayout.BlockHeight,
                unitLayout.BlockDepth));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(8u)]
    [InlineData(16u)]
    public void Detile_FullLabelledMultiBlockVolume_ProducesTightZYxBytes(
        uint bytesPerElement)
    {
        var unitLayout = Gfx10Texture3DLayout.Create(1, 1, 1, bytesPerElement);
        var layout = Gfx10Texture3DLayout.Create(
            unitLayout.BlockWidth + 3,
            unitLayout.BlockHeight + 2,
            unitLayout.BlockDepth + 1,
            bytesPerElement);
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
                        component < bytesPerElement;
                        component++)
                    {
                        var value = checked((byte)(
                            ((z * 31) +
                            (y * 17) +
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
    public void Detile_DropsPitchHeightAndDepthPadding()
    {
        var layout = Gfx10Texture3DLayout.Create(
            width: 3,
            height: 2,
            depth: 2,
            bytesPerElement: 4);
        var guestBytes = new byte[checked((int)layout.GuestSpanBytes)];
        Array.Fill(guestBytes, (byte)0xFF);
        var expected = new byte[checked((int)layout.TightSizeBytes)];

        for (uint z = 0; z < layout.Depth; z++)
        {
            for (uint y = 0; y < layout.Height; y++)
            {
                for (uint x = 0; x < layout.Width; x++)
                {
                    var value = checked((byte)(1 + z * 32 + y * 8 + x));
                    var guestOffset = checked(
                        (int)layout.GetGuestByteOffset(x, y, z));
                    guestBytes.AsSpan(guestOffset, 4).Fill(value);
                    var tightOffset = checked(
                        (int)((((ulong)z * layout.Height + y) *
                        layout.Width + x) * 4));
                    expected.AsSpan(tightOffset, 4).Fill(value);
                }
            }
        }

        Assert.Equal(expected, layout.Detile(guestBytes));
        Assert.DoesNotContain((byte)0xFF, expected);
    }

    [Fact]
    public void Create_RejectsZeroDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10Texture3DLayout.Create(0, 1, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10Texture3DLayout.Create(1, 0, 1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10Texture3DLayout.Create(1, 1, 0, 4));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(6u)]
    [InlineData(32u)]
    public void Create_RejectsUnsupportedElementSizes(uint bytesPerElement)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Gfx10Texture3DLayout.Create(1, 1, 1, bytesPerElement));
    }

    [Fact]
    public void Create_RejectsPaddedExtentOverflow()
    {
        Assert.Throws<OverflowException>(
            () => Gfx10Texture3DLayout.Create(uint.MaxValue, 1, 1, 1));
        Assert.Throws<OverflowException>(
            () => Gfx10Texture3DLayout.Create(1, uint.MaxValue, 1, 1));
        Assert.Throws<OverflowException>(
            () => Gfx10Texture3DLayout.Create(1, 1, uint.MaxValue, 1));
    }

    [Fact]
    public void Create_RejectsVolumeSpanOverflow()
    {
        Assert.Throws<OverflowException>(
            () => Gfx10Texture3DLayout.Create(
                uint.MaxValue - 31,
                uint.MaxValue - 31,
                uint.MaxValue - 31,
                2));
    }

    [Fact]
    public void Detile_RejectsManagedBufferOverflow()
    {
        var layout = Gfx10Texture3DLayout.Create(4096, 4096, 128, 1);

        Assert.Throws<OverflowException>(
            () => layout.Detile(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Detile_RejectsShortGuestSpan()
    {
        var layout = Gfx10Texture3DLayout.Create(1, 1, 1, 4);
        var guestBytes = new byte[checked((int)layout.GuestSpanBytes - 1)];

        Assert.Throws<ArgumentException>(() => layout.Detile(guestBytes));
    }

    [Fact]
    public void GuestOffset_RejectsCoordinatesOutsideTheResource()
    {
        var layout = Gfx10Texture3DLayout.Create(3, 2, 4, 4);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GetGuestByteOffset(3, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GetGuestByteOffset(0, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => layout.GetGuestByteOffset(0, 0, 4));
    }
}
