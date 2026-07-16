// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// These exercise the pure BMI1/BMI2/ABM semantics used by the illegal-instruction software
// fallback. The expected values were computed by hand from the Intel/AMD definitions so a
// regression in the bit math or the flag handling fails here without needing a live guest.
public sealed class BmiInstructionEmulatorTests
{
    private const uint Carry = 1u << 0;
    private const uint Zero = 1u << 6;
    private const uint Sign = 1u << 7;
    private const uint Overflow = 1u << 11;
    private const uint DirectionFlag = 1u << 10; // Unrelated flag; must be preserved.

    [Fact]
    public void Andn_ClearsSourceBitsAndFlags()
    {
        uint flags = Overflow | Carry | DirectionFlag;
        var result = BmiInstructionEmulator.Andn(0x0000_00F0, 0x0000_00FF, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0x0000_000FUL, result);
        Assert.Equal(0u, flags & Carry);
        Assert.Equal(0u, flags & Overflow);
        Assert.Equal(0u, flags & Zero);
        Assert.Equal(0u, flags & Sign);
        Assert.Equal(DirectionFlag, flags & DirectionFlag);
    }

    [Fact]
    public void Andn_SetsZeroFlagWhenResultIsZero()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Andn(0xFF, 0xFF, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0UL, result);
        Assert.Equal(Zero, flags & Zero);
    }

    [Fact]
    public void Andn_SetsSignFlagOnHighBit32()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Andn(0, 0x8000_0000, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0x8000_0000UL, result);
        Assert.Equal(Sign, flags & Sign);
    }

    [Fact]
    public void Andn_MasksTo64Bits()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Andn(0xFFFF_FFFF_FFFF_FF00, 0xFF, GprOperandSize.Bits64, ref flags);

        Assert.Equal(0xFFUL, result);
    }

    [Theory]
    [InlineData(0x0000_000CUL, 0x0000_0004UL, false)] // isolate lowest set bit
    [InlineData(0x0000_0000UL, 0x0000_0000UL, true)]  // src == 0 -> CF clear, ZF set
    public void Blsi_IsolatesLowestBit(ulong src, ulong expected, bool zero)
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Blsi(src, GprOperandSize.Bits32, ref flags);

        Assert.Equal(expected, result);
        Assert.Equal(src != 0 ? Carry : 0u, flags & Carry);
        Assert.Equal(zero ? Zero : 0u, flags & Zero);
    }

    [Fact]
    public void Blsmsk_MasksThroughLowestBit()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Blsmsk(0x0000_000C, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0x0000_0007UL, result);
        Assert.Equal(0u, flags & Carry);
    }

    [Fact]
    public void Blsmsk_SetsCarryWhenSourceZero32()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Blsmsk(0, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0xFFFF_FFFFUL, result);
        Assert.Equal(Carry, flags & Carry);
        Assert.Equal(Sign, flags & Sign);
    }

    [Fact]
    public void Blsr_ResetsLowestBit()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Blsr(0x0000_000C, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0x0000_0008UL, result);
        Assert.Equal(0u, flags & Carry);
    }

    [Fact]
    public void Blsr_SetsCarryAndZeroWhenSourceZero()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Blsr(0, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0UL, result);
        Assert.Equal(Carry, flags & Carry);
        Assert.Equal(Zero, flags & Zero);
    }

    [Fact]
    public void Bextr_ExtractsBitField()
    {
        uint flags = 0;
        // start = 4, len = 8 -> bits [11:4] of 0x12345678 == 0x67
        var control = (8UL << 8) | 4UL;
        var result = BmiInstructionEmulator.Bextr(0x1234_5678, control, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0x0000_0067UL, result);
        Assert.Equal(0u, flags & Zero);
        Assert.Equal(0u, flags & Carry);
        Assert.Equal(0u, flags & Overflow);
    }

    [Fact]
    public void Bextr_StartBeyondWidthYieldsZero()
    {
        uint flags = 0;
        var control = (8UL << 8) | 40UL; // start = 40 >= 32
        var result = BmiInstructionEmulator.Bextr(0xFFFF_FFFF, control, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0UL, result);
        Assert.Equal(Zero, flags & Zero);
    }

    [Fact]
    public void Bzhi_ZeroesHighBits()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Bzhi(0xFFFF_FFFF, 8, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0x0000_00FFUL, result);
        Assert.Equal(0u, flags & Carry);
    }

    [Fact]
    public void Bzhi_SetsCarryWhenIndexAtOrBeyondWidth()
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Bzhi(0xFFFF_FFFF, 32, GprOperandSize.Bits32, ref flags);

        Assert.Equal(0xFFFF_FFFFUL, result);
        Assert.Equal(Carry, flags & Carry);
    }

    [Theory]
    [InlineData(0x0000_0008UL, GprOperandSize.Bits32, 3UL, false)]
    [InlineData(0x0000_0001UL, GprOperandSize.Bits32, 0UL, false)]
    [InlineData(0x0000_0000UL, GprOperandSize.Bits32, 32UL, true)]
    [InlineData(0x1_0000_0000UL, GprOperandSize.Bits64, 32UL, false)]
    public void Tzcnt_CountsTrailingZeros(ulong src, GprOperandSize size, ulong expected, bool carry)
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Tzcnt(src, size, ref flags);

        Assert.Equal(expected, result);
        Assert.Equal(carry ? Carry : 0u, flags & Carry);
    }

    [Theory]
    [InlineData(0x0000_0001UL, GprOperandSize.Bits32, 31UL, false)]
    [InlineData(0x8000_0000UL, GprOperandSize.Bits32, 0UL, false)]
    [InlineData(0x0000_0000UL, GprOperandSize.Bits32, 32UL, true)]
    [InlineData(0x0000_0001UL, GprOperandSize.Bits64, 63UL, false)]
    [InlineData(0x0000_0000UL, GprOperandSize.Bits64, 64UL, true)]
    public void Lzcnt_CountsLeadingZeros(ulong src, GprOperandSize size, ulong expected, bool carry)
    {
        uint flags = 0;
        var result = BmiInstructionEmulator.Lzcnt(src, size, ref flags);

        Assert.Equal(expected, result);
        Assert.Equal(carry ? Carry : 0u, flags & Carry);
    }

    [Theory]
    [InlineData(0x1234_5678UL, 8, GprOperandSize.Bits32, 0x7812_3456UL)]
    [InlineData(0x1234_5678UL, 0, GprOperandSize.Bits32, 0x1234_5678UL)]
    [InlineData(0x0123_4567_89AB_CDEFUL, 4, GprOperandSize.Bits64, 0xF012_3456_789A_BCDEUL)]
    public void Rorx_RotatesRight(ulong src, int count, GprOperandSize size, ulong expected)
    {
        Assert.Equal(expected, BmiInstructionEmulator.Rorx(src, count, size));
    }

    [Theory]
    [InlineData(0x8000_0000UL, 4, GprOperandSize.Bits32, 0xF800_0000UL)]
    [InlineData(0x8000_0000UL, 36, GprOperandSize.Bits32, 0xF800_0000UL)] // count masked to 4
    [InlineData(0x8000_0000_0000_0000UL, 4, GprOperandSize.Bits64, 0xF800_0000_0000_0000UL)]
    public void Sarx_ArithmeticShiftRight(ulong src, int count, GprOperandSize size, ulong expected)
    {
        Assert.Equal(expected, BmiInstructionEmulator.Sarx(src, count, size));
    }

    [Theory]
    [InlineData(0x0000_0001UL, 4, GprOperandSize.Bits32, 0x0000_0010UL)]
    [InlineData(0x0000_0001UL, 33, GprOperandSize.Bits32, 0x0000_0002UL)] // count masked to 1
    public void Shlx_LogicalShiftLeft(ulong src, int count, GprOperandSize size, ulong expected)
    {
        Assert.Equal(expected, BmiInstructionEmulator.Shlx(src, count, size));
    }

    [Theory]
    [InlineData(0x8000_0000UL, 4, GprOperandSize.Bits32, 0x0800_0000UL)]
    [InlineData(0x8000_0000_0000_0000UL, 4, GprOperandSize.Bits64, 0x0800_0000_0000_0000UL)]
    public void Shrx_LogicalShiftRight(ulong src, int count, GprOperandSize size, ulong expected)
    {
        Assert.Equal(expected, BmiInstructionEmulator.Shrx(src, count, size));
    }

    [Fact]
    public void PdepAndPext_AreInverseForContiguousSource()
    {
        var deposited = BmiInstructionEmulator.Pdep(0x0000_000F, 0x0000_00AA, GprOperandSize.Bits32);
        Assert.Equal(0x0000_00AAUL, deposited);

        var extracted = BmiInstructionEmulator.Pext(0x0000_00AA, 0x0000_00AA, GprOperandSize.Bits32);
        Assert.Equal(0x0000_000FUL, extracted);
    }

    [Fact]
    public void Pext_GathersSelectedBits()
    {
        // mask selects bit positions 1,3,5,7; source has 1s only at 5 and 7 -> packed 0b1100
        var extracted = BmiInstructionEmulator.Pext(0xF0, 0xAA, GprOperandSize.Bits32);
        Assert.Equal(0x0000_000CUL, extracted);
    }
}
