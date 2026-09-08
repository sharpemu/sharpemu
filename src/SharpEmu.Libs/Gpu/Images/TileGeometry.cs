// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Images;

public readonly record struct TileSizeAndAlignment(uint Size, uint Align);

// One mip level with 32-bit offsets and sizes: linear span, tiled span and tail location.
public struct TileLevelSpan
{
    public uint Size;
    public uint Offset;
    public uint SourceSize;
    public uint SourceOffset;
    public uint X;
    public uint Y;
}

public readonly record struct TilePaddedSize(uint Width, uint Height);

public enum TileBlockKind : uint
{
    Standard256B,
    Standard4KB,
    Standard4KB3D,
    Standard64KB,
    Standard64KB3D,
    Prt64KB,
    Prt64KB3D,
    RenderTarget64KB,
    Depth64KB,
}

public readonly record struct TileBlockLayout(TileBlockKind Kind, uint BytesPerElement, uint BlockSize, uint BlockWidth, uint BlockHeight, uint BlockDepth)
{
    public bool IsValid => BytesPerElement != 0;
}

public readonly record struct TileElementLayout(uint Bytes, uint TexelWidth, uint TexelHeight);

public readonly record struct TileTextureBlockLayout(TileBlockLayout Block, uint TexelWidth, uint TexelHeight);

public enum TileSurfaceDimension : uint
{
    Flat2D,
    Volume3D,
}

public readonly record struct TiledSurfaceDescription(
    GuestPixelFormat Format,
    GuestTileMode TileMode,
    TileSurfaceDimension Dimension,
    uint Width,
    uint Height,
    uint Depth = 1,
    uint Levels = 1,
    uint Layers = 1);

// Width and height count addressable elements, not texels.
public struct TiledMipLayout
{
    public ulong Offset;
    public ulong Size;
    public uint Width;
    public uint Height;
    public uint PaddedWidth;
    public uint PaddedHeight;
    public uint TailX;
    public uint TailY;
}

public sealed class TiledSurfaceLayout
{
    public const int MaxLevels = 16;

    public TiledSurfaceDescription Description;
    public TileTextureBlockLayout Texture;
    public uint FirstTailLevel;
    public ulong BlockSliceSize;
    public ulong TotalSize;
    public readonly TiledMipLayout[] Mips = new TiledMipLayout[MaxLevels];
}

// Block dimensions, tiled mip-chain placement and surface sizes of guest tile modes.
public static partial class TileGeometry
{
    private readonly record struct Log2BlockDimensions(byte Width, byte Height, byte Depth);

    // Indexed by log2 of the element size.
    private static readonly Log2BlockDimensions[] Thin256B = [new(4, 4, 0), new(4, 3, 0), new(3, 3, 0), new(3, 2, 0), new(2, 2, 0)];
    private static readonly Log2BlockDimensions[] Thin4KB = [new(6, 6, 0), new(6, 5, 0), new(5, 5, 0), new(5, 4, 0), new(4, 4, 0)];
    private static readonly Log2BlockDimensions[] Thin64KB = [new(8, 8, 0), new(8, 7, 0), new(7, 7, 0), new(7, 6, 0), new(6, 6, 0)];
    private static readonly Log2BlockDimensions[] Thick4KB = [new(4, 4, 4), new(3, 4, 4), new(3, 4, 3), new(3, 3, 3), new(2, 3, 3)];
    private static readonly Log2BlockDimensions[] Thick64KB = [new(6, 5, 5), new(5, 5, 5), new(5, 5, 4), new(5, 4, 4), new(4, 4, 4)];

    private readonly record struct BlockKindFacts(uint BlockSize, Log2BlockDimensions[] Dimensions, uint MaxBytesPerElement);

    private static readonly BlockKindFacts[] BlockKinds =
    [
        new(256, Thin256B, 16),
        new(4096, Thin4KB, 16),
        new(4096, Thick4KB, 16),
        new(65536, Thin64KB, 16),
        new(65536, Thick64KB, 16),
        new(65536, Thin64KB, 16),
        new(65536, Thick64KB, 16),
        new(65536, Thin64KB, 16),
        new(65536, Thin64KB, 8),
    ];

    private static readonly Log2BlockDimensions[][] MsaaBlocks =
    [
        [new(8, 8, 0), new(8, 7, 0), new(7, 7, 0), new(7, 6, 0), new(6, 6, 0)],
        [new(7, 8, 0), new(7, 7, 0), new(6, 7, 0), new(6, 6, 0), new(5, 6, 0)],
        [new(7, 7, 0), new(7, 6, 0), new(6, 6, 0), new(6, 5, 0), new(5, 5, 0)],
        [new(6, 7, 0), new(6, 6, 0), new(5, 6, 0), new(5, 5, 0), new(4, 5, 0)],
    ];

    private readonly record struct TailLocation(uint X, uint Y);

    private static readonly TailLocation[][] TailThin4KB =
    [
        [new(32, 0), new(16, 32), new(0, 48), new(0, 32), new(16, 16), new(16, 0), new(0, 16), new(0, 0)],
        [new(32, 0), new(16, 16), new(0, 24), new(0, 16), new(16, 8), new(16, 0), new(0, 8), new(0, 0)],
        [new(16, 0), new(8, 16), new(0, 24), new(0, 16), new(8, 8), new(8, 0), new(0, 8), new(0, 0)],
        [new(16, 0), new(8, 8), new(0, 12), new(0, 8), new(8, 4), new(8, 0), new(0, 4), new(0, 0)],
        [new(8, 0), new(4, 8), new(0, 12), new(0, 8), new(4, 4), new(4, 0), new(0, 4), new(0, 0)],
    ];

    private static readonly TailLocation[][] TailThin64KB =
    [
        [new(128, 0), new(0, 128), new(64, 0), new(0, 64), new(32, 0), new(16, 32), new(0, 48), new(0, 32), new(16, 16), new(16, 0), new(0, 16), new(0, 0)],
        [new(128, 0), new(0, 64), new(64, 0), new(0, 32), new(32, 0), new(16, 16), new(0, 24), new(0, 16), new(16, 8), new(16, 0), new(0, 8), new(0, 0)],
        [new(64, 0), new(0, 64), new(32, 0), new(0, 32), new(16, 0), new(8, 16), new(0, 24), new(0, 16), new(8, 8), new(8, 0), new(0, 8), new(0, 0)],
        [new(64, 0), new(0, 32), new(32, 0), new(0, 16), new(16, 0), new(8, 8), new(0, 12), new(0, 8), new(8, 4), new(8, 0), new(0, 4), new(0, 0)],
        [new(32, 0), new(0, 32), new(16, 0), new(0, 16), new(8, 0), new(4, 8), new(0, 12), new(0, 8), new(4, 4), new(4, 0), new(0, 4), new(0, 0)],
    ];

    private static readonly TailLocation[][] TailThick64KB =
    [
        [new(32, 0), new(0, 16), new(16, 0), new(8, 8), new(0, 12), new(0, 8), new(8, 4), new(8, 0), new(0, 4), new(0, 0)],
        [new(16, 0), new(0, 16), new(8, 0), new(4, 8), new(0, 12), new(0, 8), new(4, 4), new(4, 0), new(0, 4), new(0, 0)],
        [new(16, 0), new(0, 16), new(8, 0), new(4, 8), new(0, 12), new(0, 8), new(4, 4), new(4, 0), new(0, 4), new(0, 0)],
        [new(16, 0), new(0, 8), new(8, 0), new(4, 4), new(0, 6), new(0, 4), new(4, 2), new(4, 0), new(0, 2), new(0, 0)],
        [new(8, 0), new(0, 8), new(4, 0), new(2, 4), new(0, 6), new(0, 4), new(2, 2), new(2, 0), new(0, 2), new(0, 0)],
    ];

    private static readonly TailLocation[][] TailThick4KB =
    [
        [new(0, 8), new(8, 4), new(8, 0), new(0, 4), new(0, 0)],
        [new(0, 8), new(4, 4), new(4, 0), new(0, 4), new(0, 0)],
        [new(0, 8), new(4, 4), new(4, 0), new(0, 4), new(0, 0)],
        [new(0, 4), new(4, 2), new(4, 0), new(0, 2), new(0, 0)],
        [new(0, 4), new(2, 2), new(2, 0), new(0, 2), new(0, 0)],
    ];

    private readonly record struct MipTailRule(TailLocation[] Locations, uint WidthLimit, uint HeightLimit)
    {
        public uint MaxLevels => (uint)Locations.Length;
    }

    private static uint AlignUp(uint value, uint alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static uint ShiftCeil(uint value, uint shift) => (uint)(((ulong)value + (1UL << (int)shift) - 1) >> (int)shift);

    private static uint LinearBlockWidth(uint bytesPerElement) => 256 / bytesPerElement;

    private static int Log2(uint value) => BitOperations.TrailingZeroCount(value);

    private static uint LinearAlignedLevelPitch(uint baseWidth, uint baseHeight, uint level, uint bytesPerElement, out uint paddedHeight, out uint levelSize)
    {
        var levelWidth = ShiftCeil(baseWidth, level);
        var levelHeight = ShiftCeil(baseHeight, level);
        var paddedWidth = AlignUp(Math.Max(levelWidth, 1), LinearBlockWidth(bytesPerElement));
        var size = (ulong)paddedWidth * Math.Max(levelHeight, 1) * bytesPerElement;
        if (size > uint.MaxValue)
        {
            throw SubmissionScheduler.Fatal($"A linear mip level is too large: level={level} size={size}.");
        }

        paddedHeight = Math.Max(levelHeight, 1);
        levelSize = (uint)size;
        return paddedWidth;
    }

    // Smaller mip records come first; mip 0 is last in the block slice.
    private static uint SetLinearMipChainLayout(uint levels, uint[] mipPitch, uint[] mipHeight, uint[] mipSize, TileLevelSpan[]? levelSpans, TilePaddedSize[]? paddedSizes)
    {
        uint offset = 0;
        for (var level = (int)levels - 1; level >= 0; level--)
        {
            if (levelSpans != null)
            {
                levelSpans[level].Size = mipSize[level];
                levelSpans[level].Offset = offset;
            }

            if (paddedSizes != null)
            {
                paddedSizes[level] = new TilePaddedSize(mipPitch[level], mipHeight[level]);
            }

            offset += mipSize[level];
        }

        return AlignUp(offset, 256);
    }

    private static (uint Width, uint Height, uint Depth) BlockDimensions(Log2BlockDimensions dimensions) =>
        (1u << dimensions.Width, 1u << dimensions.Height, 1u << dimensions.Depth);

    private static bool TryGetMsaa64KBBlock(uint bytesPerElement, uint fragmentsLog2, out uint blockWidth, out uint blockHeight)
    {
        blockWidth = blockHeight = 0;
        if (fragmentsLog2 > 3 || !BitOperations.IsPow2(bytesPerElement) || bytesPerElement > 16)
        {
            return false;
        }

        (blockWidth, blockHeight, _) = BlockDimensions(MsaaBlocks[fragmentsLog2][Log2(bytesPerElement)]);
        return true;
    }

    private static bool TryGetMipTailRule(TileBlockLayout block, out MipTailRule rule)
    {
        var index = Log2(block.BytesPerElement);
        switch (block.Kind)
        {
            case TileBlockKind.Standard4KB:
                rule = new MipTailRule(TailThin4KB[index], block.BlockWidth >> 1, block.BlockHeight);
                return true;
            case TileBlockKind.Standard4KB3D:
                rule = new MipTailRule(TailThick4KB[index], block.BlockWidth, block.BlockHeight >> 1);
                return true;
            case TileBlockKind.Standard64KB3D:
            case TileBlockKind.Prt64KB3D:
                rule = new MipTailRule(TailThick64KB[index], block.BlockWidth >> 1, block.BlockHeight);
                return true;
            case TileBlockKind.Depth64KB:
                rule = block.BytesPerElement < 4
                    ? new MipTailRule(TailThin64KB[index], 64, 128)
                    : new MipTailRule(TailThin64KB[index], block.BlockWidth >> 1, block.BlockHeight);
                return true;
            case TileBlockKind.Standard64KB:
            case TileBlockKind.Prt64KB:
            case TileBlockKind.RenderTarget64KB:
                rule = new MipTailRule(TailThin64KB[index], block.BlockWidth >> 1, block.BlockHeight);
                return true;
            default:
                rule = default;
                return false;
        }
    }

    public static bool TryGetElementLayout(GuestPixelFormat format, out TileElementLayout layout)
    {
        var bytes = GuestPixelFormats.BytesPerElement(format);
        if (BitOperations.IsPow2(bytes))
        {
            layout = new TileElementLayout(bytes, 1, 1);
            return true;
        }

        var blockBytes = GuestPixelFormats.BlockCompressedBytes(format);
        if (blockBytes != 0)
        {
            layout = new TileElementLayout(blockBytes, 4, 4);
            return true;
        }

        layout = default;
        return false;
    }

    public static bool TryGetTextureBlockLayout(GuestPixelFormat format, GuestTileMode tile, bool volume, out TileTextureBlockLayout layout)
    {
        layout = default;
        TileBlockKind kind;
        switch (tile)
        {
            case GuestTileMode.Standard256B when !volume:
                kind = TileBlockKind.Standard256B;
                break;
            case GuestTileMode.Standard4KB:
                kind = volume ? TileBlockKind.Standard4KB3D : TileBlockKind.Standard4KB;
                break;
            case GuestTileMode.Standard64KB:
                kind = volume ? TileBlockKind.Standard64KB3D : TileBlockKind.Standard64KB;
                break;
            case GuestTileMode.Prt:
                kind = volume ? TileBlockKind.Prt64KB3D : TileBlockKind.Prt64KB;
                break;
            case GuestTileMode.Depth:
                kind = TileBlockKind.Depth64KB;
                break;
            case GuestTileMode.RenderTarget:
                kind = TileBlockKind.RenderTarget64KB;
                break;
            default:
                return false;
        }

        if (!TryGetElementLayout(format, out var element))
        {
            return false;
        }

        if (kind is TileBlockKind.Depth64KB or TileBlockKind.RenderTarget64KB &&
            GuestPixelFormats.RenderTargetBytesPerElement(format) != element.Bytes)
        {
            return false;
        }

        if (!TryGetBlockLayout(kind, element.Bytes, out var block))
        {
            return false;
        }

        layout = new TileTextureBlockLayout(block, element.TexelWidth, element.TexelHeight);
        return true;
    }

    public static bool TryGetTiledTextureLayout(in TiledSurfaceDescription description, out TiledSurfaceLayout layout)
    {
        layout = null!;
        var volume = description.Dimension == TileSurfaceDimension.Volume3D;
        if (description.Dimension > TileSurfaceDimension.Volume3D ||
            description.Width == 0 || description.Height == 0 || description.Depth == 0 ||
            description.Levels == 0 || description.Levels > TiledSurfaceLayout.MaxLevels || description.Layers == 0 ||
            (volume ? description.Layers != 1 : description.Depth != 1))
        {
            return false;
        }

        if (!TryGetTextureBlockLayout(description.Format, description.TileMode, volume, out var texture))
        {
            return false;
        }

        var block = texture.Block;
        var width0 = (description.Width + texture.TexelWidth - 1) / texture.TexelWidth;
        var height0 = (description.Height + texture.TexelHeight - 1) / texture.TexelHeight;

        var result = new TiledSurfaceLayout
        {
            Description = description,
            Texture = texture,
            FirstTailLevel = description.Levels,
        };
        var blockSlices = volume ? ShiftCeil(description.Depth, (uint)Log2(block.BlockDepth)) : description.Layers;

        var hasTail = TryGetMipTailRule(block, out var tail);
        if (hasTail && description.Levels > 1)
        {
            for (uint level = 0; level < description.Levels; level++)
            {
                if (ShiftCeil(width0, level) <= tail.WidthLimit &&
                    ShiftCeil(height0, level) <= tail.HeightLimit &&
                    description.Levels - level <= tail.MaxLevels)
                {
                    result.FirstTailLevel = level;
                    break;
                }
            }
        }

        for (uint level = 0; level < result.FirstTailLevel; level++)
        {
            ref var mip = ref result.Mips[level];
            mip.Width = Math.Max(((description.Width >> (int)level) + texture.TexelWidth - 1) / texture.TexelWidth, 1);
            mip.Height = Math.Max(((description.Height >> (int)level) + texture.TexelHeight - 1) / texture.TexelHeight, 1);
            mip.PaddedWidth = AlignUp(Math.Max(ShiftCeil(width0, level), 1), block.BlockWidth);
            mip.PaddedHeight = AlignUp(Math.Max(ShiftCeil(height0, level), 1), block.BlockHeight);
            mip.Size = (ulong)block.BlockDepth * mip.PaddedWidth * mip.PaddedHeight * block.BytesPerElement;
            result.BlockSliceSize += mip.Size;
        }

        if (result.FirstTailLevel < description.Levels)
        {
            result.BlockSliceSize += block.BlockSize;
        }

        for (var level = result.FirstTailLevel; level < description.Levels; level++)
        {
            ref var mip = ref result.Mips[level];
            mip.Width = Math.Max(((description.Width >> (int)level) + texture.TexelWidth - 1) / texture.TexelWidth, 1);
            mip.Height = Math.Max(((description.Height >> (int)level) + texture.TexelHeight - 1) / texture.TexelHeight, 1);
            mip.PaddedWidth = block.BlockWidth;
            mip.PaddedHeight = block.BlockHeight;
            mip.Size = block.BlockSize;
            var location = tail.Locations[level - result.FirstTailLevel];
            mip.TailX = location.X;
            mip.TailY = location.Y;
        }

        var offset = result.FirstTailLevel < description.Levels ? block.BlockSize : 0UL;
        for (var level = (int)result.FirstTailLevel - 1; level >= 0; level--)
        {
            result.Mips[level].Offset = offset;
            offset += result.Mips[level].Size;
        }

        if (offset != result.BlockSliceSize || result.BlockSliceSize > ulong.MaxValue / blockSlices)
        {
            return false;
        }

        result.TotalSize = result.BlockSliceSize * blockSlices;
        layout = result;
        return true;
    }

    private static void Set32BitTiledMipLayout(TiledSurfaceLayout layout, out TileSizeAndAlignment totalSize, TileLevelSpan[]? levelSpans, TilePaddedSize[]? paddedSizes)
    {
        if (layout.BlockSliceSize > uint.MaxValue)
        {
            throw SubmissionScheduler.Fatal($"A tiled block slice exceeds the 32-bit size limit: size={layout.BlockSliceSize}.");
        }

        var block = layout.Texture.Block;
        totalSize = new TileSizeAndAlignment((uint)layout.BlockSliceSize, block.BlockSize);

        ulong linearTailOffset = 0;
        for (uint level = 0; level < layout.Description.Levels; level++)
        {
            ref var mip = ref layout.Mips[level];
            var tail = level >= layout.FirstTailLevel;
            if (mip.Offset > uint.MaxValue || mip.Size > uint.MaxValue)
            {
                throw SubmissionScheduler.Fatal($"A tiled mip level exceeds the 32-bit offset or size limit: level={level} offset={mip.Offset} size={mip.Size}.");
            }

            if (levelSpans != null)
            {
                if (tail)
                {
                    var linearSize = (ulong)mip.Width * mip.Height * block.BytesPerElement;
                    if (linearSize > uint.MaxValue || linearTailOffset > uint.MaxValue)
                    {
                        throw SubmissionScheduler.Fatal($"A mip-tail level exceeds the 32-bit offset or size limit: level={level} size={linearSize} offset={linearTailOffset}.");
                    }

                    levelSpans[level] = new TileLevelSpan
                    {
                        Size = (uint)linearSize,
                        Offset = (uint)linearTailOffset,
                        SourceSize = (uint)mip.Size,
                        SourceOffset = (uint)mip.Offset,
                        X = mip.TailX,
                        Y = mip.TailY,
                    };
                    linearTailOffset += linearSize;
                }
                else if (block.Kind == TileBlockKind.Standard256B)
                {
                    levelSpans[level] = new TileLevelSpan { Size = (uint)mip.Size, Offset = (uint)mip.Offset };
                }
                else
                {
                    levelSpans[level] = new TileLevelSpan
                    {
                        Size = (uint)mip.Size,
                        Offset = (uint)mip.Offset,
                        SourceSize = (uint)mip.Size,
                        SourceOffset = (uint)mip.Offset,
                    };
                }
            }

            if (paddedSizes != null)
            {
                paddedSizes[level] = new TilePaddedSize(mip.PaddedWidth * layout.Texture.TexelWidth, mip.PaddedHeight * layout.Texture.TexelHeight);
            }
        }
    }

    public static bool TryGetBlockLayout(TileBlockKind kind, uint bytesPerElement, out TileBlockLayout layout)
    {
        layout = default;
        if ((uint)kind >= BlockKinds.Length || !BitOperations.IsPow2(bytesPerElement))
        {
            return false;
        }

        var facts = BlockKinds[(int)kind];
        if (bytesPerElement > facts.MaxBytesPerElement)
        {
            return false;
        }

        var (width, height, depth) = BlockDimensions(facts.Dimensions[Log2(bytesPerElement)]);
        if ((ulong)width * height * depth * bytesPerElement != facts.BlockSize)
        {
            return false;
        }

        layout = new TileBlockLayout(kind, bytesPerElement, facts.BlockSize, width, height, depth);
        return true;
    }

    private static bool TryGetHtileSize(uint width, uint height, out TileSizeAndAlignment htileSize)
    {
        htileSize = default;
        if (width == 0 || width > 16384 || height == 0 || height > 16384)
        {
            return false;
        }

        // HTile keeps one dword per depth tile; 32 KiB blocks cover 1024x512 pixels.
        var size = (ulong)(AlignUp(width, 1024) / 1024) * (AlignUp(height, 512) / 512) * 32768;
        if (size == 0 || size > uint.MaxValue)
        {
            return false;
        }

        htileSize = new TileSizeAndAlignment((uint)size, 32768);
        return true;
    }

    // Uncompressed depth and stencil are independent 64 KiB block surfaces.
    public static bool TryGetDepthSize(
        uint width,
        uint height,
        uint pitch,
        GuestDepthFormat depthFormat,
        GuestStencilFormat stencilFormat,
        bool htile,
        out TileSizeAndAlignment stencilSize,
        out TileSizeAndAlignment htileSize,
        out TileSizeAndAlignment depthSize,
        uint fragmentsLog2 = 0)
    {
        if (pitch != 0)
        {
            throw SubmissionScheduler.Fatal($"A depth surface size query must not carry a pitch: pitch={pitch}.");
        }

        depthSize = htileSize = stencilSize = default;
        if (width == 0 || width > 16384 || height == 0 || height > 16384 ||
            depthFormat is not (GuestDepthFormat.Z16 or GuestDepthFormat.Z32Float) ||
            stencilFormat is not (GuestStencilFormat.Invalid or GuestStencilFormat.Stencil8UInt) ||
            fragmentsLog2 > 3)
        {
            return false;
        }

        var depthBytes = depthFormat == GuestDepthFormat.Z16 ? 2u : 4u;
        uint stencilBlockWidth = 0, stencilBlockHeight = 0;
        var validBlocks = TryGetMsaa64KBBlock(depthBytes, fragmentsLog2, out var depthBlockWidth, out var depthBlockHeight) &&
                          (stencilFormat == GuestStencilFormat.Invalid || TryGetMsaa64KBBlock(1, fragmentsLog2, out stencilBlockWidth, out stencilBlockHeight));
        var fragments = 1u << (int)fragmentsLog2;
        var depthBytesTotal = validBlocks
            ? (ulong)AlignUp(width, depthBlockWidth) * AlignUp(height, depthBlockHeight) * depthBytes * fragments
            : 0;
        var stencilBytesTotal = stencilFormat == GuestStencilFormat.Stencil8UInt && validBlocks
            ? (ulong)AlignUp(width, stencilBlockWidth) * AlignUp(height, stencilBlockHeight) * fragments
            : 0;
        var htileValid = !htile || TryGetHtileSize(width, height, out htileSize);
        if (depthBytesTotal > uint.MaxValue || stencilBytesTotal > uint.MaxValue || !htileValid)
        {
            depthSize = htileSize = stencilSize = default;
            return false;
        }

        depthSize = new TileSizeAndAlignment((uint)depthBytesTotal, 65536);
        stencilSize = stencilFormat == GuestStencilFormat.Stencil8UInt ? new TileSizeAndAlignment((uint)stencilBytesTotal, 65536) : default;
        return true;
    }

    public static uint RenderTargetPitch(uint width, uint bytesPerElement, uint fragmentsLog2 = 0)
    {
        if (width == 0 || !TryGetMsaa64KBBlock(bytesPerElement, fragmentsLog2, out var blockWidth, out _))
        {
            return 0;
        }

        var pitch = ((ulong)width + blockWidth - 1) & ~(ulong)(blockWidth - 1);
        return pitch <= uint.MaxValue ? (uint)pitch : 0;
    }

    public static uint DepthPitch(uint width, uint bytesPerElement, uint fragmentsLog2 = 0) =>
        RenderTargetPitch(width, bytesPerElement, fragmentsLog2);

    public static bool TryGetRenderTargetSize(uint width, uint height, uint pitch, uint bytesPerElement, out TileSizeAndAlignment totalSize, uint fragmentsLog2 = 0)
    {
        totalSize = default;
        if (height == 0 || pitch == 0 ||
            !TryGetMsaa64KBBlock(bytesPerElement, fragmentsLog2, out _, out var blockHeight) ||
            pitch != RenderTargetPitch(width, bytesPerElement, fragmentsLog2))
        {
            return false;
        }

        var paddedHeight = ((ulong)height + blockHeight - 1) & ~(ulong)(blockHeight - 1);
        var size = pitch * paddedHeight * bytesPerElement * (1u << (int)fragmentsLog2);
        if (size == 0 || size > uint.MaxValue)
        {
            return false;
        }

        totalSize = new TileSizeAndAlignment((uint)size, 65536);
        return true;
    }

    public static bool TryGetRenderTargetMipLayout(uint width, uint height, uint pitch, uint bytesPerElement, uint levels, out TileSizeAndAlignment totalSize, TileLevelSpan[]? levelSpans, TilePaddedSize[]? paddedSizes)
    {
        totalSize = default;
        if (width == 0 || height == 0 || levels == 0 || levels > TiledSurfaceLayout.MaxLevels || pitch != RenderTargetPitch(width, bytesPerElement))
        {
            return false;
        }

        uint maxLevels = 1;
        var maxDimension = Math.Max(width, height);
        while (maxDimension > 1)
        {
            maxDimension >>= 1;
            maxLevels++;
        }

        if (levels > maxLevels)
        {
            return false;
        }

        var format = bytesPerElement switch
        {
            1 => GuestPixelFormat.Bits8UNorm,
            2 => GuestPixelFormat.Bits16UNorm,
            4 => GuestPixelFormat.Bits32Float,
            8 => GuestPixelFormat.Bits16_16_16_16Float,
            16 => GuestPixelFormat.Bits32_32_32_32Float,
            _ => GuestPixelFormat.Invalid,
        };
        if (format == GuestPixelFormat.Invalid)
        {
            return false;
        }

        return TryGetTextureSize(format, width, height, levels, GuestTileMode.RenderTarget, out totalSize, levelSpans, paddedSizes) &&
               totalSize.Size != 0 && totalSize.Align == 65536;
    }

    // False when a tiled layout cannot be computed; linear layouts always succeed.
    public static bool TryGetTextureSize(GuestPixelFormat format, uint width, uint height, uint levels, GuestTileMode tile, out TileSizeAndAlignment totalSize, TileLevelSpan[]? levelSpans, TilePaddedSize[]? paddedSizes)
    {
        if (levels == 0 || levels > TiledSurfaceLayout.MaxLevels)
        {
            throw SubmissionScheduler.Fatal($"The mip level count is out of range: levels={levels}.");
        }

        totalSize = default;
        if (tile == GuestTileMode.Linear && TryGetElementLayout(format, out var element))
        {
            var mipPitch = new uint[TiledSurfaceLayout.MaxLevels];
            var mipHeight = new uint[TiledSurfaceLayout.MaxLevels];
            var mipSize = new uint[TiledSurfaceLayout.MaxLevels];
            var elementsWidth0 = Math.Max((width + element.TexelWidth - 1) / element.TexelWidth, 1);
            var elementsHeight0 = Math.Max((height + element.TexelHeight - 1) / element.TexelHeight, 1);
            var compressed = element.TexelWidth != 1 || element.TexelHeight != 1;
            for (uint level = 0; level < levels; level++)
            {
                var alignedElementsWidth = LinearAlignedLevelPitch(elementsWidth0, elementsHeight0, level, element.Bytes, out var paddedElementsHeight, out mipSize[level]);
                mipPitch[level] = alignedElementsWidth * element.TexelWidth;
                mipHeight[level] = paddedElementsHeight * element.TexelHeight;
                if (compressed)
                {
                    mipPitch[level] = Math.Max(mipPitch[level], 32);
                    mipHeight[level] = Math.Max(mipHeight[level], 32);
                }
            }

            var total = SetLinearMipChainLayout(levels, mipPitch, mipHeight, mipSize, levelSpans, paddedSizes);
            totalSize = new TileSizeAndAlignment(total, 256);
            return true;
        }

        var description = new TiledSurfaceDescription(format, tile, TileSurfaceDimension.Flat2D, width, height, 1, levels, 1);
        if (!TryGetTiledTextureLayout(description, out var layout))
        {
            return false;
        }

        Set32BitTiledMipLayout(layout, out totalSize, levelSpans, paddedSizes);
        return true;
    }

    public static TileSizeAndAlignment TextureTotalSize(GuestPixelFormat format, uint width, uint height, uint depth, uint levels, GuestTileMode tile, bool volume)
    {
        if (depth == 0)
        {
            throw SubmissionScheduler.Fatal("A texture size query needs a depth of at least one.");
        }

        if (volume && tile != GuestTileMode.Linear)
        {
            var description = new TiledSurfaceDescription(format, tile, TileSurfaceDimension.Volume3D, width, height, depth, levels, 1);
            if (!TryGetTiledTextureLayout(description, out var layout))
            {
                throw SubmissionScheduler.Fatal($"The 3D texture layout is not supported: format={(uint)format} tile={(uint)tile} extent={width}x{height}x{depth} levels={levels}.");
            }

            if (layout.TotalSize > uint.MaxValue)
            {
                throw SubmissionScheduler.Fatal($"The 3D texture is too large: size={layout.TotalSize}.");
            }

            return new TileSizeAndAlignment((uint)layout.TotalSize, layout.Texture.Block.BlockSize);
        }

        if (!TryGetTextureSize(format, width, height, levels, tile, out var sliceSize, null, null))
        {
            throw SubmissionScheduler.Fatal($"The texture layout is not supported: format={(uint)format} width={width} height={height} levels={levels} tile={(uint)tile}.");
        }

        var total = (ulong)sliceSize.Size * depth;
        if (total > uint.MaxValue)
        {
            throw SubmissionScheduler.Fatal($"The layered texture is too large: size={total}.");
        }

        return new TileSizeAndAlignment((uint)total, sliceSize.Align);
    }

    public static uint TexturePitch(GuestPixelFormat format, uint width, GuestTileMode tile)
    {
        var pitch = width;
        switch (tile)
        {
            case GuestTileMode.Linear:
                if (TryGetElementLayout(format, out var element) && element.TexelWidth == 1 && element.TexelHeight == 1)
                {
                    pitch = AlignUp(pitch, LinearBlockWidth(element.Bytes));
                }

                break;
            case GuestTileMode.Standard4KB:
            case GuestTileMode.Standard64KB:
            case GuestTileMode.Prt:
            case GuestTileMode.Depth:
            case GuestTileMode.RenderTarget:
                if (TryGetTextureBlockLayout(format, tile, false, out var layout))
                {
                    pitch = AlignUp(pitch, layout.Block.BlockWidth * layout.TexelWidth);
                }

                break;
        }

        return pitch;
    }
}
