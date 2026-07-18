// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// The SSE4a EXTRQ+blend idiom raises #UD -> SIGILL under Rosetta 2, so the
/// loader rewrites it to SSE4.1 at boot. Sony's toolchain allocates the source
/// register freely (xmm1 for Dead Cells, xmm2 elsewhere), so the matcher must
/// read the register from the ModRM byte rather than assume a fixed one.
/// </summary>
public sealed class Sse4aExtrqBlendPatchTests
{
    // EXTRQ xmmN, 0x28, 0x00 ; VPBLENDD xmm0, xmm0, xmmN, 2 — parameterized on N.
    private static byte[] Idiom(int register) =>
    [
        0x66, 0x0F, 0x78, (byte)(0xC0 | register), 0x28, 0x00,
        0xC4, 0xE3, 0x79, 0x02, (byte)(0xC0 | register), 0x02,
    ];

    // PEXTRB eax, xmmN, 4 ; PINSRD xmm0, eax, 1.
    private static byte[] Replacement(int register) =>
    [
        0x66, 0x0F, 0x3A, 0x14, (byte)(0xC0 | (register << 3)), 0x04,
        0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01,
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)] // Dead Cells
    [InlineData(2)] // the register the old hard-coded matcher covered
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void MatchesIdiomForEveryLowXmmRegister(int register)
    {
        Assert.True(Sse4aExtrqBlendPatch.TryMatch(Idiom(register), out var matched));
        Assert.Equal(register, matched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void EncodesSse41ReplacementForRegister(int register)
    {
        var buffer = new byte[Sse4aExtrqBlendPatch.SequenceLength];
        Assert.True(Sse4aExtrqBlendPatch.TryEncode(register, buffer));
        Assert.Equal(Replacement(register), buffer);
    }

    [Fact]
    public void RoundTripsEveryLowRegisterThroughMatchThenEncode()
    {
        for (var register = 0; register <= 7; register++)
        {
            Assert.True(Sse4aExtrqBlendPatch.TryMatch(Idiom(register), out var matched));
            var buffer = new byte[Sse4aExtrqBlendPatch.SequenceLength];
            Assert.True(Sse4aExtrqBlendPatch.TryEncode(matched, buffer));
            Assert.Equal(Replacement(register), buffer);
        }
    }

    [Fact]
    public void PreservesTheOriginalHardCodedXmm2Encoding()
    {
        // Guards against a regression that would change behaviour for the one
        // register the previous literal matcher handled.
        var buffer = new byte[Sse4aExtrqBlendPatch.SequenceLength];
        Assert.True(Sse4aExtrqBlendPatch.TryEncode(2, buffer));
        Assert.Equal(
            new byte[] { 0x66, 0x0F, 0x3A, 0x14, 0xD0, 0x04, 0x66, 0x0F, 0x3A, 0x22, 0xC0, 0x01 },
            buffer);
    }

    [Fact]
    public void RejectsMismatchedRegistersAcrossTheTwoInstructions()
    {
        // EXTRQ names xmm1 but the blend names xmm2 — not the paired idiom.
        var mixed = Idiom(1);
        mixed[10] = 0xC0 | 2;
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(mixed, out _));
    }

    [Theory]
    [InlineData(0)] // wrong first byte
    [InlineData(2)] // wrong opcode
    [InlineData(4)] // wrong EXTRQ length immediate
    [InlineData(9)] // wrong blend opcode
    [InlineData(11)] // wrong blend immediate
    public void RejectsSequencesThatDifferFromTheIdiom(int corruptIndex)
    {
        var bytes = Idiom(2);
        bytes[corruptIndex] ^= 0xFF;
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(bytes, out _));
    }

    [Fact]
    public void RejectsNonModRmRegisterEncodings()
    {
        // A memory-form ModRM (mod != 11) is not the register-to-register idiom.
        var bytes = Idiom(2);
        bytes[3] = 0x02; // mod=00, rm=010 — memory operand, not xmm2 direct
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(bytes, out _));
    }

    [Fact]
    public void RejectsTooShortWindows()
    {
        Assert.False(Sse4aExtrqBlendPatch.TryMatch(Idiom(2).AsSpan(0, 11), out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void EncodeRejectsRegistersOutsideXmm0Through7(int register)
    {
        var buffer = new byte[Sse4aExtrqBlendPatch.SequenceLength];
        Assert.False(Sse4aExtrqBlendPatch.TryEncode(register, buffer));
    }
}
