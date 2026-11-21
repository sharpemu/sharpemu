// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Intrinsics.X86;
using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// The hand-computed cases in BmiInstructionEmulatorTests only cover the values someone thought of.
// When the test host has the real instructions, compare the software results against the CPU over
// edge values and a fixed pseudo-random set. Hosts without the extensions skip these, which is the
// same situation the fallback is written for.
public sealed class BmiInstructionEmulatorHostDifferentialTests
{
    private const int RandomSamples = 20_000;

    [Fact]
    public void Bmi1MatchesHost()
    {
        if (!Bmi1.X64.IsSupported)
        {
            return;
        }

        foreach (var (a, b) in Samples())
        {
            uint flags = 0;
            Assert.Equal(Bmi1.X64.AndNot(a, b), BmiInstructionEmulator.Andn(a, b, GprOperandSize.Bits64, ref flags));
            Assert.Equal(Bmi1.AndNot((uint)a, (uint)b), BmiInstructionEmulator.Andn(a, b, GprOperandSize.Bits32, ref flags));

            Assert.Equal(Bmi1.X64.ExtractLowestSetBit(a), BmiInstructionEmulator.Blsi(a, GprOperandSize.Bits64, ref flags));
            Assert.Equal(Bmi1.ExtractLowestSetBit((uint)a), BmiInstructionEmulator.Blsi(a, GprOperandSize.Bits32, ref flags));
            Assert.Equal(Bmi1.X64.GetMaskUpToLowestSetBit(a), BmiInstructionEmulator.Blsmsk(a, GprOperandSize.Bits64, ref flags));
            Assert.Equal(Bmi1.GetMaskUpToLowestSetBit((uint)a), BmiInstructionEmulator.Blsmsk(a, GprOperandSize.Bits32, ref flags));
            Assert.Equal(Bmi1.X64.ResetLowestSetBit(a), BmiInstructionEmulator.Blsr(a, GprOperandSize.Bits64, ref flags));
            Assert.Equal(Bmi1.ResetLowestSetBit((uint)a), BmiInstructionEmulator.Blsr(a, GprOperandSize.Bits32, ref flags));

            Assert.Equal(Bmi1.X64.TrailingZeroCount(a), BmiInstructionEmulator.Tzcnt(a, GprOperandSize.Bits64, ref flags));
            Assert.Equal(Bmi1.TrailingZeroCount((uint)a), BmiInstructionEmulator.Tzcnt(a, GprOperandSize.Bits32, ref flags));

            // BEXTR only reads control[15:0]; keep that part interesting rather than uniform.
            var control = b & 0xFFFF;
            Assert.Equal(
                Bmi1.X64.BitFieldExtract(a, (ushort)control),
                BmiInstructionEmulator.Bextr(a, control, GprOperandSize.Bits64, ref flags));
            Assert.Equal(
                Bmi1.BitFieldExtract((uint)a, (ushort)control),
                BmiInstructionEmulator.Bextr(a, control, GprOperandSize.Bits32, ref flags));
        }
    }

    [Fact]
    public void LzcntMatchesHost()
    {
        if (!Lzcnt.X64.IsSupported)
        {
            return;
        }

        foreach (var (a, _) in Samples())
        {
            uint flags = 0;
            Assert.Equal(Lzcnt.X64.LeadingZeroCount(a), BmiInstructionEmulator.Lzcnt(a, GprOperandSize.Bits64, ref flags));
            Assert.Equal(Lzcnt.LeadingZeroCount((uint)a), BmiInstructionEmulator.Lzcnt(a, GprOperandSize.Bits32, ref flags));
        }
    }

    [Fact]
    public unsafe void Bmi2MatchesHost()
    {
        if (!Bmi2.X64.IsSupported)
        {
            return;
        }

        foreach (var (a, b) in Samples())
        {
            uint flags = 0;
            var index = b & 0xFF;
            Assert.Equal(Bmi2.X64.ZeroHighBits(a, index), BmiInstructionEmulator.Bzhi(a, index, GprOperandSize.Bits64, ref flags));
            Assert.Equal(Bmi2.ZeroHighBits((uint)a, (uint)index), BmiInstructionEmulator.Bzhi(a, index, GprOperandSize.Bits32, ref flags));

            Assert.Equal(Bmi2.X64.ParallelBitDeposit(a, b), BmiInstructionEmulator.Pdep(a, b, GprOperandSize.Bits64));
            Assert.Equal(Bmi2.ParallelBitDeposit((uint)a, (uint)b), BmiInstructionEmulator.Pdep(a, b, GprOperandSize.Bits32));
            Assert.Equal(Bmi2.X64.ParallelBitExtract(a, b), BmiInstructionEmulator.Pext(a, b, GprOperandSize.Bits64));
            Assert.Equal(Bmi2.ParallelBitExtract((uint)a, (uint)b), BmiInstructionEmulator.Pext(a, b, GprOperandSize.Bits32));

            ulong hostLow64;
            var hostHigh64 = Bmi2.X64.MultiplyNoFlags(a, b, &hostLow64);
            Assert.Equal(hostHigh64, BmiInstructionEmulator.Mulx(a, b, GprOperandSize.Bits64, out var low64));
            Assert.Equal(hostLow64, low64);

            uint hostLow32;
            var hostHigh32 = Bmi2.MultiplyNoFlags((uint)a, (uint)b, &hostLow32);
            Assert.Equal(hostHigh32, BmiInstructionEmulator.Mulx(a, b, GprOperandSize.Bits32, out var low32));
            Assert.Equal(hostLow32, low32);
        }
    }

    private static IEnumerable<(ulong A, ulong B)> Samples()
    {
        ulong[] edges =
        [
            0, 1, 2, 0x7F, 0x80, 0xFF, 0x100, 0x1F, 0x20, 0x3F, 0x40, 0x41,
            0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF, 0x1_0000_0000,
            0x7FFF_FFFF_FFFF_FFFF, 0x8000_0000_0000_0000, ulong.MaxValue,
            0x0000_2010, 0x0000_3F00, 0x0000_4000, 0x0000_FF20, 0xDEAD_BEEF_CAFE_F00D,
        ];

        foreach (var a in edges)
        {
            foreach (var b in edges)
            {
                yield return (a, b);
            }
        }

        var random = new System.Random(683);
        for (var i = 0; i < RandomSamples; i++)
        {
            yield return ((ulong)random.NextInt64() ^ ((ulong)random.Next() << 63),
                          (ulong)random.NextInt64() ^ ((ulong)random.Next() << 63));
        }
    }
}
