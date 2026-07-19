// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Pure software implementation of the bit-field math behind AMD's SSE4a EXTRQ/INSERTQ
/// (immediate-form) instructions.
///
/// The direct-execution backend runs guest PS5 code natively on the host CPU. The PS5's Zen 2
/// cores implement AMD-only SSE4a (EXTRQ/INSERTQ), but Intel hosts - and Rosetta 2 on Apple
/// Silicon - do not, so they raise #UD (STATUS_ILLEGAL_INSTRUCTION) instead of executing the
/// opcode. SharpEmu already rewrites one specific compiled EXTRQ+VPBLENDD idiom at load time
/// (see <see cref="Native.Sse4aExtrqBlendPatch"/>), but any other occurrence of EXTRQ/INSERTQ -
/// a different register allocation, a title built with a different compiler version, and so on
/// - still aborts the title. This class ported from Kyty's
/// <c>Loader::X64InstructionEmulator::TryEmulateSse4a</c> provides the general bit-field
/// extract/insert so the illegal-instruction handler can finish *any* immediate-form
/// EXTRQ/INSERTQ in software and resume, instead of relying on a single hard-coded byte pattern.
///
/// The methods operate on plain 64-bit integers rather than the OS CONTEXT record so the bit
/// math can be unit-tested in isolation; the unsafe CONTEXT/XMM plumbing lives in the backend
/// adapter (<see cref="Native.DirectExecutionBackend"/>).
/// </summary>
public static class Sse4aBitFieldEmulator
{
    public static bool IsValidBitField(int length, int index)
    {
        var len = length & 0x3F;
        var idx = index & 0x3F;
        return (len != 0 || idx == 0) && (len == 0 ? idx == 0 : idx + len <= 64);
    }

    public static ulong ExtractBitField(ulong value, int length, int index)
    {
        var len = length & 0x3F;
        var idx = index & 0x3F;
        if (!IsValidBitField(length, index))
        {
            return 0;
        }

        if (len == 0)
        {
            return value;
        }

        var mask = len == 64 ? ulong.MaxValue : (1UL << len) - 1;
        return (value >> idx) & mask;
    }

    public static ulong InsertBitField(ulong destination, ulong source, int length, int index)
    {
        var len = length & 0x3F;
        var idx = index & 0x3F;
        if (!IsValidBitField(length, index))
        {
            return destination;
        }

        if (len == 0)
        {
            return source;
        }

        var fieldMask = len == 64 ? ulong.MaxValue : (1UL << len) - 1;
        var destinationClearMask = fieldMask << idx;
        var sourceField = (source & fieldMask) << idx;
        return (destination & ~destinationClearMask) | sourceField;
    }
}
