// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-3.0-or-later
//
// Ported from pkg-to-anyps5 (src/kraken.rs), which follows PS5PCEM (src/pkg/kraken.zig) and the
// managed decoder of LibProsperoPKG. All three are GPL-3.0-or-later.

using System.Buffers.Binary;
using System.Numerics;

namespace SharpEmu.HLE.FileSystem.Pkg;

// Kraken (Oodle newLZ) decoder for the header-stripped blocks of a PS5 inner image. A 256 KiB block
// is one or two 128 KiB chunks with no Oodle block headers; the NAPS record gives the codec flags
// and the compressed length of the first chunk. Supported entropy types: memcpy and Huffman (2- and
// 4-way). tANS, RLE and recursive entropy blocks throw instead of guessing. Every access is bounds
// checked: corrupt input throws InvalidDataException.
internal static class Kraken
{
    private const int ChunkMax = 0x20000;
    private const int SeedSize = 8;
    private const uint Chunk0SubLit = 0x01;
    private const uint Chunk0NewLz = 0x02;
    private const uint Chunk1SubLit = 0x10;
    private const uint Chunk1NewLz = 0x20;
    private const uint Chunk1Restart = 0x40;

    private static readonly uint[] CodePrefixOrg = [0x0, 0x0, 0x2, 0x6, 0xE, 0x1E, 0x3E, 0x7E, 0xFE, 0x1FE, 0x2FE, 0x3FE];

    private static InvalidDataException Malformed() => new("Malformed Kraken data.");

    private static uint Shl(uint v, long n) => n is >= 0 and < 32 ? v << (int)n : 0;

    private static uint Shr(uint v, long n) => n is >= 0 and < 32 ? v >> (int)n : 0;

    // Decodes one stored block into `dst` (exactly its uncompressed size).
    public static void DecodeBlock(ReadOnlySpan<byte> payload, uint flags, uint firstChunkComp, Span<byte> dst)
    {
        if (dst.IsEmpty)
        {
            if (!payload.IsEmpty)
            {
                throw Malformed();
            }

            return;
        }

        var oneChunk = firstChunkComp == 0 || firstChunkComp >= payload.Length || dst.Length <= ChunkMax;
        if (oneChunk)
        {
            DecodeSubChunk(payload, dst, 0, dst.Length, (flags & Chunk0NewLz) != 0, true, SubLit(flags, Chunk0SubLit));
            return;
        }

        var chunk0Dst = Math.Min(dst.Length, ChunkMax);
        var chunk1Dst = dst.Length - chunk0Dst;
        var chunk0Comp = (int)firstChunkComp;
        var chunk1Comp = payload.Length - chunk0Comp;
        if (chunk1Dst == 0 && chunk1Comp != 0)
        {
            throw Malformed();
        }

        DecodeSubChunk(payload[..chunk0Comp], dst, 0, chunk0Dst, (flags & Chunk0NewLz) != 0, true, SubLit(flags, Chunk0SubLit));
        if (chunk1Dst > 0)
        {
            DecodeSubChunk(
                payload[chunk0Comp..],
                dst,
                chunk0Dst,
                chunk1Dst,
                (flags & Chunk1NewLz) != 0,
                (flags & Chunk1Restart) != 0,
                SubLit(flags, Chunk1SubLit));
        }
    }

    private static int SubLit(uint flags, uint bit) => (flags & bit) != 0 ? 0 : 1;

    private static void DecodeSubChunk(ReadOnlySpan<byte> src, Span<byte> dst, int dstStart, int dstLen, bool lz, bool restart, int literalMode)
    {
        if (dstStart + dstLen > dst.Length)
        {
            throw Malformed();
        }

        if (src.Length == dstLen)
        {
            src.CopyTo(dst.Slice(dstStart, dstLen));
            return;
        }

        if (lz)
        {
            DecodeChunk(src, dst, dstStart, dstLen, restart, literalMode);
            return;
        }

        var (used, decoded) = DecodeBytes(src, dstLen);
        if (used != src.Length || decoded.Length != dstLen)
        {
            throw Malformed();
        }

        decoded.CopyTo(dst.Slice(dstStart, dstLen));
    }

    private sealed class LzTable
    {
        public byte[] Lit = [];
        public byte[] Cmd = [];
        public int[] Offs = [];
        public int[] Lens = [];
    }

    private static void DecodeChunk(ReadOnlySpan<byte> src, Span<byte> dst, int dstStart, int dstLen, bool withSeed, int literalMode)
    {
        var sp = 0;
        if (withSeed)
        {
            if (src.Length < SeedSize || dstLen < SeedSize)
            {
                throw Malformed();
            }

            src[..SeedSize].CopyTo(dst.Slice(dstStart, SeedSize));
            sp = SeedSize;
        }

        var table = ReadLzTable(src, ref sp, dstLen);
        var start = dstStart + (withSeed ? SeedSize : 0);
        var end = dstStart + dstLen;
        var ok = literalMode switch
        {
            1 => ProcessType1(table, dst, start, end),
            0 => ProcessType0(table, dst, start, end),
            _ => false,
        };
        if (!ok)
        {
            throw Malformed();
        }
    }

    private static ReadOnlySpan<byte> Region(ReadOnlySpan<byte> src, int sp, int srcEnd) =>
        sp >= 0 && sp <= srcEnd && srcEnd <= src.Length ? src[sp..srcEnd] : throw Malformed();

    private static LzTable ReadLzTable(ReadOnlySpan<byte> src, ref int sp, int dstSize)
    {
        var srcEnd = src.Length;
        var excessFlag = false;
        var excessCount = 0;
        if (sp < srcEnd && (src[sp] & 0x80) != 0)
        {
            var flag = src[sp];
            if ((flag & 0xC0) == 0x80)
            {
                sp++;
                excessFlag = true;
                excessCount = flag & 0x3F;
                if (excessCount > 0x1F)
                {
                    if (sp >= srcEnd)
                    {
                        throw Malformed();
                    }

                    excessCount += src[sp] * 0x20;
                    sp++;
                }

                if (srcEnd < sp + excessCount)
                {
                    throw Malformed();
                }

                srcEnd -= excessCount;
            }
        }

        var table = new LzTable();
        var (used, lit) = DecodeBytes(Region(src, sp, srcEnd), dstSize);
        sp += used;
        table.Lit = lit;
        (used, var cmd) = DecodeBytes(Region(src, sp, srcEnd), dstSize);
        sp += used;
        table.Cmd = cmd;

        if (srcEnd - sp < 3)
        {
            throw Malformed();
        }

        var offsScaling = 0;
        byte[] packedExtra = [];
        byte[] packedOffs;
        if ((src[sp] & 0x80) != 0)
        {
            offsScaling = src[sp] - 127;
            sp++;
            (used, packedOffs) = DecodeBytes(Region(src, sp, srcEnd), table.Cmd.Length);
            sp += used;
            if (offsScaling != 1)
            {
                (used, packedExtra) = DecodeBytes(Region(src, sp, srcEnd), packedOffs.Length);
                if (packedExtra.Length != packedOffs.Length)
                {
                    throw Malformed();
                }

                sp += used;
            }
        }
        else
        {
            (used, packedOffs) = DecodeBytes(Region(src, sp, srcEnd), table.Cmd.Length);
            sp += used;
        }

        (used, var packedLen) = DecodeBytes(Region(src, sp, srcEnd), dstSize >> 2);
        sp += used;

        table.Offs = new int[packedOffs.Length];
        table.Lens = new int[packedLen.Length];
        var ok = UnpackOffsets(src, sp, srcEnd, excessFlag, excessCount, packedOffs, offsScaling, packedExtra, packedLen, table.Offs, table.Lens);
        return ok ? table : throw Malformed();
    }

    // Internal so the tests can pin down the excess-framing edge case.
    internal static bool UnpackOffsets(
        ReadOnlySpan<byte> src,
        int bsBegin,
        int bsEnd,
        bool excessFlag,
        int excessCount,
        byte[] packedOffs,
        int offsScaling,
        byte[] packedExtra,
        byte[] packedLen,
        int[] offsOut,
        int[] lenOut)
    {
        if (bsBegin < 0 || bsBegin > bsEnd)
        {
            return false;
        }

        var a = BitReader.Forward(src, bsBegin, bsEnd);
        var b = BitReader.Backward(src, bsBegin, bsEnd);
        long u32Len;
        if (!excessFlag)
        {
            if (b.Bits < 0x2000)
            {
                return false;
            }

            var nn = 31 - Bsr(b.Bits);
            b.BitPos += (int)nn;
            b.Bits = Shl(b.Bits, nn);
            b.Refill();
            nn++;
            u32Len = Shr(b.Bits, 32 - nn) - 1u;
            b.BitPos += (int)nn;
            b.Bits = Shl(b.Bits, nn);
            b.Refill();
        }
        else
        {
            u32Len = 0;
            foreach (var v in packedLen)
            {
                if (v == 255)
                {
                    u32Len++;
                }
            }
        }

        if (u32Len > 512)
        {
            return false;
        }

        if (offsScaling == 0)
        {
            var i = 0;
            while (i < packedOffs.Length)
            {
                offsOut[i] = -(int)a.ReadDistance(packedOffs[i]);
                i++;
                if (i >= packedOffs.Length)
                {
                    break;
                }

                offsOut[i] = -(int)b.ReadDistance(packedOffs[i]);
                i++;
            }
        }
        else
        {
            var i = 0;
            while (i < packedOffs.Length)
            {
                uint cmd = packedOffs[i];
                if ((cmd >> 3) > 26)
                {
                    return false;
                }

                var off = ((8 + (cmd & 7)) << (int)(cmd >> 3)) | a.ReadMoreThan24(cmd >> 3);
                offsOut[i] = 8 - (int)off;
                i++;
                if (i >= packedOffs.Length)
                {
                    break;
                }

                uint cmd2 = packedOffs[i];
                if ((cmd2 >> 3) > 26)
                {
                    return false;
                }

                var off2 = ((8 + (cmd2 & 7)) << (int)(cmd2 >> 3)) | b.ReadMoreThan24(cmd2 >> 3);
                offsOut[i] = 8 - (int)off2;
                i++;
            }

            if (offsScaling != 1)
            {
                if (packedExtra.Length != packedOffs.Length)
                {
                    return false;
                }

                for (var k = 0; k < offsOut.Length; k++)
                {
                    offsOut[k] = (sbyte)packedExtra[k] - offsOut[k] * offsScaling;
                }
            }
        }

        var u32s = new uint[512];
        if (u32Len > 0)
        {
            BitReader ra;
            BitReader rb;
            if (excessFlag)
            {
                ra = BitReader.Forward(src, bsEnd, bsEnd + excessCount);
                rb = BitReader.Backward(src, bsEnd, bsEnd + excessCount);
            }
            else
            {
                ra = a;
                rb = b;
            }

            var i = 0;
            while (i + 1 < u32Len)
            {
                if (!ra.ReadLength(out u32s[i]) || !rb.ReadLength(out u32s[i + 1]))
                {
                    return false;
                }

                i += 2;
            }

            if (i < u32Len && !ra.ReadLength(out u32s[i]))
            {
                return false;
            }
        }

        long u = 0;
        for (var i = 0; i < packedLen.Length; i++)
        {
            uint v = packedLen[i];
            if (v == 255)
            {
                if (u >= u32Len)
                {
                    return false;
                }

                v = u32s[u] + 255;
                u++;
            }

            lenOut[i] = (int)(v + 3);
        }

        return u == u32Len;
    }

    private static int RecentAt(int[] values, int index) => index < values.Length ? values[index] : 0;

    private static bool ProcessType1(LzTable t, Span<byte> dst, int start, int dstEnd)
    {
        int cmdI = 0, lenI = 0, litI = 0, offsI = 0;
        var dstPos = start;
        Span<int> recent = [0, 0, 0, -8, -8, -8, 0];
        while (cmdI < t.Cmd.Length)
        {
            int f = t.Cmd[cmdI++];
            var litLen = f & 3;
            var offsIndex = f >> 6;
            var matchLen = (f >> 2) & 0xF;
            if (litLen == 3)
            {
                if (lenI >= t.Lens.Length || t.Lens[lenI] < 0)
                {
                    return false;
                }

                litLen = t.Lens[lenI++];
            }

            recent[6] = RecentAt(t.Offs, offsI);
            if (litLen != 0)
            {
                if (litI + litLen > t.Lit.Length || dstPos + litLen > dstEnd)
                {
                    return false;
                }

                t.Lit.AsSpan(litI, litLen).CopyTo(dst.Slice(dstPos, litLen));
                dstPos += litLen;
                litI += litLen;
            }

            var offset = recent[offsIndex + 3];
            recent[offsIndex + 3] = recent[offsIndex + 2];
            recent[offsIndex + 2] = recent[offsIndex + 1];
            recent[offsIndex + 1] = recent[offsIndex];
            recent[3] = offset;
            offsI += ((offsIndex + 1) & 4) >> 2;
            var from = (long)dstPos + offset;
            // A match copies bytes already written in this block.
            if (from < 0 || from >= dstPos)
            {
                return false;
            }

            int actual;
            if (matchLen != 15)
            {
                actual = matchLen + 2;
            }
            else
            {
                if (lenI >= t.Lens.Length || t.Lens[lenI] < 0)
                {
                    return false;
                }

                actual = 14 + t.Lens[lenI++];
                if (actual < 0)
                {
                    return false;
                }
            }

            if (dstPos + actual > dstEnd || from + actual > dst.Length)
            {
                return false;
            }

            CopyMatch(dst, (int)from, dstPos, actual);
            dstPos += actual;
        }

        if (offsI != t.Offs.Length || lenI != t.Lens.Length)
        {
            return false;
        }

        var finalLen = dstEnd - dstPos;
        if (finalLen != t.Lit.Length - litI)
        {
            return false;
        }

        t.Lit.AsSpan(litI).CopyTo(dst.Slice(dstPos, finalLen));
        return true;
    }

    private static void DeltaLiterals(Span<byte> dst, int at, ReadOnlySpan<byte> lit, int lastOffset)
    {
        for (var c = 0; c < lit.Length; c++)
        {
            var back = (long)at + c + lastOffset;
            var prev = back >= 0 && back < dst.Length ? dst[(int)back] : (byte)0;
            dst[at + c] = (byte)(lit[c] + prev);
        }
    }

    private static bool ProcessType0(LzTable t, Span<byte> dst, int start, int dstEnd)
    {
        int cmdI = 0, lenI = 0, litI = 0, offsI = 0;
        var dstPos = start;
        Span<int> recent = [0, 0, 0, -8, -8, -8, 0];
        var lastOffset = -8;
        while (cmdI < t.Cmd.Length)
        {
            int f = t.Cmd[cmdI++];
            var litLen = f & 3;
            var offsIndex = f >> 6;
            var matchLen = (f >> 2) & 0xF;
            if (litLen == 3)
            {
                if (lenI >= t.Lens.Length || t.Lens[lenI] < 0)
                {
                    return false;
                }

                litLen = t.Lens[lenI++];
            }

            recent[6] = RecentAt(t.Offs, offsI);
            if (litLen != 0)
            {
                if (litI + litLen > t.Lit.Length || dstPos + litLen > dstEnd)
                {
                    return false;
                }

                DeltaLiterals(dst, dstPos, t.Lit.AsSpan(litI, litLen), lastOffset);
                dstPos += litLen;
                litI += litLen;
            }

            var offset = recent[offsIndex + 3];
            recent[offsIndex + 3] = recent[offsIndex + 2];
            recent[offsIndex + 2] = recent[offsIndex + 1];
            recent[offsIndex + 1] = recent[offsIndex];
            recent[3] = offset;
            lastOffset = offset;
            offsI += ((offsIndex + 1) & 4) >> 2;
            int actual;
            if (matchLen != 15)
            {
                actual = matchLen + 2;
            }
            else
            {
                if (lenI >= t.Lens.Length || t.Lens[lenI] < 0)
                {
                    return false;
                }

                actual = 14 + t.Lens[lenI++];
                if (actual < 0)
                {
                    return false;
                }
            }

            if (dstPos + actual > dstEnd)
            {
                return false;
            }

            var from = (long)dstPos + offset;
            // A match copies bytes already written in this block; one before the start or at or
            // after the cursor is corrupt data.
            if (from < 0 || from >= dstPos)
            {
                return false;
            }

            CopyMatch(dst, (int)from, dstPos, actual);
            dstPos += actual;
        }

        if (offsI != t.Offs.Length || lenI != t.Lens.Length)
        {
            return false;
        }

        // The remaining literals fill the block exactly; a short stream would leave stale bytes
        // from the previous block in the reused buffer.
        var rest = t.Lit.AsSpan(litI);
        if (dstPos + rest.Length != dstEnd)
        {
            return false;
        }

        DeltaLiterals(dst, dstPos, rest, lastOffset);
        return true;
    }

    // Byte by byte so overlapping matches repeat their pattern.
    private static void CopyMatch(Span<byte> dst, int from, int to, int len)
    {
        for (var c = 0; c < len; c++)
        {
            dst[to + c] = dst[from + c];
        }
    }

    // Decodes one entropy-coded byte array. Returns the bytes consumed from `src` and the output.
    internal static (int Used, byte[] Data) DecodeBytes(ReadOnlySpan<byte> src, int outputCap)
    {
        if (src.Length < 2)
        {
            throw Malformed();
        }

        var chunkType = (src[0] >> 4) & 7;
        if (chunkType == 0)
        {
            int size, sp;
            if (src[0] >= 0x80)
            {
                size = ((src[0] << 8) | src[1]) & 0xFFF;
                sp = 2;
            }
            else
            {
                if (src.Length < 3)
                {
                    throw Malformed();
                }

                size = (src[0] << 16) | (src[1] << 8) | src[2];
                if ((size & ~0x3FFFF) != 0)
                {
                    throw Malformed();
                }

                sp = 3;
            }

            if (size > outputCap || src.Length - sp < size)
            {
                throw Malformed();
            }

            return (sp + size, src.Slice(sp, size).ToArray());
        }

        int srcSize, dstSize, headerSize;
        if (src[0] >= 0x80)
        {
            if (src.Length < 3)
            {
                throw Malformed();
            }

            var bits = ((uint)src[0] << 16) | ((uint)src[1] << 8) | src[2];
            srcSize = (int)(bits & 0x3FF);
            dstSize = srcSize + (int)((bits >> 10) & 0x3FF) + 1;
            headerSize = 3;
        }
        else
        {
            if (src.Length < 5)
            {
                throw Malformed();
            }

            var bits = BinaryPrimitives.ReadUInt32BigEndian(src[1..]);
            srcSize = (int)(bits & 0x3FFFF);
            dstSize = (int)((((bits >> 18) | ((uint)src[0] << 14)) & 0x3FFFF) + 1);
            if (srcSize >= dstSize)
            {
                throw Malformed();
            }

            headerSize = 5;
        }

        if (src.Length - headerSize < srcSize || dstSize > outputCap)
        {
            throw Malformed();
        }

        var output = new byte[dstSize];
        switch (chunkType)
        {
            case 2:
            case 4:
                if (!DecodeHuff(src.Slice(headerSize, srcSize), output, chunkType >> 1))
                {
                    throw Malformed();
                }

                break;
            default:
                throw new InvalidDataException($"Unsupported Kraken entropy type {chunkType}.");
        }

        return (headerSize + srcSize, output);
    }

    private sealed class HuffLut
    {
        public readonly byte[] Bits2Len = new byte[2048 + 16];
        public readonly byte[] Bits2Sym = new byte[2048 + 16];
    }

    private static bool DecodeHuff(ReadOnlySpan<byte> src, byte[] output, int typeDiv)
    {
        var prefix = (uint[])CodePrefixOrg.Clone();
        var syms = new byte[1280];
        var bits = BitReader.Forward(src, 0, src.Length);
        int numSyms;
        if (bits.ReadBitNoRefill() == 0)
        {
            numSyms = HuffReadCodeLengthsOld(ref bits, syms, prefix);
        }
        else if (bits.ReadBitNoRefill() == 0)
        {
            numSyms = HuffReadCodeLengthsNew(ref bits, syms, prefix);
        }
        else
        {
            return false;
        }

        if (numSyms < 1)
        {
            return false;
        }

        var adj = (24 - bits.BitPos) / 8;
        long spLong = bits.P - adj;
        if (numSyms == 1)
        {
            Array.Fill(output, syms[0]);
            return true;
        }

        if (spLong < 0 || spLong > src.Length)
        {
            return false;
        }

        var sp = (int)spLong;
        var lut = new HuffLut();
        if (!HuffMakeLut(prefix, lut, syms))
        {
            return false;
        }

        var rev = new HuffLut();
        for (var i = 0; i < 2048; i++)
        {
            var r = Reverse11((uint)i);
            rev.Bits2Len[i] = lut.Bits2Len[r];
            rev.Bits2Sym[i] = lut.Bits2Sym[r];
        }

        if (typeDiv == 1)
        {
            if (sp + 3 > src.Length)
            {
                return false;
            }

            var splitMid1 = src[sp] | (src[sp + 1] << 8);
            sp += 2;
            return DecodeBytesCore(src, output, 0, output.Length, sp, src.Length, sp + splitMid1, rev);
        }

        if (sp + 6 > src.Length)
        {
            return false;
        }

        var half = (output.Length + 1) >> 1;
        var splitMid = src[sp] | (src[sp + 1] << 8) | (src[sp + 2] << 16);
        sp += 3;
        if (splitMid > src.Length - sp)
        {
            return false;
        }

        var srcMid = sp + splitMid;
        var splitLeft = src[sp] | (src[sp + 1] << 8);
        sp += 2;
        if (srcMid < sp || srcMid - sp < splitLeft + 2 || src.Length - srcMid < 3)
        {
            return false;
        }

        var splitRight = src[srcMid] | (src[srcMid + 1] << 8);
        if (src.Length - (srcMid + 2) < splitRight + 2)
        {
            return false;
        }

        return DecodeBytesCore(src, output, 0, half, sp, srcMid, sp + splitLeft, rev)
            && DecodeBytesCore(src, output, half, output.Length, srcMid + 2, src.Length, srcMid + 2 + splitRight, rev);
    }

    private static bool HuffMakeLut(uint[] prefixCur, HuffLut lut, byte[] syms)
    {
        var currSlot = 0;
        for (var i = 1; i < 11; i++)
        {
            var start = (int)CodePrefixOrg[i];
            var count = (int)(prefixCur[i] - CodePrefixOrg[i]);
            if (count == 0)
            {
                continue;
            }

            var stepSize = 1 << (11 - i);
            var numToSet = count << (11 - i);
            if (currSlot + numToSet > 2048)
            {
                return false;
            }

            Array.Fill(lut.Bits2Len, (byte)i, currSlot, numToSet);
            var p = currSlot;
            for (var j = 0; j < count; j++)
            {
                Array.Fill(lut.Bits2Sym, syms[start + j], p, stepSize);
                p += stepSize;
            }

            currSlot += numToSet;
        }

        var numToSet11 = (int)(prefixCur[11] - CodePrefixOrg[11]);
        if (numToSet11 != 0)
        {
            if (currSlot + numToSet11 > 2048)
            {
                return false;
            }

            var start = (int)CodePrefixOrg[11];
            Array.Fill(lut.Bits2Len, (byte)11, currSlot, numToSet11);
            Array.Copy(syms, start, lut.Bits2Sym, currSlot, numToSet11);
            currSlot += numToSet11;
        }

        return currSlot == 2048;
    }

    private static int Reverse11(uint v)
    {
        uint r = 0;
        for (var i = 0; i < 11; i++)
        {
            r = (r << 1) | (v & 1);
            v >>= 1;
        }

        return (int)r;
    }

    private static int HuffReadCodeLengthsOld(ref BitReader bits, byte[] syms, uint[] codePrefix)
    {
        if (bits.ReadBitNoRefill() != 0)
        {
            var sym = 0;
            var numSymbols = 0;
            var avg = 32;
            var forced = (int)bits.ReadBitsNoRefill(2);
            var thres = 1u << (31 - (int)(20u >> forced));
            var skip = bits.ReadBit() != 0;
            while (true)
            {
                if (!skip)
                {
                    if ((bits.Bits & 0xFF00_0000) == 0)
                    {
                        return -1;
                    }

                    sym += (int)bits.ReadBitsNoRefill(2 * (Clz(bits.Bits) + 1)) - 2 + 1;
                    if (sym >= 256)
                    {
                        break;
                    }
                }

                skip = false;
                bits.Refill();
                if ((bits.Bits & 0xFF00_0000) == 0)
                {
                    return -1;
                }

                var n = (int)bits.ReadBitsNoRefill(2 * (Clz(bits.Bits) + 1)) - 2 + 1;
                if (sym + n > 256)
                {
                    return -1;
                }

                bits.Refill();
                numSymbols += n;
                while (n != 0)
                {
                    n--;
                    if (bits.Bits < thres)
                    {
                        return -1;
                    }

                    var lz = Clz(bits.Bits);
                    var v = (int)bits.ReadBitsNoRefill(lz + (uint)forced + 1);
                    var vv = v + (((int)lz - 1) << forced);
                    var codeLen = (-(vv & 1) ^ (vv >> 1)) + ((avg + 2) >> 2);
                    if (codeLen is < 1 or > 11)
                    {
                        return -1;
                    }

                    avg = codeLen + ((3 * avg + 2) >> 2);
                    bits.Refill();
                    var cp = codePrefix[codeLen];
                    codePrefix[codeLen] = cp + 1;
                    if (cp >= syms.Length)
                    {
                        return -1;
                    }

                    syms[cp] = (byte)sym;
                    sym++;
                }

                if (sym == 256)
                {
                    break;
                }
            }

            return sym == 256 && numSymbols >= 2 ? numSymbols : -1;
        }

        var count = (int)bits.ReadBitsNoRefill(8);
        if (count == 0)
        {
            return -1;
        }

        if (count == 1)
        {
            syms[0] = (byte)bits.ReadBitsNoRefill(8);
        }
        else
        {
            var codeLenBits = bits.ReadBitsNoRefill(3);
            if (codeLenBits > 4)
            {
                return -1;
            }

            for (var i = 0; i < count; i++)
            {
                bits.Refill();
                var sym = bits.ReadBitsNoRefill(8);
                var codeLen = (int)bits.ReadBitsNoRefillZero(codeLenBits) + 1;
                if (codeLen > 11)
                {
                    return -1;
                }

                var cp = codePrefix[codeLen];
                codePrefix[codeLen] = cp + 1;
                if (cp >= syms.Length)
                {
                    return -1;
                }

                syms[cp] = (byte)sym;
            }
        }

        return count;
    }

    private static int HuffReadCodeLengthsNew(ref BitReader bits, byte[] syms, uint[] codePrefix)
    {
        var forcedBits = (int)bits.ReadBitsNoRefill(2);
        var numSymbols = (int)bits.ReadBitsNoRefill(8) + 1;
        var fluff = bits.ReadFluff(numSymbols);
        var codeLen = new byte[512 + 16];
        var adj = (24 - bits.BitPos + 7) >> 3;
        var br2 = new BitReader2 { P = bits.P - adj, PEnd = bits.Bound, BitPos = (bits.BitPos - 24) & 7 };
        if (!DecodeGolombRiceLengths(bits.B, codeLen, numSymbols + fluff, ref br2))
        {
            return -1;
        }

        if (!DecodeGolombRiceBits(bits.B, codeLen, numSymbols, forcedBits, ref br2))
        {
            return -1;
        }

        bits.BitPos = 24;
        bits.P = br2.P;
        bits.Bits = 0;
        bits.Refill();
        bits.Bits = Shl(bits.Bits, br2.BitPos);
        bits.BitPos += br2.BitPos;

        uint runningSum = 0x1e;
        for (var i = 0; i < numSymbols; i++)
        {
            int v = codeLen[i];
            v = -(v & 1) ^ (v >> 1);
            var cl = v + (int)(runningSum >> 2) + 1;
            if (cl is < 1 or > 11)
            {
                return -1;
            }

            codeLen[i] = (byte)cl;
            runningSum = (uint)((int)runningSum + v);
        }

        var ranges = new (int Sym, int Count)[128];
        var nRanges = HuffConvertToRanges(ranges, numSymbols, fluff, codeLen, numSymbols, ref bits);
        if (nRanges <= 0)
        {
            return -1;
        }

        var cp = 0;
        for (var r = 0; r < nRanges; r++)
        {
            var (sym, nn) = ranges[r];
            while (nn != 0)
            {
                nn--;
                var clen = codeLen[cp++];
                var slot = codePrefix[clen];
                codePrefix[clen] = slot + 1;
                if (slot >= syms.Length)
                {
                    return -1;
                }

                syms[slot] = (byte)sym;
                sym++;
            }
        }

        return numSymbols;
    }

    private static int HuffConvertToRanges(
        (int Sym, int Count)[] range,
        int numSymbols,
        int p,
        byte[] symLen,
        int start,
        ref BitReader bits)
    {
        var numRanges = p >> 1;
        var symIdx = 0;
        var off = start;
        if ((p & 1) != 0)
        {
            bits.Refill();
            int v = symLen[off++];
            if (v >= 8)
            {
                return -1;
            }

            var n = (uint)(v + 1);
            symIdx = (int)(bits.ReadBitsNoRefill(n) + (1u << (int)n) - 1);
        }

        if (numRanges >= range.Length)
        {
            return -1;
        }

        var symsUsed = 0;
        for (var i = 0; i < numRanges; i++)
        {
            bits.Refill();
            uint v = symLen[off];
            if (v >= 9)
            {
                return -1;
            }

            var num = (int)(bits.ReadBitsNoRefillZero(v) + (1u << (int)v));
            uint v2 = symLen[off + 1];
            if (v2 >= 8)
            {
                return -1;
            }

            var space = (int)(bits.ReadBitsNoRefill(v2 + 1) + (1u << (int)(v2 + 1)) - 1);
            range[i] = (symIdx, num);
            symsUsed += num;
            symIdx += num + space;
            off += 2;
        }

        if (symIdx >= 256 || symsUsed >= numSymbols || symIdx + numSymbols - symsUsed > 256)
        {
            return -1;
        }

        range[numRanges] = (symIdx, numSymbols - symsUsed);
        return numRanges + 1;
    }

    private struct BitReader2
    {
        public int P;
        public int PEnd;
        public int BitPos;
    }

    private static readonly uint[] RiceVal =
    [
        0x80000000, 0x00000007, 0x10000006, 0x00000006, 0x20000005, 0x00000105, 0x10000005, 0x00000005, 0x30000004, 0x00000204, 0x10000104,
        0x00000104, 0x20000004, 0x00010004, 0x10000004, 0x00000004, 0x40000003, 0x00000303, 0x10000203, 0x00000203, 0x20000103, 0x00010103,
        0x10000103, 0x00000103, 0x30000003, 0x00020003, 0x10010003, 0x00010003, 0x20000003, 0x01000003, 0x10000003, 0x00000003, 0x50000002,
        0x00000402, 0x10000302, 0x00000302, 0x20000202, 0x00010202, 0x10000202, 0x00000202, 0x30000102, 0x00020102, 0x10010102, 0x00010102,
        0x20000102, 0x01000102, 0x10000102, 0x00000102, 0x40000002, 0x00030002, 0x10020002, 0x00020002, 0x20010002, 0x01010002, 0x10010002,
        0x00010002, 0x30000002, 0x02000002, 0x11000002, 0x01000002, 0x20000002, 0x00000012, 0x10000002, 0x00000002, 0x60000001, 0x00000501,
        0x10000401, 0x00000401, 0x20000301, 0x00010301, 0x10000301, 0x00000301, 0x30000201, 0x00020201, 0x10010201, 0x00010201, 0x20000201,
        0x01000201, 0x10000201, 0x00000201, 0x40000101, 0x00030101, 0x10020101, 0x00020101, 0x20010101, 0x01010101, 0x10010101, 0x00010101,
        0x30000101, 0x02000101, 0x11000101, 0x01000101, 0x20000101, 0x00000111, 0x10000101, 0x00000101, 0x50000001, 0x00040001, 0x10030001,
        0x00030001, 0x20020001, 0x01020001, 0x10020001, 0x00020001, 0x30010001, 0x02010001, 0x11010001, 0x01010001, 0x20010001, 0x00010011,
        0x10010001, 0x00010001, 0x40000001, 0x03000001, 0x12000001, 0x02000001, 0x21000001, 0x01000011, 0x11000001, 0x01000001, 0x30000001,
        0x00000021, 0x10000011, 0x00000011, 0x20000001, 0x00001001, 0x10000001, 0x00000001, 0x70000000, 0x00000600, 0x10000500, 0x00000500,
        0x20000400, 0x00010400, 0x10000400, 0x00000400, 0x30000300, 0x00020300, 0x10010300, 0x00010300, 0x20000300, 0x01000300, 0x10000300,
        0x00000300, 0x40000200, 0x00030200, 0x10020200, 0x00020200, 0x20010200, 0x01010200, 0x10010200, 0x00010200, 0x30000200, 0x02000200,
        0x11000200, 0x01000200, 0x20000200, 0x00000210, 0x10000200, 0x00000200, 0x50000100, 0x00040100, 0x10030100, 0x00030100, 0x20020100,
        0x01020100, 0x10020100, 0x00020100, 0x30010100, 0x02010100, 0x11010100, 0x01010100, 0x20010100, 0x00010110, 0x10010100, 0x00010100,
        0x40000100, 0x03000100, 0x12000100, 0x02000100, 0x21000100, 0x01000110, 0x11000100, 0x01000100, 0x30000100, 0x00000120, 0x10000110,
        0x00000110, 0x20000100, 0x00001100, 0x10000100, 0x00000100, 0x60000000, 0x00050000, 0x10040000, 0x00040000, 0x20030000, 0x01030000,
        0x10030000, 0x00030000, 0x30020000, 0x02020000, 0x11020000, 0x01020000, 0x20020000, 0x00020010, 0x10020000, 0x00020000, 0x40010000,
        0x03010000, 0x12010000, 0x02010000, 0x21010000, 0x01010010, 0x11010000, 0x01010000, 0x30010000, 0x00010020, 0x10010010, 0x00010010,
        0x20010000, 0x00011000, 0x10010000, 0x00010000, 0x50000000, 0x04000000, 0x13000000, 0x03000000, 0x22000000, 0x02000010, 0x12000000,
        0x02000000, 0x31000000, 0x01000020, 0x11000010, 0x01000010, 0x21000000, 0x01001000, 0x11000000, 0x01000000, 0x40000000, 0x00000030,
        0x10000020, 0x00000020, 0x20000010, 0x00001010, 0x10000010, 0x00000010, 0x30000000, 0x00002000, 0x10001000, 0x00001000, 0x20000000,
        0x00100000, 0x10000000, 0x00000000,
    ];

    private static readonly byte[] RiceLen =
    [
        0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4, 1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3,
        4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 1, 2, 2, 3, 2, 3, 3, 4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4,
        4, 5, 4, 5, 5, 6, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7, 1, 2, 2, 3, 2, 3, 3,
        4, 2, 3, 3, 4, 3, 4, 4, 5, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5,
        4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7, 2, 3, 3, 4, 3, 4, 4, 5, 3, 4, 4, 5, 4, 5, 5, 6, 3, 4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7, 3,
        4, 4, 5, 4, 5, 5, 6, 4, 5, 5, 6, 5, 6, 6, 7, 4, 5, 5, 6, 5, 6, 6, 7, 5, 6, 6, 7, 6, 7, 7, 8,
    ];

    private static byte AtByte(ReadOnlySpan<byte> b, int i) => (uint)i < (uint)b.Length ? b[i] : (byte)0;

    private static uint ReadLe32(ReadOnlySpan<byte> b, int off)
    {
        uint r = 0;
        for (var i = 0; i < 4; i++)
        {
            r |= (uint)AtByte(b, off + i) << (8 * i);
        }

        return r;
    }

    private static ulong ReadLe64(ReadOnlySpan<byte> b, int off)
    {
        ulong r = 0;
        for (var i = 0; i < 8; i++)
        {
            r |= (ulong)AtByte(b, off + i) << (8 * i);
        }

        return r;
    }

    private static void WriteLe32(byte[] b, int off, uint v)
    {
        for (var i = 0; i < 4; i++)
        {
            if ((uint)(off + i) < (uint)b.Length)
            {
                b[off + i] = (byte)(v >> (8 * i));
            }
        }
    }

    private static void WriteLe64(byte[] b, int off, ulong v)
    {
        for (var i = 0; i < 8; i++)
        {
            if ((uint)(off + i) < (uint)b.Length)
            {
                b[off + i] = (byte)(v >> (8 * i));
            }
        }
    }

    private static bool DecodeGolombRiceLengths(ReadOnlySpan<byte> src, byte[] dst, int size, ref BitReader2 br)
    {
        var p = br.P;
        var pEnd = br.PEnd;
        var dstPos = 0;
        if (p >= pEnd)
        {
            return false;
        }

        var count = -br.BitPos;
        var v = AtByte(src, p) & Shr(255, Math.Max(br.BitPos, 0));
        p++;
        while (true)
        {
            if (v == 0)
            {
                count += 8;
            }
            else
            {
                var x = RiceVal[v];
                WriteLe32(dst, dstPos, (uint)count + (x & 0x0F0F0F0F));
                WriteLe32(dst, dstPos + 4, (x >> 4) & 0x0F0F0F0F);
                dstPos += RiceLen[v];
                if (dstPos >= size)
                {
                    break;
                }

                count = (int)(x >> 28);
            }

            if (p >= pEnd)
            {
                return false;
            }

            v = AtByte(src, p);
            p++;
        }

        if (dstPos > size)
        {
            for (var i = 0; i < dstPos - size; i++)
            {
                v &= v - 1;
            }
        }

        var bitPos = 0;
        if ((v & 1) == 0)
        {
            p--;
            bitPos = 8 - (int)Bsf(v);
        }

        br.P = p;
        br.BitPos = bitPos;
        return true;
    }

    private static bool DecodeGolombRiceBits(ReadOnlySpan<byte> src, byte[] dst, int size, int bitCount, ref BitReader2 br)
    {
        if (bitCount == 0)
        {
            return true;
        }

        var dstPos = 0;
        var p = br.P;
        var bitPos = br.BitPos;
        var bitsRequired = (long)Math.Max(bitPos, 0) + (long)bitCount * size;
        var bytesRequired = (bitsRequired + 7) / 8;
        if (p < 0 || br.PEnd - p < 0 || bytesRequired > br.PEnd - p)
        {
            return false;
        }

        br.P = p + (int)(bitsRequired >> 3);
        br.BitPos = (int)(bitsRequired & 7);
        var bak = ReadLe64(dst, size);
        while (dstPos < size)
        {
            var cur = ReadLe64(dst, dstPos);
            ulong spread;
            switch (bitCount)
            {
                case 1:
                {
                    var bits = (ulong)(byte)Shr(BinaryPrimitives.ReverseEndianness(ReadLe32(src, p)), 24 - bitPos);
                    p += 1;
                    bits = (bits | (bits << 28)) & 0xF0000000F;
                    bits = (bits | (bits << 14)) & 0x3000300030003;
                    bits = (bits | (bits << 7)) & 0x0101010101010101;
                    spread = cur * 2 + BinaryPrimitives.ReverseEndianness(bits);
                    break;
                }

                case 2:
                {
                    var bits = (ulong)(ushort)Shr(BinaryPrimitives.ReverseEndianness(ReadLe32(src, p)), 16 - bitPos);
                    p += 2;
                    bits = (bits | (bits << 24)) & 0xFF000000FF;
                    bits = (bits | (bits << 12)) & 0xF000F000F000F;
                    bits = (bits | (bits << 6)) & 0x0303030303030303;
                    spread = cur * 4 + BinaryPrimitives.ReverseEndianness(bits);
                    break;
                }

                default:
                {
                    var bits = (ulong)(Shr(BinaryPrimitives.ReverseEndianness(ReadLe32(src, p)), 8 - bitPos) & 0xFFFFFF);
                    p += 3;
                    bits = (bits | (bits << 20)) & 0xFFF00000FFF;
                    bits = (bits | (bits << 10)) & 0x3F003F003F003F;
                    bits = (bits | (bits << 5)) & 0x0707070707070707;
                    spread = cur * 8 + BinaryPrimitives.ReverseEndianness(bits);
                    break;
                }
            }

            WriteLe64(dst, dstPos, spread);
            dstPos += 8;
        }

        WriteLe64(dst, size, bak);
        return true;
    }

    private static bool DecodeBytesCore(
        ReadOnlySpan<byte> src,
        byte[] output,
        int outOff,
        int outEnd,
        int srcOff,
        int srcEnd0,
        int srcMidOrg,
        HuffLut lut)
    {
        var srcI = srcOff;
        uint srcBits = 0;
        var srcBitPos = 0;
        var srcMid = srcMidOrg;
        uint srcMidBits = 0;
        var srcMidBitPos = 0;
        var srcEnd = srcEnd0;
        uint srcEndBits = 0;
        var srcEndBitPos = 0;
        var dst = outOff;
        if (srcI > srcMid || srcMid > srcEnd || srcEnd > src.Length || outEnd > output.Length)
        {
            return false;
        }

        while (dst < outEnd)
        {
            long bp = (uint)srcBitPos & 31;
            if (srcMid - srcI <= 1)
            {
                if (srcMid - srcI == 1)
                {
                    srcBits |= Shl(src[srcI], bp);
                }
            }
            else
            {
                srcBits |= Shl((uint)(src[srcI] | (src[srcI + 1] << 8)), bp);
            }

            var k = (int)(srcBits & 0x7FF);
            int n = lut.Bits2Len[k];
            srcBitPos -= n;
            srcBits = Shr(srcBits, n);
            output[dst++] = lut.Bits2Sym[k];
            srcI += (7 - srcBitPos) >> 3;
            srcBitPos &= 7;
            if (dst < outEnd)
            {
                long ebp = (uint)srcEndBitPos & 31;
                long mbp = (uint)srcMidBitPos & 31;
                if (srcEnd - srcMid <= 1)
                {
                    if (srcEnd - srcMid == 1)
                    {
                        srcEndBits |= Shl(src[srcMid], ebp);
                        srcMidBits |= Shl(src[srcMid], mbp);
                    }
                }
                else
                {
                    var vv = (uint)(src[srcEnd - 2] | (src[srcEnd - 1] << 8));
                    srcEndBits |= Shl(((vv >> 8) | (vv << 8)) & 0xFFFF, ebp);
                    srcMidBits |= Shl((uint)(src[srcMid] | (src[srcMid + 1] << 8)), mbp);
                }

                k = (int)(srcEndBits & 0x7FF);
                n = lut.Bits2Len[k];
                output[dst++] = lut.Bits2Sym[k];
                srcEndBitPos -= n;
                srcEndBits = Shr(srcEndBits, n);
                srcEnd -= (7 - srcEndBitPos) >> 3;
                srcEndBitPos &= 7;
                if (dst < outEnd)
                {
                    k = (int)(srcMidBits & 0x7FF);
                    n = lut.Bits2Len[k];
                    output[dst++] = lut.Bits2Sym[k];
                    srcMidBitPos -= n;
                    srcMidBits = Shr(srcMidBits, n);
                    srcMid += (7 - srcMidBitPos) >> 3;
                    srcMidBitPos &= 7;
                }
            }

            if (srcI > srcMid || srcMid > srcEnd)
            {
                return false;
            }
        }

        return srcI == srcMidOrg && srcEnd == srcMid;
    }

    // A 32-bit window with the next bits at the top, filled forwards from P or backwards from P
    // down to Bound.
    private ref struct BitReader
    {
        public ReadOnlySpan<byte> B;
        public int P;
        public int Bound;
        public uint Bits;
        public int BitPos;
        private bool _backward;

        public static BitReader Forward(ReadOnlySpan<byte> b, int start, int end)
        {
            var r = new BitReader { B = b, P = start, Bound = end, Bits = 0, BitPos = 24, _backward = false };
            r.RefillForward();
            return r;
        }

        public static BitReader Backward(ReadOnlySpan<byte> b, int low, int high)
        {
            var r = new BitReader { B = b, P = high, Bound = low, Bits = 0, BitPos = 24, _backward = true };
            r.RefillBackward();
            return r;
        }

        public void Refill()
        {
            if (_backward)
            {
                RefillBackward();
            }
            else
            {
                RefillForward();
            }
        }

        private void RefillForward()
        {
            while (BitPos > 0)
            {
                uint value = P < Bound ? AtByte(B, P) : 0u;
                Bits |= Shl(value, BitPos);
                BitPos -= 8;
                P++;
            }
        }

        private void RefillBackward()
        {
            while (BitPos > 0)
            {
                P--;
                uint value = P >= 0 && P < B.Length && P >= Bound ? B[P] : 0u;
                Bits |= Shl(value, BitPos);
                BitPos -= 8;
            }
        }

        public uint ReadBit()
        {
            Refill();
            return ReadBitNoRefill();
        }

        public uint ReadBitNoRefill()
        {
            var r = Bits >> 31;
            Bits <<= 1;
            BitPos++;
            return r;
        }

        public uint ReadBitsNoRefill(uint n)
        {
            var r = Shr(Bits, 32 - (long)n);
            Bits = Shl(Bits, n);
            BitPos += (int)n;
            return r;
        }

        public uint ReadBitsNoRefillZero(uint n)
        {
            var r = Shr(Bits >> 1, 31 - (long)n);
            Bits = Shl(Bits, n);
            BitPos += (int)n;
            return r;
        }

        public int ReadFluff(int numSymbols)
        {
            if (numSymbols == 256)
            {
                return 0;
            }

            var x = Math.Min(257 - numSymbols, numSymbols) * 2;
            var y = (int)Bsr((uint)(x - 1)) + 1;
            var v = Shr(Bits, 32 - y);
            var z = (1u << y) - (uint)x;
            if ((v >> 1) >= z)
            {
                Bits = Shl(Bits, y);
                BitPos += y;
                return (int)(v - z);
            }

            Bits = Shl(Bits, y - 1);
            BitPos += y - 1;
            return (int)(v >> 1);
        }

        public uint ReadMoreThan24(uint n)
        {
            uint rv;
            if (n <= 24)
            {
                rv = ReadBitsNoRefillZero(n);
            }
            else
            {
                var hi = ReadBitsNoRefill(24) << (int)(n - 24);
                Refill();
                rv = hi + ReadBitsNoRefill(n - 24);
            }

            Refill();
            return rv;
        }

        public uint ReadDistance(byte value)
        {
            uint v = value;
            uint rv;
            if (v < 0xF0)
            {
                var n = (int)(v >> 4) + 4;
                var w = BitOperations.RotateLeft(Bits | 1, n);
                BitPos += n;
                var m = (2u << n) - 1;
                Bits = w & ~m;
                rv = ((w & m) << 4) + (v & 0xF) - 248;
            }
            else
            {
                var n = (int)(v - 0xF0) + 4;
                var w = BitOperations.RotateLeft(Bits | 1, n);
                BitPos += n;
                var m = (2u << n) - 1;
                Bits = w & ~m;
                rv = 8322816u + ((w & m) << 12);
                Refill();
                rv += Bits >> 20;
                BitPos += 12;
                Bits <<= 12;
            }

            Refill();
            return rv;
        }

        public bool ReadLength(out uint v)
        {
            var n = 31 - Bsr(Bits);
            if (n > 12)
            {
                v = 0;
                return false;
            }

            BitPos += (int)n;
            Bits = Shl(Bits, n);
            Refill();
            n += 7;
            BitPos += (int)n;
            v = Shr(Bits, 32 - (long)n) - 64;
            Bits = Shl(Bits, n);
            Refill();
            return true;
        }
    }

    private static uint Bsr(uint x) => x == 0 ? 0 : 31u - (uint)BitOperations.LeadingZeroCount(x);

    private static uint Bsf(uint x) => x == 0 ? 0 : (uint)BitOperations.TrailingZeroCount(x);

    private static uint Clz(uint x) => x == 0 ? 31 : (uint)BitOperations.LeadingZeroCount(x);
}
