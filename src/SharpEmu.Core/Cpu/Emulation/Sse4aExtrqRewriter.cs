// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Rewrites the AMD SSE4a EXTRQ idiom that Sony's toolchain emits for the PS5's Zen 2 cores into an
/// equivalent SSE4.1 sequence at load time.
///
/// SSE4a is an AMD-only extension; Intel has never shipped it. When the direct-execution backend runs
/// the guest natively, the <c>extrq</c> opcode raises #UD on any Intel host (and on Rosetta 2), while
/// the AVX/AVX2 code around it runs fine. The particular shape the toolchain produces is a fixed-width
/// bit extract followed immediately by a <c>vpblendd</c> that folds the extracted dword back into
/// <c>xmm0</c>. This class recognises that pair and produces a <c>pextrb</c>/<c>pinsrd</c> replacement
/// with the same observable effect, which the backend patches over the guest bytes before first run.
///
/// The matcher is pure and works on plain byte spans so the encoding can be unit-tested on its own; the
/// unsafe page-protection and instruction-cache plumbing stays in the backend adapter.
/// </summary>
public static class Sse4aExtrqRewriter
{
    /// <summary>Byte length of both the recognised idiom and its replacement. They are the same size,
    /// so the patch is an in-place overwrite with no relocation.</summary>
    public const int SequenceLength = 12;

    /// <summary>
    /// Matches
    /// <code>
    ///   66 0F 78 /0 28 00      extrq    xmmN, 0x28, 0
    ///   C4 E3 79 02 C0|N 02    vpblendd xmm0, xmm0, xmmN, 2
    /// </code>
    /// for any scratch register <c>xmmN</c> (N in 0..7) and, on a match, writes
    /// <code>
    ///   66 0F 3A 14 (N&lt;&lt;3) 04   pextrb eax, xmmN, 4
    ///   66 0F 3A 22 C0 01           pinsrd xmm0, eax, 1
    /// </code>
    /// into <paramref name="replacement"/>.
    ///
    /// Why these two instructions are equivalent: <c>extrq xmmN, 0x28, 0</c> keeps the low 40 bits of
    /// <c>xmmN</c> and zeroes the rest, so the second dword <c>vpblendd</c> then copies into
    /// <c>xmm0</c> is just byte 4 of the original <c>xmmN</c>, zero-extended. <c>pextrb</c> reads that
    /// same byte into EAX and <c>pinsrd</c> drops it into dword lane 1 of <c>xmm0</c>. The extract
    /// length (0x28) and index (0) are matched exactly rather than decoded, because the replacement's
    /// byte offset of 4 is only the right answer for that specific field.
    ///
    /// Unlike the original, the replacement clobbers EAX instead of <c>xmmN</c>. That trade already
    /// existed in the hand-written patch this generalises; the toolchain only uses <c>xmmN</c> here as a
    /// throwaway for the extract, so nothing downstream depends on it.
    /// </summary>
    /// <param name="source">Guest bytes at the candidate instruction. Only the first
    /// <see cref="SequenceLength"/> bytes are inspected.</param>
    /// <param name="replacement">Destination for the replacement encoding. Written only on a match, and
    /// must be at least <see cref="SequenceLength"/> bytes.</param>
    /// <returns><see langword="true"/> with <paramref name="replacement"/> filled when the idiom is
    /// recognised; otherwise <see langword="false"/> with <paramref name="replacement"/> left alone.</returns>
    public static bool TryRewrite(ReadOnlySpan<byte> source, Span<byte> replacement)
    {
        if (source.Length < SequenceLength || replacement.Length < SequenceLength)
        {
            return false;
        }

        // extrq xmmN, 0x28, 0. The ModRM byte must have mod=11 and reg=000 (reg is the /0 opcode
        // extension for this encoding, not an operand); the low three bits select the register.
        if (source[0] != 0x66 || source[1] != 0x0F || source[2] != 0x78 ||
            (source[3] & 0xF8) != 0xC0 || source[4] != 0x28 || source[5] != 0x00)
        {
            return false;
        }

        var register = source[3] & 0x07;

        // vpblendd xmm0, xmm0, xmmN, 2 (VEX.128.66.0F3A.W0). Destination and first source are fixed to
        // xmm0 by the C4 E3 79 prefix and the reg=000 field; the second source register (ModRM rm) has
        // to be the same xmmN the extract just wrote, or this isn't the idiom.
        if (source[6] != 0xC4 || source[7] != 0xE3 || source[8] != 0x79 || source[9] != 0x02 ||
            source[10] != (0xC0 | register) || source[11] != 0x02)
        {
            return false;
        }

        // pextrb eax, xmmN, 4
        replacement[0] = 0x66;
        replacement[1] = 0x0F;
        replacement[2] = 0x3A;
        replacement[3] = 0x14;
        replacement[4] = (byte)(0xC0 | (register << 3));
        replacement[5] = 0x04;
        // pinsrd xmm0, eax, 1
        replacement[6] = 0x66;
        replacement[7] = 0x0F;
        replacement[8] = 0x3A;
        replacement[9] = 0x22;
        replacement[10] = 0xC0;
        replacement[11] = 0x01;
        return true;
    }
}
