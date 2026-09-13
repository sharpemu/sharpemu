// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Calculates the bit-field results for SSE4a instruction recovery.
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
        if (len == 0)
        {
            len = 64;
        }

        // Results past bit 63 are undefined; limit the field to the remaining bits.
        len = Math.Min(len, 64 - idx);
        var mask = len == 64 ? ulong.MaxValue : (1UL << len) - 1;
        return (value >> idx) & mask;
    }

    public static ulong InsertBitField(ulong destination, ulong source, int length, int index)
    {
        var len = length & 0x3F;
        var idx = index & 0x3F;
        if (len == 0)
        {
            len = 64;
        }

        len = Math.Min(len, 64 - idx);
        var fieldMask = len == 64 ? ulong.MaxValue : (1UL << len) - 1;
        var destinationClearMask = fieldMask << idx;
        var sourceField = (source & fieldMask) << idx;
        return (destination & ~destinationClearMask) | sourceField;
    }
}
