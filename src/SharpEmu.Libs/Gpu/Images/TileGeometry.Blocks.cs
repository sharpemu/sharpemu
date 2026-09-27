// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Images;

// Element byte offsets inside one tiled block; the CPU twins of the tiler shaders.
public static partial class TileGeometry
{
    private static uint Standard4KBOffset(uint x, uint y, uint bytesPerElement)
    {
        uint offset = 0;
        switch (bytesPerElement)
        {
            case 1:
                offset ^= (y << 4) & 0x1f0;
                offset ^= (y << 5) & 0x400;
                offset ^= x & 0x00f;
                offset ^= (x << 5) & 0x200;
                offset ^= (x << 6) & 0x800;
                return offset;
            case 2:
                offset ^= (y << 4) & 0x070;
                offset ^= (y << 5) & 0x100;
                offset ^= (y << 6) & 0x400;
                offset ^= (x << 1) & 0x00e;
                offset ^= (x << 4) & 0x080;
                offset ^= (x << 5) & 0x200;
                offset ^= (x << 6) & 0x800;
                return offset;
            case 4:
                offset ^= (y << 4) & 0x070;
                offset ^= (y << 5) & 0x100;
                offset ^= (y << 6) & 0x400;
                offset ^= (x << 2) & 0x00c;
                offset ^= (x << 5) & 0x080;
                offset ^= (x << 6) & 0x200;
                offset ^= (x << 7) & 0x800;
                return offset;
            case 8:
                offset ^= (y << 4) & 0x030;
                offset ^= (y << 6) & 0x100;
                offset ^= (y << 7) & 0x400;
                offset ^= (x << 3) & 0x008;
                offset ^= (x << 5) & 0x0c0;
                offset ^= (x << 6) & 0x200;
                offset ^= (x << 7) & 0x800;
                return offset;
            case 16:
                offset ^= (y << 4) & 0x030;
                offset ^= (y << 6) & 0x100;
                offset ^= (y << 7) & 0x400;
                offset ^= (x << 6) & 0x0c0;
                offset ^= (x << 7) & 0x200;
                offset ^= (x << 8) & 0x800;
                return offset;
            default:
                throw SubmissionScheduler.Fatal($"The 4 KiB tile block does not support this element size: bytes={bytesPerElement}.");
        }
    }

    private static uint Standard4KBVolumeOffset(uint x, uint y, uint z, uint bytesPerElement)
    {
        uint offset = 0;
        switch (bytesPerElement)
        {
            case 1:
                offset ^= x & 0x3;
                offset ^= (x << 4) & 0x40;
                offset ^= (x << 6) & 0x200;
                offset ^= (y << 3) & 0x8;
                offset ^= (y << 4) & 0x20;
                offset ^= (y << 6) & 0x100;
                offset ^= (y << 8) & 0x800;
                offset ^= (z << 2) & 0x4;
                offset ^= (z << 3) & 0x10;
                offset ^= (z << 5) & 0x80;
                offset ^= (z << 7) & 0x400;
                return offset;
            case 2:
                offset ^= (x << 1) & 0x2;
                offset ^= (x << 5) & 0x40;
                offset ^= (x << 7) & 0x200;
                offset ^= (y << 3) & 0x8;
                offset ^= (y << 4) & 0x20;
                offset ^= (y << 6) & 0x100;
                offset ^= (y << 8) & 0x800;
                offset ^= (z << 2) & 0x4;
                offset ^= (z << 3) & 0x10;
                offset ^= (z << 5) & 0x80;
                offset ^= (z << 7) & 0x400;
                return offset;
            case 4:
                offset ^= (x << 2) & 0x4;
                offset ^= (x << 5) & 0x40;
                offset ^= (x << 7) & 0x200;
                offset ^= (y << 3) & 0x8;
                offset ^= (y << 4) & 0x20;
                offset ^= (y << 6) & 0x100;
                offset ^= (y << 8) & 0x800;
                offset ^= (z << 4) & 0x10;
                offset ^= (z << 6) & 0x80;
                offset ^= (z << 8) & 0x400;
                return offset;
            case 8:
                offset ^= (x << 3) & 0x8;
                offset ^= (x << 5) & 0x40;
                offset ^= (x << 7) & 0x200;
                offset ^= (y << 5) & 0x20;
                offset ^= (y << 7) & 0x100;
                offset ^= (y << 9) & 0x800;
                offset ^= (z << 4) & 0x10;
                offset ^= (z << 6) & 0x80;
                offset ^= (z << 8) & 0x400;
                return offset;
            case 16:
                offset ^= (x << 6) & 0x40;
                offset ^= (x << 8) & 0x200;
                offset ^= (y << 5) & 0x20;
                offset ^= (y << 7) & 0x100;
                offset ^= (y << 9) & 0x800;
                offset ^= (z << 4) & 0x10;
                offset ^= (z << 6) & 0x80;
                offset ^= (z << 8) & 0x400;
                return offset;
            default:
                throw SubmissionScheduler.Fatal($"The 4 KiB volume tile block does not support this element size: bytes={bytesPerElement}.");
        }
    }

    private static uint Bit(uint value, int source, int destination) => ((value >> source) & 1) << destination;

    private static readonly byte[][] Standard64KBVolumeSources = [[4, 4, 4, 5], [3, 4, 4, 4], [3, 3, 4, 4], [3, 3, 3, 4], [2, 3, 3, 3]];

    private static uint Standard64KBVolumeOffset(uint x, uint y, uint z, uint bytesPerElement)
    {
        var bits = Standard64KBVolumeSources[Log2(bytesPerElement)];
        return Standard4KBVolumeOffset(x, y, z, bytesPerElement) ^ Bit(x, bits[0], 12) ^ Bit(z, bits[1], 13) ^ Bit(y, bits[2], 14) ^ Bit(x, bits[3], 15);
    }

    private static uint Standard256BOffset(uint x, uint y, uint bytesPerElement) => Standard4KBOffset(x, y, bytesPerElement) & 0xff;

    private static readonly byte[][] Prt64KBVolumeSources = [[4, 5, 4, 4], [4, 4, 3, 4], [4, 4, 3, 3], [3, 4, 3, 3], [3, 3, 2, 3]];
    private static readonly byte[][] Prt64KBSources = [[7, 7, 6, 6], [7, 6, 6, 5], [6, 6, 5, 5], [6, 5, 5, 4], [5, 5, 4, 4]];

    // Block-linear 64 KiB tables: element words indexed by x or y, per element size.
    private static readonly uint[] Standard64KB8X = BuildTable(256, tableIndex => (tableIndex & 0x000f) ^ ((tableIndex << 5) & 0x0200) ^ ((tableIndex << 6) & 0x0800) ^ ((tableIndex << 7) & 0x2000) ^ ((tableIndex << 8) & 0x8000));
    private static readonly uint[] Standard64KB8Y = BuildTable(256, tableIndex => ((tableIndex << 4) & 0x01f0) ^ ((tableIndex << 5) & 0x0400) ^ ((tableIndex << 6) & 0x1000) ^ ((tableIndex << 7) & 0x4000));
    private static readonly uint[] Standard64KB16X = BuildTable(256, tableIndex => (((tableIndex << 1) & 0x000e) ^ ((tableIndex << 4) & 0x0080) ^ ((tableIndex << 5) & 0x0200) ^ ((tableIndex << 6) & 0x0800) ^ ((tableIndex << 7) & 0x2000) ^ ((tableIndex << 8) & 0x8000)) >> 1);
    private static readonly uint[] Standard64KB16Y = BuildTable(128, tableIndex => (((tableIndex << 4) & 0x0070) ^ ((tableIndex << 5) & 0x0100) ^ ((tableIndex << 6) & 0x0400) ^ ((tableIndex << 7) & 0x1000) ^ ((tableIndex << 8) & 0x4000)) >> 1);
    private static readonly uint[] Standard64KB32X = BuildTable(128, tableIndex => (((tableIndex << 2) & 0x0c) ^ ((tableIndex << 5) & 0x80) ^ ((tableIndex << 6) & 0x200) ^ ((tableIndex << 7) & 0x800) ^ ((tableIndex << 8) & 0x2000) ^ ((tableIndex << 9) & 0x8000)) >> 2);
    private static readonly uint[] Standard64KB32Y = BuildTable(128, tableIndex => (((tableIndex << 4) & 0x70) ^ ((tableIndex << 5) & 0x100) ^ ((tableIndex << 6) & 0x400) ^ ((tableIndex << 7) & 0x1000) ^ ((tableIndex << 8) & 0x4000)) >> 2);
    private static readonly uint[] Standard64KB64X = BuildTable(128, tableIndex => (((tableIndex << 3) & 0x0008) ^ ((tableIndex << 5) & 0x00c0) ^ ((tableIndex << 6) & 0x0200) ^ ((tableIndex << 7) & 0x0800) ^ ((tableIndex << 8) & 0x2000) ^ ((tableIndex << 9) & 0x8000)) >> 3);
    private static readonly uint[] Standard64KB64Y = BuildTable(64, tableIndex => (((tableIndex << 4) & 0x0030) ^ ((tableIndex << 6) & 0x0100) ^ ((tableIndex << 7) & 0x0400) ^ ((tableIndex << 8) & 0x1000) ^ ((tableIndex << 9) & 0x4000)) >> 3);
    private static readonly uint[] Standard64KB128X = BuildTable(64, tableIndex => (((tableIndex << 6) & 0x00c0) ^ ((tableIndex << 7) & 0x0200) ^ ((tableIndex << 8) & 0x0800) ^ ((tableIndex << 9) & 0x2000) ^ ((tableIndex << 10) & 0x8000)) >> 4);
    private static readonly uint[] Standard64KB128Y = BuildTable(64, tableIndex => (((tableIndex << 4) & 0x0030) ^ ((tableIndex << 6) & 0x0100) ^ ((tableIndex << 7) & 0x0400) ^ ((tableIndex << 8) & 0x1000) ^ ((tableIndex << 9) & 0x4000)) >> 4);

    private static uint[] BuildTable(int count, Func<uint, uint> word)
    {
        var table = new uint[count];
        for (uint tableIndex = 0; tableIndex < count; tableIndex++)
        {
            table[tableIndex] = word(tableIndex);
        }

        return table;
    }

    private static uint RenderTargetOffset(uint x, uint y, uint bytesPerElement)
    {
        uint offset = 0;
        switch (bytesPerElement)
        {
            case 1:
                offset ^= Bit(x, 0, 0) ^ Bit(x, 1, 1) ^ Bit(x, 2, 2) ^ Bit(x, 3, 3);
                offset ^= Bit(y, 0, 4) ^ Bit(y, 1, 5) ^ Bit(y, 2, 6) ^ Bit(y, 3, 7);
                offset ^= Bit(x, 7, 8) ^ Bit(y, 4, 8) ^ Bit(y, 7, 8);
                offset ^= Bit(x, 4, 9) ^ Bit(y, 4, 9);
                offset ^= Bit(x, 6, 10) ^ Bit(y, 5, 10);
                offset ^= Bit(x, 5, 11) ^ Bit(y, 6, 11);
                offset ^= Bit(x, 6, 12) ^ Bit(y, 6, 13);
                offset ^= Bit(x, 7, 14) ^ Bit(y, 8, 14);
                offset ^= Bit(x, 8, 15) ^ Bit(y, 7, 15);
                return offset;
            case 2:
                offset ^= Bit(x, 0, 1) ^ Bit(x, 1, 2) ^ Bit(x, 2, 3);
                offset ^= Bit(y, 0, 4) ^ Bit(y, 1, 5) ^ Bit(y, 2, 6) ^ Bit(x, 3, 7);
                offset ^= Bit(x, 7, 8) ^ Bit(y, 4, 8) ^ Bit(y, 7, 8);
                offset ^= Bit(x, 4, 9) ^ Bit(y, 4, 9);
                offset ^= Bit(x, 6, 10) ^ Bit(y, 5, 10);
                offset ^= Bit(x, 5, 11) ^ Bit(y, 6, 11);
                offset ^= Bit(y, 3, 12) ^ Bit(x, 6, 13);
                offset ^= Bit(x, 7, 14) ^ Bit(y, 7, 14);
                offset ^= Bit(x, 8, 15) ^ Bit(y, 6, 15);
                return offset;
            case 4:
                offset ^= Bit(x, 0, 2) ^ Bit(x, 1, 3);
                offset ^= Bit(y, 0, 4) ^ Bit(y, 1, 5) ^ Bit(x, 2, 6) ^ Bit(y, 2, 7);
                offset ^= Bit(x, 7, 8) ^ Bit(y, 4, 8) ^ Bit(y, 7, 8);
                offset ^= Bit(x, 4, 9) ^ Bit(y, 4, 9);
                offset ^= Bit(x, 6, 10) ^ Bit(y, 5, 10);
                offset ^= Bit(x, 5, 11) ^ Bit(y, 6, 11);
                offset ^= Bit(x, 3, 12) ^ Bit(y, 3, 13);
                offset ^= Bit(x, 6, 14) ^ Bit(y, 7, 14);
                offset ^= Bit(x, 7, 15) ^ Bit(y, 6, 15);
                return offset;
            case 8:
                offset ^= Bit(x, 0, 3) ^ Bit(y, 0, 4) ^ Bit(x, 1, 5) ^ Bit(x, 2, 6) ^ Bit(y, 1, 7);
                offset ^= Bit(x, 7, 8) ^ Bit(y, 4, 8) ^ Bit(y, 7, 8);
                offset ^= Bit(x, 4, 9) ^ Bit(y, 4, 9);
                offset ^= Bit(x, 6, 10) ^ Bit(y, 5, 10);
                offset ^= Bit(x, 5, 11) ^ Bit(y, 6, 11);
                offset ^= Bit(y, 2, 12) ^ Bit(x, 3, 13);
                offset ^= Bit(x, 7, 14) ^ Bit(y, 3, 14);
                offset ^= Bit(x, 6, 15) ^ Bit(y, 6, 15);
                return offset;
            case 16:
                offset ^= Bit(x, 0, 4) ^ Bit(y, 0, 5) ^ Bit(x, 1, 6) ^ Bit(y, 1, 7);
                offset ^= Bit(x, 7, 8) ^ Bit(y, 4, 8) ^ Bit(y, 7, 8);
                offset ^= Bit(x, 4, 9) ^ Bit(y, 4, 9);
                offset ^= Bit(x, 6, 10) ^ Bit(y, 5, 10);
                offset ^= Bit(x, 5, 11) ^ Bit(y, 6, 11);
                offset ^= Bit(x, 2, 12) ^ Bit(y, 2, 13);
                offset ^= Bit(x, 6, 14) ^ Bit(y, 3, 14);
                offset ^= Bit(x, 3, 15) ^ Bit(y, 6, 15);
                return offset;
            default:
                throw SubmissionScheduler.Fatal($"The render-target tile block does not support this element size: bytes={bytesPerElement}.");
        }
    }

    private static uint Depth64KB8X(uint x) =>
        (x & 0x0001) ^ ((x << 1) & 0x0004) ^ ((x << 2) & 0x0010) ^ ((x << 3) & 0x0040) ^ ((x << 5) & 0x0300) ^
        ((x << 4) & 0x0400) ^ ((x << 6) & 0x0800) ^ ((x << 7) & 0x2000) ^ ((x << 8) & 0x8000);

    private static uint Depth64KB8Y(uint y) =>
        ((y << 1) & 0x0002) ^ ((y << 2) & 0x0008) ^ ((y << 3) & 0x00a0) ^ ((y << 5) & 0x0f00) ^ ((y << 6) & 0x1000) ^ ((y << 7) & 0x4000);

    private static uint Depth64KB16X(uint x) =>
        ((x << 1) & 0x0002) ^ ((x << 2) & 0x0008) ^ ((x << 3) & 0x0020) ^ ((x << 4) & 0x0480) ^ ((x << 5) & 0x0300) ^
        ((x << 6) & 0x0800) ^ ((x << 7) & 0x2000) ^ ((x << 8) & 0x8000);

    private static uint Depth64KB16Y(uint y) =>
        ((y << 2) & 0x0004) ^ ((y << 3) & 0x0010) ^ ((y << 4) & 0x0040) ^ ((y << 5) & 0x0f00) ^ ((y << 8) & 0x5000);

    private static uint Depth64KB32X(uint x) =>
        ((x << 2) & 0x0004) ^ ((x << 3) & 0x0010) ^ ((x << 4) & 0x0440) ^ ((x << 5) & 0x0300) ^ ((x << 6) & 0x0800) ^ ((x << 9) & 0xa000);

    private static uint Depth64KB32Y(uint y) =>
        ((y << 3) & 0x0008) ^ ((y << 4) & 0x0020) ^ ((y << 5) & 0x0f80) ^ ((y << 9) & 0x1000) ^ ((y << 8) & 0x4000);

    private static uint Depth64KB64X(uint x) =>
        ((x << 3) & 0x0008) ^ ((x << 4) & 0x0420) ^ ((x << 5) & 0x0380) ^ ((x << 6) & 0x0800) ^ ((x << 10) & 0x2000) ^ ((x << 9) & 0x8000);

    private static uint Depth64KB64Y(uint y) =>
        ((y << 4) & 0x0010) ^ ((y << 5) & 0x0f40) ^ ((y << 10) & 0x5000);

    private static bool MatchesKnownLayout(in TileBlockLayout layout) =>
        TryGetBlockLayout(layout.Kind, layout.BytesPerElement, out var expected) &&
        layout.BlockSize == expected.BlockSize && layout.BlockWidth == expected.BlockWidth &&
        layout.BlockHeight == expected.BlockHeight && layout.BlockDepth == expected.BlockDepth;

    public static bool TryGetBlockOffset(in TileBlockLayout layout, uint x, uint y, uint z, out uint byteOffset)
    {
        byteOffset = 0;
        if (!MatchesKnownLayout(layout) || x >= layout.BlockWidth || y >= layout.BlockHeight || z >= layout.BlockDepth)
        {
            return false;
        }

        var bytes = layout.BytesPerElement;
        uint offset;
        switch (layout.Kind)
        {
            case TileBlockKind.Standard256B:
                offset = Standard256BOffset(x, y, bytes);
                break;
            case TileBlockKind.Standard4KB:
                offset = Standard4KBOffset(x, y, bytes);
                break;
            case TileBlockKind.Standard4KB3D:
                offset = Standard4KBVolumeOffset(x, y, z, bytes);
                break;
            case TileBlockKind.Standard64KB3D:
            case TileBlockKind.Prt64KB3D:
                offset = Standard64KBVolumeOffset(x, y, z, bytes);
                if (layout.Kind == TileBlockKind.Prt64KB3D)
                {
                    var bits = Prt64KBVolumeSources[Log2(bytes)];
                    offset ^= Bit(y, bits[0], 10) ^ Bit(x, bits[1], 10) ^ Bit(x, bits[2], 11) ^ Bit(z, bits[3], 11);
                }

                break;
            case TileBlockKind.Standard64KB:
            case TileBlockKind.Prt64KB:
                switch (bytes)
                {
                    case 1: offset = Standard64KB8X[x] ^ Standard64KB8Y[y]; break;
                    case 2: offset = (Standard64KB16X[x] ^ Standard64KB16Y[y]) * 2; break;
                    case 4: offset = (Standard64KB32X[x] ^ Standard64KB32Y[y]) * 4; break;
                    case 8: offset = (Standard64KB64X[x] ^ Standard64KB64Y[y]) * 8; break;
                    case 16: offset = (Standard64KB128X[x] ^ Standard64KB128Y[y]) * 16; break;
                    default: return false;
                }

                if (layout.Kind == TileBlockKind.Prt64KB)
                {
                    var bits = Prt64KBSources[Log2(bytes)];
                    offset ^= Bit(x, bits[0], 8) ^ Bit(y, bits[1], 9) ^ Bit(x, bits[2], 10) ^ Bit(y, bits[3], 11);
                }

                break;
            case TileBlockKind.RenderTarget64KB:
                if (bytes > 16)
                {
                    return false;
                }

                offset = RenderTargetOffset(x, y, bytes);
                break;
            case TileBlockKind.Depth64KB:
                switch (bytes)
                {
                    case 1: offset = Depth64KB8X(x) ^ Depth64KB8Y(y); break;
                    case 2: offset = Depth64KB16X(x) ^ Depth64KB16Y(y); break;
                    case 4: offset = Depth64KB32X(x) ^ Depth64KB32Y(y); break;
                    case 8: offset = Depth64KB64X(x) ^ Depth64KB64Y(y); break;
                    default: return false;
                }

                break;
            default:
                return false;
        }

        if (offset >= layout.BlockSize || offset % bytes != 0)
        {
            return false;
        }

        byteOffset = offset;
        return true;
    }

    public static bool TryGetBlockXor(in TileBlockLayout layout, uint blockX, uint blockY, out uint byteOffset) =>
        TryGetBlockXor(layout, blockX, blockY, 0, out byteOffset);

    // The per-block address bits that the 64 KiB render-target and depth kinds fold into each element.
    public static bool TryGetBlockXor(in TileBlockLayout layout, uint blockX, uint blockY, uint blockZ, out uint byteOffset)
    {
        byteOffset = 0;
        if (!MatchesKnownLayout(layout))
        {
            return false;
        }

        if (layout.Kind == TileBlockKind.Depth64KB && layout.BytesPerElement == 8)
        {
            if (blockX > uint.MaxValue / layout.BlockWidth || blockY > uint.MaxValue / layout.BlockHeight)
            {
                return false;
            }

            byteOffset = Depth64KB64X(blockX * layout.BlockWidth) ^ Depth64KB64Y(blockY * layout.BlockHeight);
        }
        else if (layout.Kind == TileBlockKind.RenderTarget64KB)
        {
            if (blockX > uint.MaxValue / layout.BlockWidth || blockY > uint.MaxValue / layout.BlockHeight || layout.BytesPerElement > 16)
            {
                return false;
            }

            byteOffset = RenderTargetOffset(blockX * layout.BlockWidth, blockY * layout.BlockHeight, layout.BytesPerElement);
        }

        if (layout.Kind is TileBlockKind.RenderTarget64KB or TileBlockKind.Depth64KB)
        {
            byteOffset ^= ((blockZ & 8) << 5) ^ ((blockZ & 4) << 7) ^ ((blockZ & 2) << 9) ^ ((blockZ & 1) << 11);
        }

        return byteOffset < layout.BlockSize && byteOffset % layout.BytesPerElement == 0;
    }
}
