// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// TryDetile's exact-XOR fast path (PS5 swizzle modes 5/9/24/27) factors the
// AddrLib bit-interleave into independent per-column X and per-row Y terms so
// the inner loop is one array load and one XOR instead of a 16-bit interleave.
// These tests pin that the factored output stays byte-identical to the direct
// AddrLib address equation.
public sealed class GnmTilingDetileTests
{
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
}
