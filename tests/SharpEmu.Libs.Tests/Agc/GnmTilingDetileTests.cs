// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// TryDetile's exact-XOR fast path (PS5 swizzle modes 1/5/9/24/27) factors the
// AddrLib bit-interleave into independent per-column X and per-row Y terms so
// the inner loop is one array load and one XOR instead of a 16-bit interleave.
// These tests pin that the factored output stays byte-identical to the direct
// AddrLib address equation.
public sealed class GnmTilingDetileTests
{
    [Fact]
    public void BaseMipPlacement_Depth64KbR16Chain_UsesReducedTailLimit()
    {
        var ok = GnmTiling.TryGetBaseMipPlacement(
            swizzleMode: 24,
            elementsWide: 128,
            elementsHigh: 128,
            bytesPerElement: 2,
            resourceMipLevels: 8,
            out var byteOffset,
            out var inMipTail,
            out var tailElementX,
            out var tailElementY,
            out var chainSliceBytes);

        Assert.True(ok);
        Assert.False(inMipTail);
        Assert.Equal(65_536UL, byteOffset);
        Assert.Equal(131_072UL, chainSliceBytes);
        Assert.Equal(0, tailElementX);
        Assert.Equal(0, tailElementY);
    }

    [Fact]
    public void BaseMipPlacement_Standard4KbBc1Chain_UsesThinTailLayout()
    {
        // A 2048x2048 BC1 image is a 512x512 grid of 8-byte blocks.
        // Its 12-level chain enters the 4 KiB tail at mip 5.
        var ok = GnmTiling.TryGetBaseMipPlacement(
            swizzleMode: 5,
            elementsWide: 512,
            elementsHigh: 512,
            bytesPerElement: 8,
            resourceMipLevels: 12,
            out var byteOffset,
            out var inMipTail,
            out var tailElementX,
            out var tailElementY,
            out var chainSliceBytes);

        Assert.True(ok);
        Assert.False(inMipTail);
        Assert.Equal(700_416UL, byteOffset);
        Assert.Equal(2_797_568UL, chainSliceBytes);
        Assert.Equal(0, tailElementX);
        Assert.Equal(0, tailElementY);
    }

    [Fact]
    public void MipChainPlacement_Standard4KbBc7Atlas_MapsAllSixLevels()
    {
        // A 512x512 BC7 layer is a 128x128 grid of 16-byte blocks.
        // Levels 4 and 5 share the 4 KiB mip-tail block.
        var ok = GnmTiling.TryGetMipChainPlacement(
            swizzleMode: 5,
            elementsWide: 128,
            elementsHigh: 128,
            bytesPerElement: 16,
            resourceMipLevels: 6,
            out var placements,
            out var chainSliceBytes);

        Assert.True(ok);
        Assert.Equal(352_256UL, chainSliceBytes);
        Assert.Equal(6, placements.Length);
        Assert.Equal(90_112UL, placements[0].ByteOffset);
        Assert.Equal(24_576UL, placements[1].ByteOffset);
        Assert.Equal(8_192UL, placements[2].ByteOffset);
        Assert.Equal(4_096UL, placements[3].ByteOffset);
        Assert.False(placements[3].InMipTail);
        Assert.True(placements[4].InMipTail);
        Assert.Equal((8, 0), (placements[4].TailElementX, placements[4].TailElementY));
        Assert.True(placements[5].InMipTail);
        Assert.Equal((4, 8), (placements[5].TailElementX, placements[5].TailElementY));
    }

    // Independent re-derivation of the 64 KiB RB+ R_X equation (swizzle mode 27,
    // 2 bytes/element) straight from the address-bit table, so the tiled source
    // layout does not depend on TryDetile's own internal factoring.
    private static readonly (uint XMask, uint YMask)[] RbPlus64KRenderX2Bpp =
    [
        (0, 0), (1u << 0, 0), (1u << 1, 0), (1u << 2, 0),
        (0, 1u << 0), (0, 1u << 1), (0, 1u << 2), (1u << 3, 0),
        (1u << 7, (1u << 4) | (1u << 7)), (1u << 4, 1u << 4), (1u << 6, 1u << 5), (1u << 5, 1u << 6),
        (0, 1u << 3), (1u << 6, 0), (1u << 7, 1u << 7), (1u << 8, 1u << 6),
    ];

    private static uint ReferenceOffset(uint x, uint y, (uint XMask, uint YMask)[] pattern)
    {
        uint offset = 0;
        for (var bit = 0; bit < pattern.Length; bit++)
        {
            var parity = (System.Numerics.BitOperations.PopCount(x & pattern[bit].XMask) +
                          System.Numerics.BitOperations.PopCount(y & pattern[bit].YMask)) & 1;
            offset |= (uint)parity << bit;
        }

        return offset;
    }

    [Theory]
    [InlineData(384, 200)]
    [InlineData(768, 512)]
    public void TryDetile_ExactXorMode27_MatchesReferenceAddressEquation(
        int elementsWide,
        int elementsHigh)
    {
        const uint swizzleMode = 27; // 64 KiB RB+ R_X
        const int bytesPerElement = 2;
        const int blockBytes = 65536;
        // SquareBlockDimensions(32768 elements): 15 bits split 8/7, x favored.
        const int blockWidth = 256;
        const int blockHeight = 128;
        var blocksPerRow = (elementsWide + blockWidth - 1) / blockWidth;
        var blocksPerColumn = (elementsHigh + blockHeight - 1) / blockHeight;

        // Lay out a tiled source where each element stores its own linear index,
        // placed at the byte address the AddrLib equation dictates. The tiled
        // buffer is sized by padded whole blocks (block addressing overshoots the
        // linear extent). A correct detile must recover ascending linear indices.
        var tiled = new byte[blocksPerRow * blocksPerColumn * blockBytes];
        for (var y = 0; y < elementsHigh; y++)
        {
            for (var x = 0; x < elementsWide; x++)
            {
                var blockIndex = (long)(y / blockHeight) * blocksPerRow + (x / blockWidth);
                // The equation yields a byte offset within the block (bit 0 is
                // Zero at 2bpp, keeping element writes 2-byte aligned).
                var sourceByte = (int)(blockIndex * blockBytes +
                    ReferenceOffset((uint)x, (uint)y, RbPlus64KRenderX2Bpp));
                var linearIndex = (ushort)(y * elementsWide + x);
                tiled[sourceByte] = (byte)linearIndex;
                tiled[sourceByte + 1] = (byte)(linearIndex >> 8);
            }
        }

        var linear = new byte[elementsWide * elementsHigh * bytesPerElement];
        var ok = GnmTiling.TryDetile(tiled, linear, swizzleMode, elementsWide, elementsHigh, bytesPerElement);

        Assert.True(ok);
        for (var i = 0; i < elementsWide * elementsHigh; i++)
        {
            var value = (ushort)(linear[i * 2] | (linear[i * 2 + 1] << 8));
            Assert.Equal((ushort)i, value);
        }
    }

    // Gen5 Standard256B (mode 1) uses the 8-bit AddrLib S equation, not the
    // generic StandardSwizzle bit-interleave. Pin that TryDetile recovers a
    // known linear fill placed with that equation.
    private static readonly (uint XMask, uint YMask)[] Standard256_1Bpp =
    [
        (1u << 0, 0), (1u << 1, 0), (1u << 2, 0), (1u << 3, 0),
        (0, 1u << 0), (0, 1u << 1), (0, 1u << 2), (0, 1u << 3),
    ];

    [Theory]
    [InlineData(32, 32)]
    [InlineData(64, 48)]
    public void TryDetile_ExactXorMode1_MatchesReferenceAddressEquation(
        int elementsWide,
        int elementsHigh)
    {
        const uint swizzleMode = 1; // Standard256B
        const int bytesPerElement = 1;
        const int blockBytes = 256;
        const int blockWidth = 16;
        const int blockHeight = 16;
        var blocksPerRow = (elementsWide + blockWidth - 1) / blockWidth;
        var blocksPerColumn = (elementsHigh + blockHeight - 1) / blockHeight;

        var tiled = new byte[blocksPerRow * blocksPerColumn * blockBytes];
        for (var y = 0; y < elementsHigh; y++)
        {
            for (var x = 0; x < elementsWide; x++)
            {
                var blockIndex = (long)(y / blockHeight) * blocksPerRow + (x / blockWidth);
                var sourceByte = (int)(blockIndex * blockBytes +
                    ReferenceOffset((uint)x, (uint)y, Standard256_1Bpp));
                tiled[sourceByte] = (byte)(y * elementsWide + x);
            }
        }

        var linear = new byte[elementsWide * elementsHigh * bytesPerElement];
        Assert.True(GnmTiling.TryDetile(tiled, linear, swizzleMode, elementsWide, elementsHigh, bytesPerElement));
        for (var i = 0; i < elementsWide * elementsHigh; i++)
        {
            Assert.Equal((byte)i, linear[i]);
        }
    }

    // GetDetileParams must reproduce TryDetile bit-for-bit: the CPU fallback and
    // the GPU compute kernel both consume these params, so a detile driven purely
    // by DetileParams (the shared addressing formula the kernel runs) must equal
    // the shipped CPU detile for every supported mode/bpp.
    [Theory]
    [InlineData(27u, 2, 384, 200)] // 64 KiB RB+ R_X (exact-XOR)
    [InlineData(27u, 4, 256, 256)] // 64 KiB RB+ R_X (exact-XOR)
    [InlineData(9u, 4, 300, 300)]  // 64 KiB standard (exact-XOR)
    [InlineData(24u, 4, 128, 256)] // 64 KiB RB+ Z_X (exact-XOR)
    [InlineData(5u, 4, 200, 120)]  // 4 KiB standard (exact-XOR)
    [InlineData(1u, 4, 64, 64)]    // 256 B standard (exact-XOR)
    [InlineData(8u, 4, 128, 128)]  // 64 KiB Z (block-table path)
    public void GetDetileParams_ReproducesTryDetile(uint mode, int bpp, int w, int h)
    {
        var p = GnmTiling.GetDetileParams(mode, bpp, w, h);
        Assert.True(p.IsSupported);

        // Whole-block tiled buffer (block addressing overshoots the linear extent),
        // filled with a deterministic non-trivial pattern.
        var blocksHigh = (h + p.BlockHeight - 1) / p.BlockHeight;
        var tiled = new byte[(long)p.BlocksPerRow * blocksHigh * p.BlockBytes];
        for (var i = 0; i < tiled.Length; i++)
        {
            tiled[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        var expected = new byte[w * h * bpp];
        Assert.True(GnmTiling.TryDetile(tiled, expected, mode, w, h, bpp));

        var actual = DetileViaParams(tiled, p, w, h, bpp);
        Assert.Equal(expected, actual);
    }

    // The production GnmTiling.DetileWithParams (the active CPU fallback used by
    // the Metal path under default-on GPU detile) must equal TryDetile for every
    // supported mode/bpp — same DetileParams addressing, no re-derived swizzle.
    [Theory]
    [InlineData(27u, 2, 384, 200)]
    [InlineData(27u, 4, 256, 256)]
    [InlineData(9u, 4, 300, 300)]
    [InlineData(24u, 4, 128, 256)]
    [InlineData(5u, 4, 200, 120)]
    [InlineData(8u, 4, 128, 128)]
    [InlineData(1u, 4, 64, 64)]
    [InlineData(27u, 8, 256, 256)]  // 8bpp (GPU: 2 words/element)
    [InlineData(27u, 16, 128, 128)] // 16bpp (GPU: 4 words/element)
    [InlineData(9u, 8, 128, 96)]
    [InlineData(8u, 16, 64, 64)]    // block-table, 16bpp
    public void DetileWithParams_MatchesTryDetile(uint mode, int bpp, int w, int h)
    {
        var p = GnmTiling.GetDetileParams(mode, bpp, w, h);
        Assert.True(p.IsSupported);

        var blocksHigh = (h + p.BlockHeight - 1) / p.BlockHeight;
        var tiled = new byte[(long)p.BlocksPerRow * blocksHigh * p.BlockBytes];
        for (var i = 0; i < tiled.Length; i++)
        {
            tiled[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        var expected = new byte[w * h * bpp];
        Assert.True(GnmTiling.TryDetile(tiled, expected, mode, w, h, bpp));

        var actual = new byte[w * h * bpp];
        Assert.True(GnmTiling.DetileWithParams(p, tiled, actual));
        Assert.Equal(expected, actual);
    }

    // Array textures are packed as contiguous tiled slices and detiled one slice
    // per layer (dispatch-Z on the GPU; a per-layer loop in the CPU fallbacks)
    // into a layer-major linear buffer. This pins that packing: each slice must
    // deswizzle into its own region and match a per-slice TryDetile, and a
    // per-layer-distinct pattern catches any slice cross-talk.
    [Theory]
    [InlineData(27u, 4, 256, 256, 3)]
    [InlineData(9u, 4, 128, 96, 2)]
    [InlineData(24u, 4, 64, 128, 4)]
    public void DetileWithParams_MultiLayer_MatchesPerSliceTryDetile(uint mode, int bpp, int w, int h, int layers)
    {
        var p = GnmTiling.GetDetileParams(mode, bpp, w, h);
        Assert.True(p.IsSupported);

        var blocksHigh = (h + p.BlockHeight - 1) / p.BlockHeight;
        var sliceTiledBytes = (int)((long)p.BlocksPerRow * blocksHigh * p.BlockBytes);
        var sliceLinearBytes = w * h * bpp;

        var tiled = new byte[sliceTiledBytes * layers];
        for (var layer = 0; layer < layers; layer++)
        {
            for (var i = 0; i < sliceTiledBytes; i++)
            {
                tiled[layer * sliceTiledBytes + i] = (byte)((i * 31 + 7 + layer * 101) & 0xFF);
            }
        }

        // Expected: each slice detiled independently via the shipped CPU detile.
        var expected = new byte[sliceLinearBytes * layers];
        for (var layer = 0; layer < layers; layer++)
        {
            Assert.True(GnmTiling.TryDetile(
                tiled.AsSpan(layer * sliceTiledBytes, sliceTiledBytes),
                expected.AsSpan(layer * sliceLinearBytes, sliceLinearBytes),
                mode, w, h, bpp));
        }

        // Actual: the layer-major loop the Vulkan/Metal CPU fallbacks run.
        var actual = new byte[sliceLinearBytes * layers];
        for (var layer = 0; layer < layers; layer++)
        {
            Assert.True(GnmTiling.DetileWithParams(
                p,
                tiled.AsSpan(layer * sliceTiledBytes, sliceTiledBytes),
                actual.AsSpan(layer * sliceLinearBytes, sliceLinearBytes)));
        }

        Assert.Equal(expected, actual);
    }

    // Reference detile driven entirely by DetileParams — the single shared
    // addressing formula the Vulkan/Metal compute kernel will run per texel.
    private static byte[] DetileViaParams(byte[] tiled, DetileParams p, int w, int h, int bpp)
    {
        var linear = new byte[w * h * bpp];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var blockX = x / p.BlockWidth;
                var blockY = y / p.BlockHeight;
                var inX = x % p.BlockWidth;
                var inY = y % p.BlockHeight;
                var inBlockByte = p.Equation == DetileEquation.ExactXor
                    ? p.XByteTerm[x & p.XMask] ^ p.YByteTerm[y & p.YMask]
                    : p.BlockTable[inY * p.BlockWidth + inX] * p.BytesPerElement;
                var srcByte = ((long)blockY * p.BlocksPerRow + blockX) * p.BlockBytes + inBlockByte;
                var dstByte = ((long)y * w + x) * bpp;
                if (srcByte < 0 || srcByte + bpp > tiled.Length)
                {
                    continue;
                }

                Array.Copy(tiled, srcByte, linear, dstByte, bpp);
            }
        }

        return linear;
    }
}
