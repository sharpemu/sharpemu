// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Recognizes Sony's AMD-only SSE4a EXTRQ+blend idiom and rewrites it into an
/// equivalent SSE4.1 sequence. SharpEmu executes guest x86-64 natively, but
/// Rosetta 2 and Intel hosts do not implement SSE4a, so the original opcode
/// raises #UD -> SIGILL. The compiler emits the idiom against whichever XMM
/// register it happens to allocate (Dead Cells uses xmm1, others xmm2), so the
/// source register is read from the ModRM r/m field rather than hard-coded.
///
/// The match/encode logic is deliberately free of native page-patching so it
/// can be unit-tested against handcrafted byte sequences.
/// </summary>
public static class Sse4aExtrqBlendPatch
{
    /// <summary>Length in bytes of both the matched idiom and its replacement.</summary>
    public const int SequenceLength = 12;

    /// <summary>
    /// Matches the 12-byte idiom, extracting the source (scratch) register N:
    /// <code>
    ///   EXTRQ    xmmN, 0x28, 0x00     ; 66 0F 78 /0 28 00      mask xmmN to low 40 bits
    ///   VPBLENDD xmm0, xmm0, xmmN, 2  ; C4 E3 79 02 /r 02      copy dword 1 into xmm0
    /// </code>
    /// N (xmm0-xmm7) lives in the ModRM r/m field of both instructions and must
    /// be identical in each; the destination (xmm0) is fixed by the idiom. The
    /// VEX bytes pin src1 to xmm0 and the register range to xmm0-xmm7.
    /// </summary>
    public static bool TryMatch(ReadOnlySpan<byte> source, out int xmmRegister)
    {
        xmmRegister = -1;
        if (source.Length < SequenceLength)
        {
            return false;
        }

        // EXTRQ xmmN, 0x28, 0x00 : 66 0F 78, ModRM (mod=11 reg=000 rm=N), 28, 00.
        if (source[0] != 0x66 || source[1] != 0x0F || source[2] != 0x78 ||
            (source[3] & 0xF8) != 0xC0 || source[4] != 0x28 || source[5] != 0x00)
        {
            return false;
        }

        var register = source[3] & 0x07;

        // VPBLENDD xmm0, xmm0, xmmN, 2 : C4 E3 79 02, ModRM (mod=11 reg=000 rm=N), 02.
        // The r/m field must name the same register the EXTRQ masked.
        if (source[6] != 0xC4 || source[7] != 0xE3 || source[8] != 0x79 || source[9] != 0x02 ||
            source[10] != (0xC0 | register) || source[11] != 0x02)
        {
            return false;
        }

        xmmRegister = register;
        return true;
    }

    /// <summary>
    /// Writes the SSE4.1 equivalent for the given source register into
    /// <paramref name="destination"/>:
    /// <code>
    ///   PEXTRB eax, xmmN, 4  ; 66 0F 3A 14 /r 04   extract byte 4 (zero-extended)
    ///   PINSRD xmm0, eax, 1  ; 66 0F 3A 22 /r 01   insert into xmm0 dword lane 1
    /// </code>
    /// After EXTRQ masks xmmN to its low 40 bits, dword 1 is just byte 4
    /// zero-extended, so the two-instruction extract/insert reproduces the exact
    /// observable result the AMD idiom left in xmm0.
    /// </summary>
    public static bool TryEncode(int xmmRegister, Span<byte> destination)
    {
        if ((uint)xmmRegister > 7 || destination.Length < SequenceLength)
        {
            return false;
        }

        // PEXTRB eax, xmmN, 4 : ModRM (mod=11 reg=N rm=000 -> eax), imm8 = byte index 4.
        destination[0] = 0x66;
        destination[1] = 0x0F;
        destination[2] = 0x3A;
        destination[3] = 0x14;
        destination[4] = (byte)(0xC0 | (xmmRegister << 3));
        destination[5] = 0x04;

        // PINSRD xmm0, eax, 1 : ModRM (mod=11 reg=000 -> xmm0, rm=000 -> eax), lane 1.
        destination[6] = 0x66;
        destination[7] = 0x0F;
        destination[8] = 0x3A;
        destination[9] = 0x22;
        destination[10] = 0xC0;
        destination[11] = 0x01;
        return true;
    }
}
