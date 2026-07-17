// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Covers the load-time rewrite of the AMD SSE4a EXTRQ idiom into SSE4.1 PEXTRB/PINSRD. The expected
// bytes were assembled by hand from the instruction encodings, so a regression in either the match or
// the replacement shows up here without needing a host that actually lacks SSE4a.
public sealed class Sse4aExtrqRewriterTests
{
    // extrq xmmN, 0x28, 0 ; vpblendd xmm0, xmm0, xmmN, 2
    private static byte[] Idiom(int register) =>
    [
        0x66, 0x0F, 0x78, (byte)(0xC0 | register), 0x28, 0x00,
        0xC4, 0xE3, 0x79, 0x02, (byte)(0xC0 | register), 0x02,
    ];

    // pextrb eax, xmmN, 4 ; pinsrd xmm0, eax, 1
    private static byte[] Replacement(int register) =>
    [
        0x66, 0x0F, 0x3A, 0x14, (byte)(0xC0 | (register << 3)), 0x04,
        0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01,
    ];

    // Before this fix the patch was hard-coded to xmm2. Keeping that exact case pinned makes sure the
    // generalisation didn't quietly change the encoding that already shipped and worked.
    [Fact]
    public void Xmm2_StillRewritesToTheOriginalHandWrittenBytes()
    {
        var replacement = new byte[Sse4aExtrqRewriter.SequenceLength];
        var matched = Sse4aExtrqRewriter.TryRewrite(Idiom(2), replacement);

        Assert.True(matched);
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x14, 0xD0, 0x04, 0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01 },
            replacement);
    }

    // The regression itself: Dead Cells (PPSA15552) emits the identical idiom against xmm1, which the
    // old fixed pattern missed by one byte, so the opcode reached the CPU and #UD'd.
    [Fact]
    public void Xmm1_IsNowRewritten()
    {
        var replacement = new byte[Sse4aExtrqRewriter.SequenceLength];
        var matched = Sse4aExtrqRewriter.TryRewrite(Idiom(1), replacement);

        Assert.True(matched);
        // pextrb reads from xmm1, so its ModRM reg field is 001 -> 0xC8.
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x14, 0xC8, 0x04, 0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01 },
            replacement);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void AnyScratchRegister_ExtractsFromThatSameRegister(int register)
    {
        var replacement = new byte[Sse4aExtrqRewriter.SequenceLength];
        var matched = Sse4aExtrqRewriter.TryRewrite(Idiom(register), replacement);

        Assert.True(matched);
        Assert.Equal(Replacement(register), replacement);
    }

    // If the extract and the blend name different registers it isn't the toolchain idiom, and rewriting
    // it would silently read from the wrong xmm.
    [Fact]
    public void MismatchedExtractAndBlendRegisters_AreRejected()
    {
        var bytes = Idiom(1);
        bytes[10] = 0xC2; // extract xmm1 but blend xmm2

        Assert.False(Sse4aExtrqRewriter.TryRewrite(bytes, new byte[Sse4aExtrqRewriter.SequenceLength]));
    }

    // The replacement's byte offset (4) is only correct for length 0x28 / index 0, so a different
    // extract field must not match.
    [Fact]
    public void DifferentExtractField_IsRejected()
    {
        var bytes = Idiom(1);
        bytes[4] = 0x20; // length 32 instead of 40

        Assert.False(Sse4aExtrqRewriter.TryRewrite(bytes, new byte[Sse4aExtrqRewriter.SequenceLength]));
    }

    // reg != 000 in the ModRM is a different /r opcode extension, not EXTRQ.
    [Fact]
    public void NonZeroModRmRegField_IsRejected()
    {
        var bytes = Idiom(1);
        bytes[3] = 0xC9; // mod=11, reg=001, rm=001

        Assert.False(Sse4aExtrqRewriter.TryRewrite(bytes, new byte[Sse4aExtrqRewriter.SequenceLength]));
    }

    [Fact]
    public void UnrelatedBytes_AreRejected()
    {
        var bytes = new byte[Sse4aExtrqRewriter.SequenceLength];
        Assert.False(Sse4aExtrqRewriter.TryRewrite(bytes, new byte[Sse4aExtrqRewriter.SequenceLength]));
    }

    [Fact]
    public void ShortInput_IsRejectedWithoutReading()
    {
        Assert.False(Sse4aExtrqRewriter.TryRewrite(Idiom(1).AsSpan(0, 11), new byte[Sse4aExtrqRewriter.SequenceLength]));
    }

    // A non-match must not scribble into the caller's buffer; the backend reuses one stack buffer per
    // scan position and only writes to guest memory when the return value is true.
    [Fact]
    public void NoMatch_LeavesReplacementBufferUntouched()
    {
        var replacement = new byte[Sse4aExtrqRewriter.SequenceLength];
        for (var i = 0; i < replacement.Length; i++)
        {
            replacement[i] = 0xAB;
        }

        Assert.False(Sse4aExtrqRewriter.TryRewrite(new byte[Sse4aExtrqRewriter.SequenceLength], replacement));
        Assert.All(replacement, b => Assert.Equal(0xAB, b));
    }
}
