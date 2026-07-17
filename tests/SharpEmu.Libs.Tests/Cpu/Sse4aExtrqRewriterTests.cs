// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class Sse4aExtrqRewriterTests
{
    // extrq xmmN, 0x28, 0 ; vpblendd xmm0, xmm0, xmmN, 2 — the idiom Sony's toolchain emits.
    private static byte[] Idiom(byte register)
    {
        var modrm = (byte)(0xC0 | register);
        return
        [
            0x66, 0x0F, 0x78, modrm, 0x28, 0x00,
            0xC4, 0xE3, 0x79, 0x02, modrm, 0x02,
        ];
    }

    [Fact]
    public void Xmm2_ProducesTheBytesTheOriginalPatchShipped()
    {
        // Regression guard: the hand-written patch this replaced emitted exactly these
        // bytes for the xmm2 encoding, so the refactor must stay byte-identical for it.
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.True(Sse4aExtrqRewriter.TryGetReplacement(Idiom(2), replacement));
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x14, 0xD0, 0x04, 0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01 },
            replacement.ToArray());
    }

    [Fact]
    public void Xmm1_IsRewritten()
    {
        // #328: Dead Cells emits the idiom against xmm1, which the literal xmm2 match missed.
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.True(Sse4aExtrqRewriter.TryGetReplacement(Idiom(1), replacement));
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x14, 0xC8, 0x04, 0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01 },
            replacement.ToArray());
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
    public void EveryScratchRegister_IsRewritten(int register)
    {
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.True(Sse4aExtrqRewriter.TryGetReplacement(Idiom((byte)register), replacement));

        // pextrb eax, xmmN, 4 — only the source register byte varies with N.
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x14, (byte)(0xC0 | (register << 3)), 0x04 },
            replacement[..6].ToArray());
        // pinsrd xmm0, eax, 1 — constant across every register.
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01 },
            replacement[6..].ToArray());
    }

    [Fact]
    public void MismatchedExtractField_IsRejected()
    {
        // A different extract length needs different replacement math, so it must not match.
        var idiom = Idiom(1);
        idiom[4] = 0x20;
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.False(Sse4aExtrqRewriter.TryGetReplacement(idiom, replacement));
    }

    [Fact]
    public void DifferingBlendRegister_IsRejected()
    {
        // extrq on xmm1 but a vpblendd folding xmm2 is not the single idiom we rewrite.
        var idiom = Idiom(1);
        idiom[10] = 0xC2;
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.False(Sse4aExtrqRewriter.TryGetReplacement(idiom, replacement));
    }

    [Fact]
    public void NonZeroExtrqRegField_IsRejected()
    {
        // extrq is the /0 form; a set reg field decodes to a different instruction.
        var idiom = Idiom(1);
        idiom[3] = 0xCA; // mod=11, reg=001, rm=010
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.False(Sse4aExtrqRewriter.TryGetReplacement(idiom, replacement));
    }

    [Fact]
    public void UnrelatedBytes_AreRejected()
    {
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.False(Sse4aExtrqRewriter.TryGetReplacement(new byte[Sse4aExtrqRewriter.SequenceLength], replacement));
    }

    [Fact]
    public void ShortWindow_IsRejected()
    {
        Span<byte> replacement = stackalloc byte[Sse4aExtrqRewriter.SequenceLength];

        Assert.False(Sse4aExtrqRewriter.TryGetReplacement(Idiom(1).AsSpan(0, 11), replacement));
    }
}
