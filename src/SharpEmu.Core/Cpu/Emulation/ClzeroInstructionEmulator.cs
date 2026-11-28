// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Emulation;

// CLZERO (0F 01 FC) is AMD-only and raises #UD on other hosts. It zeroes the cache line that
// contains the address in rAX; a 0x67 prefix selects the 32-bit eAX form in 64-bit mode.
public static class ClzeroInstructionEmulator
{
    public const int CacheLineSize = 64;

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out int length, out bool addressSize32)
    {
        addressSize32 = bytes.Length >= 4 && bytes[0] == 0x67;
        var start = addressSize32 ? 1 : 0;
        length = start + 3;
        return bytes.Length >= length &&
            bytes[start] == 0x0F && bytes[start + 1] == 0x01 && bytes[start + 2] == 0xFC;
    }

    public static ulong GetCacheLineAddress(ulong rax, bool addressSize32)
    {
        if (addressSize32)
        {
            rax &= 0xFFFF_FFFFUL;
        }

        return rax & ~(ulong)(CacheLineSize - 1);
    }

    public static unsafe void ZeroCacheLine(ulong rax, bool addressSize32)
    {
        new Span<byte>((void*)GetCacheLineAddress(rax, addressSize32), CacheLineSize).Clear();
    }
}
