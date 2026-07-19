// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// The four Intel SHA-extension operations used by SHA-1 code. This is the arithmetic half of
/// the direct-execution SIGILL fallback used when an x86-64 guest runs under Rosetta 2, which
/// does not expose Intel SHA instructions to translated processes.
/// </summary>
public static class Sha1InstructionEmulator
{
    public static Sha1Vector MessageSchedule1(Sha1Vector destination, Sha1Vector source) => new(
        destination.Lane0 ^ source.Lane2,
        destination.Lane1 ^ source.Lane3,
        destination.Lane2 ^ destination.Lane0,
        destination.Lane3 ^ destination.Lane1);

    public static Sha1Vector MessageSchedule2(Sha1Vector destination, Sha1Vector source)
    {
        var lane3 = BitOperations.RotateLeft(destination.Lane3 ^ source.Lane2, 1);
        return new Sha1Vector(
            BitOperations.RotateLeft(destination.Lane0 ^ lane3, 1),
            BitOperations.RotateLeft(destination.Lane1 ^ source.Lane0, 1),
            BitOperations.RotateLeft(destination.Lane2 ^ source.Lane1, 1),
            lane3);
    }

    public static Sha1Vector NextE(Sha1Vector destination, Sha1Vector source) => new(
        source.Lane0,
        source.Lane1,
        source.Lane2,
        unchecked(source.Lane3 + BitOperations.RotateLeft(destination.Lane3, 30)));

    public static Sha1Vector FourRounds(Sha1Vector destination, Sha1Vector source, byte function)
    {
        uint a = destination.Lane3;
        uint b = destination.Lane2;
        uint c = destination.Lane1;
        uint d = destination.Lane0;
        uint e = 0;

        uint constant = (function & 3) switch
        {
            0 => 0x5A82_7999u,
            1 => 0x6ED9_EBA1u,
            2 => 0x8F1B_BCDCu,
            _ => 0xCA62_C1D6u,
        };

        for (var round = 0; round < 4; round++)
        {
            uint choose = (function & 3) switch
            {
                0 => (b & c) ^ (~b & d),
                2 => (b & c) ^ (b & d) ^ (c & d),
                _ => b ^ c ^ d,
            };
            uint word = round switch
            {
                0 => source.Lane3,
                1 => source.Lane2,
                2 => source.Lane1,
                _ => source.Lane0,
            };
            uint next = unchecked(choose + BitOperations.RotateLeft(a, 5) + word + e + constant);
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = next;
        }

        return new Sha1Vector(d, c, b, a);
    }
}

/// <summary>The four little-endian 32-bit lanes of an XMM register.</summary>
public readonly record struct Sha1Vector(uint Lane0, uint Lane1, uint Lane2, uint Lane3);
