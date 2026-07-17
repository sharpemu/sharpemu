// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerNv12LayoutTests
{
    [Theory]
    [InlineData(1920, 2048)]
    [InlineData(3840, 3840)]
    [InlineData(4097, 4352)]
    public void CalculateNv12Pitch_AlignsTo256Bytes(int width, int expectedPitch)
    {
        Assert.Equal(expectedPitch, AvPlayerExports.CalculateNv12Pitch(width));
    }

    [Fact]
    public void CalculateNv12BufferSize_IncludesBothPlanesAtTheAlignedPitch()
    {
        Assert.Equal(3_317_760, AvPlayerExports.CalculateNv12BufferSize(2048, 1080));
    }

    [Fact]
    public void CopyNv12ToGuestBuffer_UsesSourceStridesAndPitchedUvOffset()
    {
        const int width = 4;
        const int height = 4;
        const int sourceLumaStride = 6;
        const int sourceChromaStride = 8;
        const int destinationPitch = 8;
        var source = Enumerable.Repeat((byte)0xEE, 40).ToArray();
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                source[(row * sourceLumaStride) + column] = checked((byte)(1 + (row * 10) + column));
            }
        }
        var sourceChromaOffset = sourceLumaStride * height;
        for (var row = 0; row < height / 2; row++)
        {
            for (var column = 0; column < width; column++)
            {
                source[sourceChromaOffset + (row * sourceChromaStride) + column] =
                    checked((byte)(101 + (row * 10) + column));
            }
        }

        var destination = Enumerable.Repeat((byte)0xCC, 48).ToArray();
        AvPlayerExports.CopyNv12ToGuestBuffer(
            source,
            destination,
            width,
            height,
            sourceLumaStride,
            sourceChromaStride,
            destinationPitch);

        var expected = new byte[48];
        for (var row = 0; row < height; row++)
        {
            source.AsSpan(row * sourceLumaStride, width)
                .CopyTo(expected.AsSpan(row * destinationPitch, width));
        }
        var destinationChromaOffset = destinationPitch * height;
        for (var row = 0; row < height / 2; row++)
        {
            source.AsSpan(sourceChromaOffset + (row * sourceChromaStride), width)
                .CopyTo(expected.AsSpan(destinationChromaOffset + (row * destinationPitch), width));
        }
        Assert.Equal(expected, destination);
    }
}
