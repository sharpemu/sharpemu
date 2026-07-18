// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// The SSE4a EXTRQ+blend idiom raises #UD -> SIGILL under Rosetta 2, so the
/// loader rewrites it to SSE4.1 at boot. Sony's toolchain allocates both the
/// blend destination and the scratch source register freely (one Dead Cells
/// build uses dest=xmm0, another dest=xmm3), so the matcher must read both from
/// the encoding rather than assume fixed registers.
/// </summary>
public sealed class Sse4aExtrqBlendPatchTests
{
    // EXTRQ xmmSrc, 0x28, 0x00 ; VPBLENDD xmmDest, xmmDest, xmmSrc, 2.
    private static byte[] Idiom(int dest, int src) =>
    [
        0x66, 0x0F, 0x78, (byte)(0xC0 | src), 0x28, 0x00,
        0xC4, 0xE3, (byte)(((~dest & 0xF) << 3) | 0x01), 0x02, (byte)(0xC0 | (dest << 3) | src), 0x02,
    ];

    // PEXTRB eax, xmmSrc, 4 ; PINSRD xmmDest, eax, 1.
    private static byte[] Replacement(int dest, int src) =>
    [
        0x66, 0x0F, 0x3A, 0x14, (byte)(0xC0 | (src << 3)), 0x04,
        0x66, 0x0F, 0x3A, 0x22, (byte)(0xC0 | (dest << 3)), 0x01,
    ];

    [Fact]
    public void MatchesEveryDestinationAndSourceCombination()
    {
        for (var dest = 0; dest <= 7; dest++)
        {
            for (var src = 0; src <= 7; src++)
            {
                Assert.True(
                    Sse4aExtrqBlendPatch.TryMatch(Idiom(dest, src), out var matchedDest, out var matchedSrc),
                    $"dest={dest} src={src}");
                Assert.Equal(dest, matchedDest);
                Assert.Equal(src, matchedSrc);
            }
        }
    }

    [Fact]
    public void MatchesTheDeadCellsXmm3Xmm4Idiom()
    {
        // The exact bytes that faulted: EXTRQ xmm4,0x28,0x00 ; VPBLENDD xmm3,xmm3,xmm4,2.
        byte[] bytes = [0x66, 0x0F, 0x78, 0xC4, 0x28, 0x00, 0xC4, 0xE3, 0x61, 0x02, 0xDC, 0x02];
        Assert.True(Sse4aExtrqBlendPatch.TryMatch(bytes, out var dest, out var src));
        Assert.Equal(3, dest);
        Assert.Equal(4, src);
    }

    [Fact]
    public void RoundTripsEveryCombinationThroughMatchThenEncode()
    {
        for (var dest = 0; dest <= 7; dest++)
        {
            for (var src = 0; src <= 7; src++)
            {
                Assert.True(Sse4aExtrqBlendPatch.TryMatch(Idiom(dest, src), out var matchedDest, out var matchedSrc));
                var buffer = new byte[Sse4aExtrqBlendPatch.SequenceLength];
                Assert.True(Sse4aExtrqBlendPatch.TryEncode(matchedDest, matchedSrc, buffer));
                Assert.Equal(Replacement(dest, src), buffer);
            }
        }
    }

    [Fact]
    public void PreservesTheOriginalXmm0DestinationEncoding()
    {
        // Guards the behaviour the previous matcher (dest fixed to xmm0) produced.
        var buffer = new byte[Sse4aExtrqBlendPatch.SequenceLength];
        Assert.True(Sse4aExtrqBlendPatch.TryEncode(destRegister: 0, srcRegister: 2, buffer));
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x14, 0xD0, 0x04, 0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01 },
            buffer);
    }

    [Fact]
    public void RejectsMismatchedSourceAcrossTheTwoInstructions()
    {
        // EXTRQ masks xmm1 but the blend reads xmm2 — not the paired idiom.
        var mixed = Idiom(dest: 0, src: 1);
        mixed[10] = 0xC0 | 2;
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(mixed, out _, out _));
    }

    [Theory]
    [InlineData(0)] // wrong first byte
    [InlineData(2)] // wrong opcode
    [InlineData(4)] // wrong EXTRQ length immediate
    [InlineData(9)] // wrong blend opcode
    [InlineData(11)] // wrong blend immediate
    public void RejectsSequencesThatDifferFromTheIdiom(int corruptIndex)
    {
        var bytes = Idiom(dest: 0, src: 2);
        bytes[corruptIndex] ^= 0xFF;
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(bytes, out _, out _));
    }

    [Fact]
    public void RejectsNonModRmRegisterEncodings()
    {
        // A memory-form ModRM (mod != 11) is not the register-to-register idiom.
        var bytes = Idiom(dest: 0, src: 2);
        bytes[3] = 0x02; // mod=00, rm=010 — memory operand, not xmm2 direct
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(bytes, out _, out _));
    }

    [Fact]
    public void RejectsTooShortWindows()
    {
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(Idiom(dest: 0, src: 2).AsSpan(0, 11), out _, out _));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(8, 0)]
    [InlineData(0, 8)]
    public void EncodeRejectsRegistersOutsideXmm0Through7(int dest, int src)
    {
        var buffer = new byte[Sse4aExtrqBlendPatch.SequenceLength];
        Assert.False(Sse4aExtrqBlendPatch.TryEncode(dest, src, buffer));
    }
}
