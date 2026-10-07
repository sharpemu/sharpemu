// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class TileGeometryTests
{
    private static uint DepthOffset(uint bytes, uint x, uint y)
    {
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Depth64KB, bytes, out var layout));
        Assert.True(TileGeometry.TryGetBlockOffset(layout, x, y, 0, out var offset));
        return offset;
    }

    [Fact]
    public void DepthBlockOffsets_MatchTheReferenceSpotValues()
    {
        Assert.Equal(0x0004u, DepthOffset(1, 2, 0));
        Assert.Equal(0x0002u, DepthOffset(1, 0, 1));
        Assert.Equal(0x0027u, DepthOffset(1, 3, 5));
        Assert.Equal(0x0008u, DepthOffset(2, 2, 0));
        Assert.Equal(0x0004u, DepthOffset(2, 0, 1));
        Assert.Equal(0x004eu, DepthOffset(2, 3, 5));
        Assert.Equal(0x0010u, DepthOffset(4, 2, 0));
        Assert.Equal(0x0008u, DepthOffset(4, 0, 1));
        Assert.Equal(0x009cu, DepthOffset(4, 3, 5));
    }

    [Theory]
    [InlineData(TileBlockKind.Standard256B, 256u, 16u)]
    [InlineData(TileBlockKind.Standard4KB, 4096u, 16u)]
    [InlineData(TileBlockKind.Standard4KB3D, 4096u, 16u)]
    [InlineData(TileBlockKind.Standard64KB, 65536u, 16u)]
    [InlineData(TileBlockKind.Standard64KB3D, 65536u, 16u)]
    [InlineData(TileBlockKind.Prt64KB, 65536u, 16u)]
    [InlineData(TileBlockKind.Prt64KB3D, 65536u, 16u)]
    [InlineData(TileBlockKind.RenderTarget64KB, 65536u, 16u)]
    [InlineData(TileBlockKind.Depth64KB, 65536u, 8u)]
    public void BlockLayouts_CoverEveryElementSize(TileBlockKind kind, uint blockSize, uint maxBytes)
    {
        for (uint bytes = 1; bytes <= 16; bytes *= 2)
        {
            var valid = TileGeometry.TryGetBlockLayout(kind, bytes, out var layout);
            Assert.Equal(bytes <= maxBytes, valid);
            if (!valid)
            {
                continue;
            }

            Assert.Equal(kind, layout.Kind);
            Assert.Equal(blockSize, layout.BlockSize);
            Assert.Equal(blockSize, layout.BlockWidth * layout.BlockHeight * layout.BlockDepth * bytes);
            Assert.Equal(kind is TileBlockKind.Standard4KB3D or TileBlockKind.Standard64KB3D or TileBlockKind.Prt64KB3D, layout.BlockDepth > 1);
        }

        Assert.False(TileGeometry.TryGetBlockLayout(kind, 3, out _));
        Assert.False(TileGeometry.TryGetBlockLayout(kind, 0, out _));
    }

    [Fact]
    public void ThickBlockShapes_MatchTheShaderExtents()
    {
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Standard4KB3D, 1, out var thin1));
        Assert.Equal((16u, 16u, 16u), (thin1.BlockWidth, thin1.BlockHeight, thin1.BlockDepth));
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Standard4KB3D, 4, out var thin4));
        Assert.Equal((8u, 16u, 8u), (thin4.BlockWidth, thin4.BlockHeight, thin4.BlockDepth));
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Standard64KB3D, 16, out var thick16));
        Assert.Equal((16u, 16u, 16u), (thick16.BlockWidth, thick16.BlockHeight, thick16.BlockDepth));
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Standard64KB, 4, out var flat4));
        Assert.Equal((128u, 128u, 1u), (flat4.BlockWidth, flat4.BlockHeight, flat4.BlockDepth));
        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Standard256B, 2, out var small2));
        Assert.Equal((16u, 8u, 1u), (small2.BlockWidth, small2.BlockHeight, small2.BlockDepth));
    }

    [Fact]
    public void BlockOffsets_StayInsideTheBlockAndAreUniquePerElement()
    {
        foreach (var kind in Enum.GetValues<TileBlockKind>())
        {
            for (uint bytes = 1; bytes <= 16; bytes *= 2)
            {
                if (!TileGeometry.TryGetBlockLayout(kind, bytes, out var layout))
                {
                    continue;
                }

                var seen = new HashSet<uint>();
                for (uint z = 0; z < layout.BlockDepth; z++)
                {
                    for (uint y = 0; y < layout.BlockHeight; y++)
                    {
                        for (uint x = 0; x < layout.BlockWidth; x++)
                        {
                            Assert.True(TileGeometry.TryGetBlockOffset(layout, x, y, z, out var offset));
                            Assert.True(offset % bytes == 0);
                            Assert.True(offset + bytes <= layout.BlockSize);
                            Assert.True(seen.Add(offset), $"{kind} bytes={bytes} ({x},{y},{z}) repeats offset {offset}");
                        }
                    }
                }

                Assert.Equal((int)layout.BlockSize / (int)bytes, seen.Count);
            }
        }
    }

    [Fact]
    public void MipTail_StartsWhereTheLevelFitsAndOffsetsRunBackwards()
    {
        var description = new TiledSurfaceDescription(GuestPixelFormat.Bits8_8_8_8UNorm, GuestTileMode.Standard64KB, TileSurfaceDimension.Flat2D, 256, 256, 1, 9, 1);
        Assert.True(TileGeometry.TryGetTiledTextureLayout(description, out var layout));
        Assert.Equal(2u, layout.FirstTailLevel);
        Assert.Equal(393216UL, layout.BlockSliceSize);
        Assert.Equal(393216UL, layout.TotalSize);

        Assert.Equal(131072UL, layout.Mips[0].Offset);
        Assert.Equal(262144UL, layout.Mips[0].Size);
        Assert.Equal((256u, 256u), (layout.Mips[0].PaddedWidth, layout.Mips[0].PaddedHeight));
        Assert.Equal(65536UL, layout.Mips[1].Offset);
        Assert.Equal(65536UL, layout.Mips[1].Size);
        for (var level = 2; level < 9; level++)
        {
            Assert.Equal(0UL, layout.Mips[level].Offset);
            Assert.Equal(65536UL, layout.Mips[level].Size);
            Assert.Equal((128u, 128u), (layout.Mips[level].PaddedWidth, layout.Mips[level].PaddedHeight));
        }

        Assert.Equal((64u, 0u), (layout.Mips[2].TailX, layout.Mips[2].TailY));
        Assert.Equal((0u, 64u), (layout.Mips[3].TailX, layout.Mips[3].TailY));
        Assert.Equal((32u, 0u), (layout.Mips[4].TailX, layout.Mips[4].TailY));
        Assert.Equal((0u, 24u), (layout.Mips[8].TailX, layout.Mips[8].TailY));
        Assert.Equal((1u, 1u), (layout.Mips[8].Width, layout.Mips[8].Height));
    }

    [Fact]
    public void LayeredAndVolumeLayouts_ScaleTheBlockSlice()
    {
        var layered = new TiledSurfaceDescription(GuestPixelFormat.Bits8_8_8_8UNorm, GuestTileMode.Standard64KB, TileSurfaceDimension.Flat2D, 128, 128, 1, 1, 6);
        Assert.True(TileGeometry.TryGetTiledTextureLayout(layered, out var layeredLayout));
        Assert.Equal(65536UL, layeredLayout.BlockSliceSize);
        Assert.Equal(6 * 65536UL, layeredLayout.TotalSize);

        var volume = new TiledSurfaceDescription(GuestPixelFormat.Bits8_8_8_8UNorm, GuestTileMode.Standard64KB, TileSurfaceDimension.Volume3D, 32, 32, 40, 1, 1);
        Assert.True(TileGeometry.TryGetTiledTextureLayout(volume, out var volumeLayout));
        Assert.Equal(TileBlockKind.Standard64KB3D, volumeLayout.Texture.Block.Kind);
        Assert.Equal(65536UL, volumeLayout.BlockSliceSize);
        Assert.Equal(3 * 65536UL, volumeLayout.TotalSize);

        Assert.False(TileGeometry.TryGetTiledTextureLayout(volume with { Layers = 2 }, out _));
        Assert.False(TileGeometry.TryGetTiledTextureLayout(layered with { Depth = 2 }, out _));
        Assert.False(TileGeometry.TryGetTiledTextureLayout(layered with { Levels = 17 }, out _));
        Assert.False(TileGeometry.TryGetTiledTextureLayout(layered with { TileMode = GuestTileMode.Linear }, out _));
    }

    [Fact]
    public void TextureBlockLayouts_FollowTileModeAndDimension()
    {
        Assert.False(TileGeometry.TryGetTextureBlockLayout(GuestPixelFormat.Bits8_8_8_8UNorm, GuestTileMode.Standard256B, volume: true, out _));
        Assert.True(TileGeometry.TryGetTextureBlockLayout(GuestPixelFormat.Bits8_8_8_8UNorm, GuestTileMode.Standard256B, volume: false, out var small));
        Assert.Equal(TileBlockKind.Standard256B, small.Block.Kind);
        Assert.True(TileGeometry.TryGetTextureBlockLayout(GuestPixelFormat.Bits8_8_8_8UNorm, GuestTileMode.Prt, volume: true, out var prt));
        Assert.Equal(TileBlockKind.Prt64KB3D, prt.Block.Kind);
        Assert.True(TileGeometry.TryGetTextureBlockLayout(GuestPixelFormat.Bits8_8_8_8UNorm, GuestTileMode.Depth, volume: false, out var depth));
        Assert.Equal(TileBlockKind.Depth64KB, depth.Block.Kind);
        Assert.True(TileGeometry.TryGetTextureBlockLayout(GuestPixelFormat.Bc1UNorm, GuestTileMode.Standard64KB, volume: false, out var compressed));
        Assert.Equal((4u, 4u, 8u), (compressed.TexelWidth, compressed.TexelHeight, compressed.Block.BytesPerElement));
        Assert.False(TileGeometry.TryGetTextureBlockLayout(GuestPixelFormat.Bc1UNorm, GuestTileMode.RenderTarget, volume: false, out _));
        Assert.False(TileGeometry.TryGetTextureBlockLayout(GuestPixelFormat.Invalid, GuestTileMode.Standard64KB, volume: false, out _));
    }

    [Fact]
    public void LinearTextureSize_PadsRowsToTheLinearBlockWidth()
    {
        var spans = new TileLevelSpan[TiledSurfaceLayout.MaxLevels];
        var padded = new TilePaddedSize[TiledSurfaceLayout.MaxLevels];
        Assert.True(TileGeometry.TryGetTextureSize(GuestPixelFormat.Bits8_8_8_8UNorm, 100, 100, 1, GuestTileMode.Linear, out var total, spans, padded));
        Assert.Equal(new TileSizeAndAlignment(51200, 256), total);
        Assert.Equal(new TilePaddedSize(128, 100), padded[0]);
        Assert.Equal((0u, 51200u), (spans[0].Offset, spans[0].Size));
        Assert.Equal(128u, TileGeometry.TexturePitch(GuestPixelFormat.Bits8_8_8_8UNorm, 100, GuestTileMode.Linear));

        Assert.True(TileGeometry.TryGetTextureSize(GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, 2, GuestTileMode.Linear, out total, spans, padded));
        Assert.Equal(24576u, total.Size);
        Assert.Equal((8192u, 16384u), (spans[0].Offset, spans[0].Size));
        Assert.Equal((0u, 8192u), (spans[1].Offset, spans[1].Size));
        Assert.Equal(new TilePaddedSize(64, 32), padded[1]);
    }

    [Fact]
    public void TiledTextureSize_Exposes32BitLevelSpans()
    {
        var spans = new TileLevelSpan[TiledSurfaceLayout.MaxLevels];
        var padded = new TilePaddedSize[TiledSurfaceLayout.MaxLevels];
        Assert.True(TileGeometry.TryGetTextureSize(GuestPixelFormat.Bits8_8_8_8UNorm, 256, 256, 9, GuestTileMode.Standard64KB, out var total, spans, padded));
        Assert.Equal(new TileSizeAndAlignment(393216, 65536), total);
        Assert.Equal((131072u, 262144u, 131072u, 262144u), (spans[0].Offset, spans[0].Size, spans[0].SourceOffset, spans[0].SourceSize));
        Assert.Equal((0u, 16384u, 0u, 65536u, 64u, 0u), (spans[2].Offset, spans[2].Size, spans[2].SourceOffset, spans[2].SourceSize, spans[2].X, spans[2].Y));
        Assert.Equal((16384u, 4096u), (spans[3].Offset, spans[3].Size));
        Assert.Equal(new TilePaddedSize(256, 256), padded[0]);
        Assert.Equal(new TilePaddedSize(128, 128), padded[2]);

        var whole = TileGeometry.TextureTotalSize(GuestPixelFormat.Bits8_8_8_8UNorm, 256, 256, 3, 9, GuestTileMode.Standard64KB, volume: false);
        Assert.Equal(new TileSizeAndAlignment(3 * 393216, 65536), whole);
        Assert.True(TileGeometry.TryGetTextureSize(GuestPixelFormat.Bits8_8_8_8UNorm, 256, 256, 1, GuestTileMode.Standard256B, out var small, null, null));
        Assert.Equal(new TileSizeAndAlignment(256 * 256 * 4, 256), small);
        Assert.False(TileGeometry.TryGetTextureSize(GuestPixelFormat.Invalid, 256, 256, 1, GuestTileMode.Standard64KB, out _, null, null));
    }

    [Fact]
    public void RenderTargetSizes_UseSixtyFourKiBBlocks()
    {
        Assert.Equal(256u, TileGeometry.RenderTargetPitch(200, 4));
        Assert.True(TileGeometry.TryGetRenderTargetSize(200, 100, 256, 4, out var size));
        Assert.Equal(65536u, size.Align);
        Assert.Equal(0u, size.Size % 65536);
        Assert.True(size.Size >= 256 * 128 * 4);
        Assert.True(TileGeometry.TryGetDepthSize(200, 100, 0, GuestDepthFormat.Z32Float, GuestStencilFormat.Stencil8UInt, true, out var stencilSize, out var htileSize, out var depthSize));
        Assert.Equal(65536u, depthSize.Align);
        Assert.Equal(65536u, stencilSize.Align);
        Assert.Equal(new TileSizeAndAlignment(32768, 32768), htileSize);
        Assert.Equal(256u, TileGeometry.DepthPitch(200, 4));
    }
}
