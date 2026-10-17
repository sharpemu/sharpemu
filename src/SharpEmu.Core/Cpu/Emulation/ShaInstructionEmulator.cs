// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Calculates the results of the SHA extension instructions for instruction recovery.
/// Dword 0 of a value is bits 31:0 and dword 3 is bits 127:96, as in the Intel SDM.
/// </summary>
public static class ShaInstructionEmulator
{
    public static UInt128 Sha1Rnds4(UInt128 source1, UInt128 source2, int function)
    {
        var a = Dword(source1, 3);
        var b = Dword(source1, 2);
        var c = Dword(source1, 1);
        var d = Dword(source1, 0);
        var e = 0u;
        var k = (function & 3) switch
        {
            0 => 0x5A827999u,
            1 => 0x6ED9EBA1u,
            2 => 0x8F1BBCDCu,
            _ => 0xCA62C1D6u,
        };

        // The first message dword already carries E (SHA1NEXTE adds it).
        for (var round = 0; round < 4; round++)
        {
            var next = Sha1Function(function, b, c, d) + BitOperations.RotateLeft(a, 5) + Dword(source2, 3 - round) + e + k;
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = next;
        }

        return FromDwords(d, c, b, a);
    }

    public static UInt128 Sha1Nexte(UInt128 source1, UInt128 source2)
    {
        var e = BitOperations.RotateLeft(Dword(source1, 3), 30);
        return FromDwords(Dword(source2, 0), Dword(source2, 1), Dword(source2, 2), Dword(source2, 3) + e);
    }

    public static UInt128 Sha1Msg1(UInt128 source1, UInt128 source2)
    {
        var w0 = Dword(source1, 3);
        var w1 = Dword(source1, 2);
        var w2 = Dword(source1, 1);
        var w3 = Dword(source1, 0);
        var w4 = Dword(source2, 3);
        var w5 = Dword(source2, 2);
        return FromDwords(w5 ^ w3, w4 ^ w2, w3 ^ w1, w2 ^ w0);
    }

    public static UInt128 Sha1Msg2(UInt128 source1, UInt128 source2)
    {
        var w13 = Dword(source2, 2);
        var w14 = Dword(source2, 1);
        var w15 = Dword(source2, 0);
        var w16 = BitOperations.RotateLeft(Dword(source1, 3) ^ w13, 1);
        var w17 = BitOperations.RotateLeft(Dword(source1, 2) ^ w14, 1);
        var w18 = BitOperations.RotateLeft(Dword(source1, 1) ^ w15, 1);
        var w19 = BitOperations.RotateLeft(Dword(source1, 0) ^ w16, 1);
        return FromDwords(w19, w18, w17, w16);
    }

    // SHA256RNDS2 takes its two round-constant-plus-message dwords from XMM0[63:0].
    public static UInt128 Sha256Rnds2(UInt128 source1, UInt128 source2, UInt128 xmm0)
    {
        var a = Dword(source2, 3);
        var b = Dword(source2, 2);
        var c = Dword(source1, 3);
        var d = Dword(source1, 2);
        var e = Dword(source2, 1);
        var f = Dword(source2, 0);
        var g = Dword(source1, 1);
        var h = Dword(source1, 0);

        for (var round = 0; round < 2; round++)
        {
            var t1 = Ch(e, f, g) + BigSigma1(e) + Dword(xmm0, round) + h;
            var t2 = Maj(a, b, c) + BigSigma0(a);
            h = g;
            g = f;
            f = e;
            e = d + t1;
            d = c;
            c = b;
            b = a;
            a = t1 + t2;
        }

        return FromDwords(f, e, b, a);
    }

    public static UInt128 Sha256Msg1(UInt128 source1, UInt128 source2)
    {
        var w4 = Dword(source2, 0);
        var w3 = Dword(source1, 3);
        var w2 = Dword(source1, 2);
        var w1 = Dword(source1, 1);
        var w0 = Dword(source1, 0);
        return FromDwords(w0 + SmallSigma0(w1), w1 + SmallSigma0(w2), w2 + SmallSigma0(w3), w3 + SmallSigma0(w4));
    }

    public static UInt128 Sha256Msg2(UInt128 source1, UInt128 source2)
    {
        var w14 = Dword(source2, 2);
        var w15 = Dword(source2, 3);
        var w16 = Dword(source1, 0) + SmallSigma1(w14);
        var w17 = Dword(source1, 1) + SmallSigma1(w15);
        var w18 = Dword(source1, 2) + SmallSigma1(w16);
        var w19 = Dword(source1, 3) + SmallSigma1(w17);
        return FromDwords(w16, w17, w18, w19);
    }

    public static uint Dword(UInt128 value, int index) => (uint)(value >> (32 * index));

    public static UInt128 FromDwords(uint dword0, uint dword1, uint dword2, uint dword3) =>
        ((UInt128)dword3 << 96) | ((UInt128)dword2 << 64) | ((UInt128)dword1 << 32) | dword0;

    private static uint Sha1Function(int function, uint b, uint c, uint d) => (function & 3) switch
    {
        0 => (b & c) ^ (~b & d),
        2 => (b & c) ^ (b & d) ^ (c & d),
        _ => b ^ c ^ d,
    };

    private static uint Ch(uint e, uint f, uint g) => (e & f) ^ (~e & g);

    private static uint Maj(uint a, uint b, uint c) => (a & b) ^ (a & c) ^ (b & c);

    private static uint BigSigma0(uint x) =>
        BitOperations.RotateRight(x, 2) ^ BitOperations.RotateRight(x, 13) ^ BitOperations.RotateRight(x, 22);

    private static uint BigSigma1(uint x) =>
        BitOperations.RotateRight(x, 6) ^ BitOperations.RotateRight(x, 11) ^ BitOperations.RotateRight(x, 25);

    private static uint SmallSigma0(uint x) =>
        BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3);

    private static uint SmallSigma1(uint x) =>
        BitOperations.RotateRight(x, 17) ^ BitOperations.RotateRight(x, 19) ^ (x >> 10);
}
