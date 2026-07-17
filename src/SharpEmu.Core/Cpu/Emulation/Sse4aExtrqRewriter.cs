// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Rewrites the AMD SSE4a <c>extrq</c> idiom that Sony's compiler emits into an
/// equivalent SSE4.1 sequence.
///
/// Guest PS5 code runs natively. The PS5's Zen 2 cores implement SSE4a, but Intel
/// hosts and Rosetta 2 do not, so the raw <c>extrq</c> opcode raises #UD and the
/// title aborts. The compiler emits a single fixed shape: extract the low 40 bits of
/// a scratch register, then fold that register's second dword into <c>xmm0</c>.
///
/// <code>
/// 66 0F 78 /0 28 00        extrq    xmmN, 0x28, 0
/// C4 E3 79 02 /r 02        vpblendd xmm0, xmm0, xmmN, 2
/// </code>
///
/// After <c>extrq xmmN, 0x28, 0</c> only the low 40 bits of <c>xmmN</c> survive, so
/// the dword the blend takes (lane 1) is just byte 4 of the original register,
/// zero-extended. <c>pextrb</c>/<c>pinsrd</c> reproduces exactly that, using EAX as a
/// throwaway scratch:
///
/// <code>
/// 66 0F 3A 14 /r 04        pextrb eax, xmmN, 4
/// 66 0F 3A 22 C0 01        pinsrd xmm0, eax, 1
/// </code>
///
/// The scratch register N is read out of the ModRM byte rather than compared against
/// a literal, so every register the compiler picks (xmm0–xmm7) is covered. The
/// extract field (length 0x28, index 0) and the xmm0 destination stay pinned: the
/// byte-4 offset in the replacement is only correct for that field, so a different
/// extract is deliberately left unmatched instead of rewritten wrongly.
///
/// The unsafe memory-protection and instruction-cache plumbing lives in the backend;
/// this class only decides whether a byte window matches and what to write back, so
/// the equivalence can be unit-tested in isolation.
/// </summary>
public static class Sse4aExtrqRewriter
{
    /// <summary>Length in bytes of both the matched idiom and its replacement.</summary>
    public const int SequenceLength = 12;

    /// <summary>
    /// Matches the <c>extrq</c> + <c>vpblendd</c> idiom at the start of
    /// <paramref name="source"/> and, when it matches, writes the SSE4.1 replacement
    /// into <paramref name="replacement"/>.
    /// </summary>
    /// <param name="source">Instruction bytes at the faulting address.</param>
    /// <param name="replacement">Receives the 12-byte replacement when the idiom matches.</param>
    /// <returns><see langword="true"/> when the idiom matched and a replacement was produced.</returns>
    public static bool TryGetReplacement(ReadOnlySpan<byte> source, Span<byte> replacement)
    {
        if (source.Length < SequenceLength || replacement.Length < SequenceLength)
        {
            return false;
        }

        // extrq xmmN, 0x28, 0 is the /0 form, so the ModRM byte is 0xC0|N (N = 0..7).
        var modrm = source[3];
        if (source[0] != 0x66 || source[1] != 0x0F || source[2] != 0x78 ||
            (modrm & 0xF8) != 0xC0 || source[4] != 0x28 || source[5] != 0x00)
        {
            return false;
        }

        // vpblendd xmm0, xmm0, xmmN, 2: the same register, an xmm0 destination and a
        // blend mask of 2 all fall out of requiring the ModRM byte to equal the extrq's.
        if (source[6] != 0xC4 || source[7] != 0xE3 || source[8] != 0x79 || source[9] != 0x02 ||
            source[10] != modrm || source[11] != 0x02)
        {
            return false;
        }

        var register = modrm & 0x07;

        // pextrb eax, xmmN, 4: xmmN is the reg field, EAX (0) the r/m destination.
        replacement[0] = 0x66;
        replacement[1] = 0x0F;
        replacement[2] = 0x3A;
        replacement[3] = 0x14;
        replacement[4] = (byte)(0xC0 | (register << 3));
        replacement[5] = 0x04;

        // pinsrd xmm0, eax, 1: insert the extracted byte into xmm0's second dword.
        replacement[6] = 0x66;
        replacement[7] = 0x0F;
        replacement[8] = 0x3A;
        replacement[9] = 0x22;
        replacement[10] = 0xC0;
        replacement[11] = 0x01;

        return true;
    }
}
