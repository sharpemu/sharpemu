// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Agc;

internal readonly struct Gfx10TextureLayout
{
    public const ulong BlockSizeBytes = 64 * 1024;

    private Gfx10TextureLayout(
        uint width,
        uint height,
        uint arrayLayers,
        uint bytesPerElement,
        uint blockWidth,
        uint blockHeight,
        uint paddedPitch,
        uint paddedHeight,
        ulong sliceSizeBytes,
        ulong guestSpanBytes,
        ulong tightSizeBytes)
    {
        Width = width;
        Height = height;
        ArrayLayers = arrayLayers;
        BytesPerElement = bytesPerElement;
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;
        PaddedPitch = paddedPitch;
        PaddedHeight = paddedHeight;
        SliceSizeBytes = sliceSizeBytes;
        GuestSpanBytes = guestSpanBytes;
        TightSizeBytes = tightSizeBytes;
    }

    public uint Width { get; }
    public uint Height { get; }
    public uint ArrayLayers { get; }
    public uint BytesPerElement { get; }
    public uint BlockWidth { get; }
    public uint BlockHeight { get; }
    public uint PaddedPitch { get; }
    public uint PaddedHeight { get; }
    public ulong SliceSizeBytes { get; }
    public ulong GuestSpanBytes { get; }
    public ulong TightSizeBytes { get; }

    public static Gfx10TextureLayout Create(
        uint width,
        uint height,
        uint arrayLayers,
        uint bytesPerElement)
    {
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(arrayLayers);

        var (blockWidth, blockHeight) = bytesPerElement switch
        {
            1 => (256u, 256u),
            2 => (256u, 128u),
            4 => (128u, 128u),
            8 => (128u, 64u),
            16 => (64u, 64u),
            _ => throw new ArgumentOutOfRangeException(
                nameof(bytesPerElement),
                bytesPerElement,
                "GFX10 ADDR_SW_64KB_S requires a 1, 2, 4, 8, or 16-byte uncompressed element."),
        };

        var paddedPitch = AlignUp(width, blockWidth);
        var paddedHeight = AlignUp(height, blockHeight);
        var blockColumns = paddedPitch / blockWidth;
        var blockRows = paddedHeight / blockHeight;
        var sliceSizeBytes = checked(
            (ulong)blockColumns * blockRows * BlockSizeBytes);
        var guestSpanBytes = checked(sliceSizeBytes * arrayLayers);
        var tightSizeBytes = checked(
            (ulong)width * height * bytesPerElement * arrayLayers);

        return new Gfx10TextureLayout(
            width,
            height,
            arrayLayers,
            bytesPerElement,
            blockWidth,
            blockHeight,
            paddedPitch,
            paddedHeight,
            sliceSizeBytes,
            guestSpanBytes,
            tightSizeBytes);
    }

    public ulong GetGuestByteOffset(uint x, uint y, uint arrayLayer)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            arrayLayer,
            ArrayLayers);

        var blockX = x / BlockWidth;
        var blockY = y / BlockHeight;
        var blockColumns = PaddedPitch / BlockWidth;
        var blockIndex = checked((ulong)blockY * blockColumns + blockX);
        var xByte = checked((x % BlockWidth) * BytesPerElement);
        var yInBlock = y % BlockHeight;

        return checked(
            (arrayLayer * SliceSizeBytes) +
            (blockIndex * BlockSizeBytes) +
            GetBlockByteOffset(xByte, yInBlock, BytesPerElement));
    }

    public byte[] Detile(ReadOnlySpan<byte> guestBytes)
    {
        if (GuestSpanBytes > int.MaxValue || TightSizeBytes > int.MaxValue)
        {
            throw new OverflowException(
                "The texture is too large for a contiguous managed buffer.");
        }

        if ((ulong)guestBytes.Length < GuestSpanBytes)
        {
            throw new ArgumentException(
                $"The tiled input requires at least {GuestSpanBytes} bytes.",
                nameof(guestBytes));
        }

        var tightBytes = new byte[checked((int)TightSizeBytes)];
        var destinationOffset = 0;
        var elementSize = checked((int)BytesPerElement);
        for (uint layer = 0; layer < ArrayLayers; layer++)
        {
            for (uint y = 0; y < Height; y++)
            {
                for (uint x = 0; x < Width; x++)
                {
                    var sourceOffset = checked((int)GetGuestByteOffset(x, y, layer));
                    guestBytes.Slice(sourceOffset, elementSize).CopyTo(
                        tightBytes.AsSpan(destinationOffset, elementSize));
                    destinationOffset = checked(destinationOffset + elementSize);
                }
            }
        }

        return tightBytes;
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        var aligned = checked(
            (((ulong)value + alignment - 1) / alignment) * alignment);
        return checked((uint)aligned);
    }

    private static uint GetBlockByteOffset(
        uint x,
        uint y,
        uint bytesPerElement) =>
        bytesPerElement switch
        {
            1 =>
                Bit(x, 0, 0) |
                Bit(x, 1, 1) |
                Bit(x, 2, 2) |
                Bit(x, 3, 3) |
                Bit(y, 0, 4) |
                Bit(y, 1, 5) |
                Bit(y, 2, 6) |
                Bit(y, 3, 7) |
                Bit(y, 4, 8) |
                Bit(x, 4, 9) |
                Bit(y, 5, 10) |
                Bit(x, 5, 11) |
                Bit(y, 6, 12) |
                Bit(x, 6, 13) |
                Bit(y, 7, 14) |
                Bit(x, 7, 15),
            2 or 4 =>
                Bit(x, 0, 0) |
                Bit(x, 1, 1) |
                Bit(x, 2, 2) |
                Bit(x, 3, 3) |
                Bit(y, 0, 4) |
                Bit(y, 1, 5) |
                Bit(y, 2, 6) |
                Bit(x, 4, 7) |
                Bit(y, 3, 8) |
                Bit(x, 5, 9) |
                Bit(y, 4, 10) |
                Bit(x, 6, 11) |
                Bit(y, 5, 12) |
                Bit(x, 7, 13) |
                Bit(y, 6, 14) |
                Bit(x, 8, 15),
            8 or 16 =>
                Bit(x, 0, 0) |
                Bit(x, 1, 1) |
                Bit(x, 2, 2) |
                Bit(x, 3, 3) |
                Bit(y, 0, 4) |
                Bit(y, 1, 5) |
                Bit(x, 4, 6) |
                Bit(x, 5, 7) |
                Bit(y, 2, 8) |
                Bit(x, 6, 9) |
                Bit(y, 3, 10) |
                Bit(x, 7, 11) |
                Bit(y, 4, 12) |
                Bit(x, 8, 13) |
                Bit(y, 5, 14) |
                Bit(x, 9, 15),
            _ => throw new UnreachableException(),
        };

    private static uint Bit(
        uint value,
        int sourceBit,
        int destinationBit) =>
        ((value >> sourceBit) & 1u) << destinationBit;
}

internal readonly struct Gfx10Texture3DLayout
{
    public const ulong BlockSizeBytes = 64 * 1024;

    private Gfx10Texture3DLayout(
        uint width,
        uint height,
        uint depth,
        uint bytesPerElement,
        uint blockWidth,
        uint blockHeight,
        uint blockDepth,
        uint paddedPitch,
        uint paddedHeight,
        uint paddedDepth,
        ulong guestSpanBytes,
        ulong tightSizeBytes)
    {
        Width = width;
        Height = height;
        Depth = depth;
        BytesPerElement = bytesPerElement;
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;
        BlockDepth = blockDepth;
        PaddedPitch = paddedPitch;
        PaddedHeight = paddedHeight;
        PaddedDepth = paddedDepth;
        GuestSpanBytes = guestSpanBytes;
        TightSizeBytes = tightSizeBytes;
    }

    public uint Width { get; }
    public uint Height { get; }
    public uint Depth { get; }
    public uint BytesPerElement { get; }
    public uint BlockWidth { get; }
    public uint BlockHeight { get; }
    public uint BlockDepth { get; }
    public uint PaddedPitch { get; }
    public uint PaddedHeight { get; }
    public uint PaddedDepth { get; }
    public ulong GuestSpanBytes { get; }
    public ulong TightSizeBytes { get; }

    public static Gfx10Texture3DLayout Create(
        uint width,
        uint height,
        uint depth,
        uint bytesPerElement)
    {
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(depth);

        // GFX10_SW_64K_S3_PATINFO describes a thick 64 KiB standard-
        // swizzled block. Unlike a 2D array, Z participates in the address
        // equation within each block.
        var (blockWidth, blockHeight, blockDepth) = bytesPerElement switch
        {
            1 => (64u, 32u, 32u),
            2 => (32u, 32u, 32u),
            4 => (32u, 32u, 16u),
            8 => (32u, 16u, 16u),
            16 => (16u, 16u, 16u),
            _ => throw new ArgumentOutOfRangeException(
                nameof(bytesPerElement),
                bytesPerElement,
                "GFX10 ADDR_SW_64KB_S 3D requires a 1, 2, 4, 8, or 16-byte uncompressed element."),
        };

        var paddedPitch = AlignUp(width, blockWidth);
        var paddedHeight = AlignUp(height, blockHeight);
        var paddedDepth = AlignUp(depth, blockDepth);
        var blockCount = checked(
            (ulong)(paddedPitch / blockWidth) *
            (paddedHeight / blockHeight) *
            (paddedDepth / blockDepth));
        var guestSpanBytes = checked(
            blockCount * BlockSizeBytes);
        var tightSizeBytes = checked(
            (ulong)width * height * depth * bytesPerElement);

        return new Gfx10Texture3DLayout(
            width,
            height,
            depth,
            bytesPerElement,
            blockWidth,
            blockHeight,
            blockDepth,
            paddedPitch,
            paddedHeight,
            paddedDepth,
            guestSpanBytes,
            tightSizeBytes);
    }

    public ulong GetGuestByteOffset(uint x, uint y, uint z)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, Depth);

        var blockX = x / BlockWidth;
        var blockY = y / BlockHeight;
        var blockZ = z / BlockDepth;
        var blockColumns = PaddedPitch / BlockWidth;
        var blockRows = PaddedHeight / BlockHeight;
        var blockIndex = checked(
            ((ulong)blockZ * blockRows + blockY) * blockColumns + blockX);
        var blockOffset = GetBlockByteOffset(
            x % BlockWidth,
            y % BlockHeight,
            z % BlockDepth,
            BytesPerElement);

        return checked(
            blockIndex * BlockSizeBytes + blockOffset);
    }

    public byte[] Detile(ReadOnlySpan<byte> guestBytes)
    {
        if (GuestSpanBytes > int.MaxValue || TightSizeBytes > int.MaxValue)
        {
            throw new OverflowException(
                "The 3D texture is too large for a contiguous managed buffer.");
        }

        if ((ulong)guestBytes.Length < GuestSpanBytes)
        {
            throw new ArgumentException(
                $"The tiled 3D input requires at least {GuestSpanBytes} bytes.",
                nameof(guestBytes));
        }

        var tightBytes = new byte[checked((int)TightSizeBytes)];
        var destinationOffset = 0;
        var elementSize = checked((int)BytesPerElement);
        for (uint z = 0; z < Depth; z++)
        {
            for (uint y = 0; y < Height; y++)
            {
                for (uint x = 0; x < Width; x++)
                {
                    var sourceOffset = checked((int)GetGuestByteOffset(x, y, z));
                    guestBytes.Slice(sourceOffset, elementSize).CopyTo(
                        tightBytes.AsSpan(destinationOffset, elementSize));
                    destinationOffset = checked(destinationOffset + elementSize);
                }
            }
        }

        return tightBytes;
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        var aligned = checked(
            (((ulong)value + alignment - 1) / alignment) * alignment);
        return checked((uint)aligned);
    }

    private static uint GetBlockByteOffset(
        uint x,
        uint y,
        uint z,
        uint bytesPerElement) =>
        bytesPerElement switch
        {
            1 =>
                Bit(x, 0, 0) |
                Bit(x, 1, 1) |
                Bit(z, 0, 2) |
                Bit(y, 0, 3) |
                Bit(z, 1, 4) |
                Bit(y, 1, 5) |
                Bit(x, 2, 6) |
                Bit(z, 2, 7) |
                Bit(y, 2, 8) |
                Bit(x, 3, 9) |
                Bit(z, 3, 10) |
                Bit(y, 3, 11) |
                Bit(x, 4, 12) |
                Bit(z, 4, 13) |
                Bit(y, 4, 14) |
                Bit(x, 5, 15),
            2 =>
                Bit(x, 0, 1) |
                Bit(z, 0, 2) |
                Bit(y, 0, 3) |
                Bit(z, 1, 4) |
                Bit(y, 1, 5) |
                Bit(x, 1, 6) |
                Bit(z, 2, 7) |
                Bit(y, 2, 8) |
                Bit(x, 2, 9) |
                Bit(z, 3, 10) |
                Bit(y, 3, 11) |
                Bit(x, 3, 12) |
                Bit(z, 4, 13) |
                Bit(y, 4, 14) |
                Bit(x, 4, 15),
            4 =>
                Bit(x, 0, 2) |
                Bit(y, 0, 3) |
                Bit(z, 0, 4) |
                Bit(y, 1, 5) |
                Bit(x, 1, 6) |
                Bit(z, 1, 7) |
                Bit(y, 2, 8) |
                Bit(x, 2, 9) |
                Bit(z, 2, 10) |
                Bit(y, 3, 11) |
                Bit(x, 3, 12) |
                Bit(z, 3, 13) |
                Bit(y, 4, 14) |
                Bit(x, 4, 15),
            8 =>
                Bit(x, 0, 3) |
                Bit(z, 0, 4) |
                Bit(y, 0, 5) |
                Bit(x, 1, 6) |
                Bit(z, 1, 7) |
                Bit(y, 1, 8) |
                Bit(x, 2, 9) |
                Bit(z, 2, 10) |
                Bit(y, 2, 11) |
                Bit(x, 3, 12) |
                Bit(z, 3, 13) |
                Bit(y, 3, 14) |
                Bit(x, 4, 15),
            16 =>
                Bit(z, 0, 4) |
                Bit(y, 0, 5) |
                Bit(x, 0, 6) |
                Bit(z, 1, 7) |
                Bit(y, 1, 8) |
                Bit(x, 1, 9) |
                Bit(z, 2, 10) |
                Bit(y, 2, 11) |
                Bit(x, 2, 12) |
                Bit(z, 3, 13) |
                Bit(y, 3, 14) |
                Bit(x, 3, 15),
            _ => throw new UnreachableException(),
        };

    private static uint Bit(
        uint value,
        int sourceBit,
        int destinationBit) =>
        ((value >> sourceBit) & 1u) << destinationBit;
}
