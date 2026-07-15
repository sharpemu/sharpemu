// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;

namespace SharpEmu.Libs.VideoOut;

internal static class PngSplashLoader
{
    private const uint CrcPolynomial = 0xEDB88320;
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static ReadOnlySpan<byte> PngSignature =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
    ];

    public static bool TryLoad(out byte[] pixels, out uint width, out uint height)
        => TryLoad("pic0.png", requestRgba: false, out pixels, out width, out height);

    public static bool TryLoadIcon(out byte[] pixels, out uint width, out uint height)
        => TryLoad("icon0.png", requestRgba: true, out pixels, out width, out height);

    private static bool TryLoad(
        string fileName,
        bool requestRgba,
        out byte[] pixels,
        out uint width,
        out uint height)
    {
        pixels = [];
        width = 0;
        height = 0;

        try
        {
            var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
            if (string.IsNullOrWhiteSpace(app0Root))
            {
                return false;
            }

            var path = Path.Combine(app0Root, "sce_sys", fileName);
            if (!File.Exists(path))
            {
                return false;
            }

            return TryDecode(
                File.ReadAllBytes(path),
                out pixels,
                out width,
                out height,
                requestRgba);
        }
        catch
        {
            pixels = [];
            width = 0;
            height = 0;
            return false;
        }
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> png,
        out byte[] pixels,
        out uint width,
        out uint height,
        bool requestRgba)
    {
        pixels = [];
        width = 0;
        height = 0;
        if (png.Length < 33 || !png[..8].SequenceEqual(PngSignature))
        {
            return false;
        }

        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        using var compressed = new MemoryStream();
        var offset = 8;
        while (offset <= png.Length - 12)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (chunkLength > int.MaxValue || offset > png.Length - 12 - (int)chunkLength)
            {
                return false;
            }

            var chunkType = png.Slice(offset + 4, 4);
            var chunkData = png.Slice(offset + 8, (int)chunkLength);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                png.Slice(offset + 8 + (int)chunkLength, 4));
            if (CalculateCrc(chunkType, chunkData) != expectedCrc)
            {
                return false;
            }

            if (chunkType.SequenceEqual("IHDR"u8))
            {
                if (chunkData.Length != 13)
                {
                    return false;
                }

                width = BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                height = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                interlace = chunkData[12];
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                compressed.Write(chunkData);
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += checked((int)chunkLength + 12);
        }

        var sourceBytesPerPixel = colorType switch
        {
            2 => 3,
            6 => 4,
            _ => 0,
        };
        if (width == 0 ||
            height == 0 ||
            width > 16384 ||
            height > 16384 ||
            bitDepth != 8 ||
            interlace != 0 ||
            sourceBytesPerPixel == 0 ||
            compressed.Length == 0)
        {
            return false;
        }

        var stride = checked((int)width * sourceBytesPerPixel);
        var scanlineLength = checked(stride + 1);
        var decompressedLength = checked(scanlineLength * (int)height);
        var scanlines = GC.AllocateUninitializedArray<byte>(decompressedLength);
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            zlib.ReadExactly(scanlines);
            if (zlib.ReadByte() != -1)
            {
                return false;
            }
        }

        var reconstructed = GC.AllocateUninitializedArray<byte>(checked(stride * (int)height));
        for (var y = 0; y < (int)height; y++)
        {
            var sourceLine = scanlines.AsSpan(y * scanlineLength + 1, stride);
            var targetLine = reconstructed.AsSpan(y * stride, stride);
            var previousLine = y == 0
                ? ReadOnlySpan<byte>.Empty
                : reconstructed.AsSpan((y - 1) * stride, stride);
            if (!TryUnfilter(
                    scanlines[y * scanlineLength],
                    sourceLine,
                    previousLine,
                    targetLine,
                    sourceBytesPerPixel))
            {
                return false;
            }
        }

        pixels = GC.AllocateUninitializedArray<byte>(checked((int)width * (int)height * 4));
        for (int sourceOffset = 0, targetOffset = 0;
             sourceOffset < reconstructed.Length;
             sourceOffset += sourceBytesPerPixel, targetOffset += 4)
        {
            pixels[targetOffset] = requestRgba
                ? reconstructed[sourceOffset]
                : reconstructed[sourceOffset + 2];
            pixels[targetOffset + 1] = reconstructed[sourceOffset + 1];
            pixels[targetOffset + 2] = requestRgba
                ? reconstructed[sourceOffset + 2]
                : reconstructed[sourceOffset];
            pixels[targetOffset + 3] = sourceBytesPerPixel == 4
                ? reconstructed[sourceOffset + 3]
                : (byte)0xFF;
        }

        return true;
    }

    private static uint CalculateCrc(ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> chunkData)
    {
        var crc = UpdateCrc(uint.MaxValue, chunkType);
        return ~UpdateCrc(crc, chunkData);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc = CrcTable[(byte)(crc ^ value)] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? CrcPolynomial ^ (value >> 1)
                    : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static bool TryUnfilter(
        byte filter,
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> previous,
        Span<byte> target,
        int bytesPerPixel)
    {
        for (var x = 0; x < source.Length; x++)
        {
            var left = x >= bytesPerPixel ? target[x - bytesPerPixel] : (byte)0;
            var above = previous.IsEmpty ? (byte)0 : previous[x];
            var upperLeft = !previous.IsEmpty && x >= bytesPerPixel
                ? previous[x - bytesPerPixel]
                : (byte)0;
            target[x] = filter switch
            {
                0 => source[x],
                1 => unchecked((byte)(source[x] + left)),
                2 => unchecked((byte)(source[x] + above)),
                3 => unchecked((byte)(source[x] + ((left + above) >> 1))),
                4 => unchecked((byte)(source[x] + Paeth(left, above, upperLeft))),
                _ => source[x],
            };

            if (filter > 4)
            {
                return false;
            }
        }

        return true;
    }

    private static byte Paeth(byte left, byte above, byte upperLeft)
    {
        var estimate = left + above - upperLeft;
        var leftDistance = Math.Abs(estimate - left);
        var aboveDistance = Math.Abs(estimate - above);
        var upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left
            : aboveDistance <= upperLeftDistance
                ? above
                : upperLeft;
    }
}
