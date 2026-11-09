// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Media;
using Xunit;

namespace SharpEmu.Libs.Tests.Media;

public sealed class HostMovieYuv420Tests
{
    [Theory]
    [InlineData(1u, 1u)]
    [InlineData(2u, 2u)]
    [InlineData(3u, 2u)]
    [InlineData(7u, 5u)]
    [InlineData(64u, 33u)]
    [InlineData(4u, 2u)]
    [InlineData(8u, 6u)]
    [InlineData(1920u, 1080u)]
    public void ConvertFromBgra_MatchesPerPixelReference(uint width, uint height)
    {
        var bgra = new byte[width * height * 4];
        new System.Random((int)(width * 31 + height)).NextBytes(bgra);
        var expected = ReferenceConvert(bgra, (int)width, (int)height);
        var actual = new byte[HostMovieYuv420.FrameLength(width, height)];

        HostMovieYuv420.ConvertFromBgra(bgra, width, height, actual);

        Assert.Equal(expected, actual);
        var scalar = new byte[actual.Length];
        HostMovieYuv420.ConvertFromBgra(bgra, width, height, scalar, useVectorized: false);
        Assert.Equal(scalar, actual);
    }

    [Fact]
    public void ConvertFromBgra_VectorStoresRespectUnalignedDestinationBounds()
    {
        const uint width = 8, height = 4;
        var source = new byte[width * height * 4 + 1];
        new System.Random(731).NextBytes(source);
        var length = HostMovieYuv420.FrameLength(width, height);
        var destination = Enumerable.Repeat((byte)0xCD, length + 9).ToArray();
        HostMovieYuv420.ConvertFromBgra(source.AsSpan(1), width, height, destination.AsSpan(3, length));
        Assert.Equal(ReferenceConvert(source[1..], (int)width, (int)height), destination.AsSpan(3, length).ToArray());
        Assert.All(destination[..3], value => Assert.Equal(0xCD, value));
        Assert.All(destination[(3 + length)..], value => Assert.Equal(0xCD, value));
    }

    [Fact]
    public void Decoder_ConvertsEachDecodedFrameIntoPlanes()
    {
        using var decoder = new HostMovieYuv420Decoder(new SolidDecoder(b: 40, g: 120, r: 200));
        var frame = new byte[4 * 4 * 4];

        Assert.True(decoder.TryDecodeNextFrame(frame));

        var expected = ReferenceConvert(SolidFrame(4, 4, 40, 120, 200), 4, 4);
        Assert.Equal(expected, frame.AsSpan(0, expected.Length).ToArray());
    }

    [Fact]
    public void ConvertFromBgra_RejectsShortDestination()
    {
        var bgra = new byte[2 * 2 * 4];
        var destination = new byte[HostMovieYuv420.FrameLength(2, 2) - 1];

        Assert.Throws<ArgumentException>(() =>
            HostMovieYuv420.ConvertFromBgra(bgra, 2, 2, destination));
    }

    private static byte[] SolidFrame(int width, int height, byte b, byte g, byte r)
    {
        var frame = new byte[width * height * 4];
        for (var offset = 0; offset < frame.Length; offset += 4)
        {
            frame[offset] = b;
            frame[offset + 1] = g;
            frame[offset + 2] = r;
            frame[offset + 3] = 255;
        }

        return frame;
    }

    // Straightforward two-pass conversion the presenter used to run per frame.
    private static byte[] ReferenceConvert(byte[] bgra, int width, int height)
    {
        var chromaWidth = (width + 1) / 2;
        var chromaHeight = (height + 1) / 2;
        var result = new byte[width * height + chromaWidth * chromaHeight * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = (y * width + x) * 4;
                result[y * width + x] = Clamp(
                    (54 * bgra[source + 2] + 183 * bgra[source + 1] + 19 * bgra[source] + 128) >> 8);
            }
        }

        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                int red = 0, green = 0, blue = 0, samples = 0;
                for (var sampleY = y; sampleY < Math.Min(y + 2, height); sampleY++)
                {
                    for (var sampleX = x; sampleX < Math.Min(x + 2, width); sampleX++)
                    {
                        var source = (sampleY * width + sampleX) * 4;
                        blue += bgra[source];
                        green += bgra[source + 1];
                        red += bgra[source + 2];
                        samples++;
                    }
                }

                red /= samples;
                green /= samples;
                blue /= samples;
                var destination = width * height + ((y / 2) * chromaWidth + x / 2) * 2;
                result[destination] = Clamp(((128 * red - 116 * green - 12 * blue + 128) >> 8) + 128);
                result[destination + 1] = Clamp(((-29 * red - 99 * green + 128 * blue + 128) >> 8) + 128);
            }
        }

        return result;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private sealed class SolidDecoder(byte b, byte g, byte r) : IMediaFrameDecoder
    {
        public uint Width => 4;

        public uint Height => 4;

        public uint FramesPerSecondNumerator => 30;

        public uint FramesPerSecondDenominator => 1;

        public bool TryDecodeNextFrame(Span<byte> destination)
        {
            SolidFrame(4, 4, b, g, r).CopyTo(destination);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
