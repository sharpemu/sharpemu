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
    /// Matches the 12-byte idiom, extracting the destination register D and the
    /// source (scratch) register N:
    /// <code>
    ///   EXTRQ    xmmN, 0x28, 0x00     ; 66 0F 78 /0 28 00      mask xmmN to low 40 bits
    ///   VPBLENDD xmmD, xmmD, xmmN, 2  ; C4 E3 vvvv 02 /r 02    copy dword 1 into xmmD
    /// </code>
    /// N lives in the ModRM r/m field of both instructions; D (the blend
    /// destination and src1) lives in the VPBLENDD ModRM reg field and VEX.vvvv.
    /// Both are xmm0-xmm7 (the VEX byte1 0xE3 pins R/X/B, so no xmm8-15 extension).
    /// The compiler allocates whichever registers it likes — Dead Cells builds use
    /// D=xmm0 and D=xmm3, others differ — so both are read from the encoding.
    /// </summary>
    public static bool TryMatch(ReadOnlySpan<byte> source, out int destRegister, out int srcRegister)
    {
        destRegister = -1;
        srcRegister = -1;
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

        var n = source[3] & 0x07;

        // VPBLENDD xmmD, xmmD, xmmN, 2 : C4 E3 <W=0 vvvv=~D L=0 pp=01> 02 ModRM 02.
        // VEX.byte2 fixed bits (W, L, pp) must read 0b*0000*01; vvvv encodes ~D.
        if (source[6] != 0xC4 || source[7] != 0xE3 || (source[8] & 0x87) != 0x01 ||
            source[9] != 0x02 || source[11] != 0x02)
        {
            return false;
        }

        var d = (~(source[8] >> 3)) & 0x0F;
        if (d > 7)
        {
            return false;
        }

        // ModRM: mod=11, reg=D (dest = src1), rm=N (src2 = the masked register).
        if (source[10] != (0xC0 | (d << 3) | n))
        {
            return false;
        }

        destRegister = d;
        srcRegister = n;
        return true;
    }

    /// <summary>
    /// Writes the SSE4.1 equivalent into <paramref name="destination"/>:
    /// <code>
    ///   PEXTRB eax, xmmN, 4  ; 66 0F 3A 14 /r 04   extract byte 4 (zero-extended)
    ///   PINSRD xmmD, eax, 1  ; 66 0F 3A 22 /r 01   insert into xmmD dword lane 1
    /// </code>
    /// After EXTRQ masks xmmN to its low 40 bits, dword 1 is just byte 4
    /// zero-extended, so the two-instruction extract/insert reproduces the exact
    /// observable result the AMD idiom left in xmmD. eax is a caller-dead scratch
    /// at every site the compiler emits this idiom.
    /// </summary>
    public static bool TryEncode(int destRegister, int srcRegister, Span<byte> destination)
    {
        if ((uint)destRegister > 7 || (uint)srcRegister > 7 || destination.Length < SequenceLength)
        {
            return false;
        }

        // PEXTRB eax, xmmN, 4 : ModRM (mod=11 reg=N rm=000 -> eax), imm8 = byte index 4.
        destination[0] = 0x66;
        destination[1] = 0x0F;
        destination[2] = 0x3A;
        destination[3] = 0x14;
        destination[4] = (byte)(0xC0 | (srcRegister << 3));
        destination[5] = 0x04;

        // PINSRD xmmD, eax, 1 : ModRM (mod=11 reg=D -> xmmD, rm=000 -> eax), lane 1.
        destination[6] = 0x66;
        destination[7] = 0x0F;
        destination[8] = 0x3A;
        destination[9] = 0x22;
        destination[10] = (byte)(0xC0 | (destRegister << 3));
        destination[11] = 0x01;
        return true;
    }
}
