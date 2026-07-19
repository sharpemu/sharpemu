// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// These exercise the pure EXTRQ/INSERTQ bit-field semantics used by the general SSE4a
// illegal-instruction software fallback (DirectExecutionBackend.Amd64Compat.cs). Expected values
// were computed from the AMD64 Architecture Programmer's Manual definitions and cross-checked
// with an independent Python re-implementation before being written here, so a regression in the
// ported bit math fails in this file without needing a live guest or a Windows host.
public sealed class Sse4aBitFieldEmulatorTests
{
    [Fact]
    public void ExtractBitField_ExtractsLowByte()
    {
        var result = Sse4aBitFieldEmulator.ExtractBitField(0x1234_5678_9ABC_DEF0, length: 8, index: 0);

        Assert.Equal(0xF0UL, result);
    }

    [Fact]
    public void ExtractBitField_ExtractsMidFieldAtNonZeroIndex()
    {
        // bits [31:16] of 0x1234_5678_9ABC_DEF0 == 0x9ABC
        var result = Sse4aBitFieldEmulator.ExtractBitField(0x1234_5678_9ABC_DEF0, length: 16, index: 16);

        Assert.Equal(0x9ABCUL, result);
    }

    [Fact]
    public void ExtractBitField_LengthZeroMeansSixtyFour()
    {
        var result = Sse4aBitFieldEmulator.ExtractBitField(0xFFFF_FFFF_FFFF_FFFF, length: 0, index: 0);

        Assert.Equal(0xFFFF_FFFF_FFFF_FFFFUL, result);
    }

    [Fact]
    public void ExtractBitField_MasksImmediatesToLowSixBits()
    {
        // length=0x28 (40) and index=0 is exactly the idiom SharpEmu's load-time
        // Sse4aExtrqBlendPatch already recognizes; the general emulator must agree with it.
        var result = Sse4aBitFieldEmulator.ExtractBitField(0x0000_0000_0000_00FF, length: 0x28, index: 0);

        Assert.Equal(0xFFUL, result);
    }

    [Theory]
    [InlineData(0x1234_5678_9ABC_DEF0UL)]
    [InlineData(0x0000_0000_0000_0000UL)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFFUL)]
    [InlineData(0x00FF_00FF_00FF_00FFUL)]
    public void ExtractBitField_AgreesWithSse4aExtrqBlendPatchsByteFourRule(ulong value)
    {
        // Sse4aExtrqBlendPatch's own comment states that after "EXTRQ xmmN, 0x28, 0x00", dword
        // lane 1 (bits 63:32) of the result equals byte 4 of the source zero-extended. The
        // general emulator (used for every other EXTRQ occurrence) must produce a result
        // consistent with that independently-reverse-engineered rule for the one idiom both
        // paths can be checked against.
        var extractedLow64 = Sse4aBitFieldEmulator.ExtractBitField(value, length: 0x28, index: 0);
        var dword1 = (uint)(extractedLow64 >> 32);
        var byteFourZeroExtended = (uint)((value >> 32) & 0xFF);

        Assert.Equal(byteFourZeroExtended, dword1);
    }

    [Fact]
    public void ExtractBitField_RejectsUndefinedFieldPastRegisterEnd()
    {
        Assert.False(Sse4aBitFieldEmulator.IsValidBitField(length: 8, index: 60));
        Assert.Equal(0UL, Sse4aBitFieldEmulator.ExtractBitField(
            0xFFFF_FFFF_FFFF_FFFF,
            length: 8,
            index: 60));
    }

    [Fact]
    public void ExtractBitField_RejectsZeroLengthAtNonZeroIndex()
    {
        Assert.False(Sse4aBitFieldEmulator.IsValidBitField(length: 0, index: 1));
    }

    [Fact]
    public void InsertBitField_InsertsFieldAtNonZeroIndexWithoutDisturbingOtherBits()
    {
        var result = Sse4aBitFieldEmulator.InsertBitField(
            destination: 0x0000_0000_0000_0000,
            source: 0xFFFF_FFFF_FFFF_FFFF,
            length: 8,
            index: 8);

        Assert.Equal(0x0000_0000_0000_FF00UL, result);
    }

    [Fact]
    public void InsertBitField_ClearsExactlyTheDestinationWindowBeforeInserting()
    {
        var result = Sse4aBitFieldEmulator.InsertBitField(
            destination: 0xFFFF_FFFF_FFFF_FFFF,
            source: 0x0000_0000_0000_0000,
            length: 16,
            index: 16);

        Assert.Equal(0xFFFF_FFFF_0000_FFFFUL, result);
    }

    [Fact]
    public void InsertBitField_InsertsLowByteAtIndexZero()
    {
        var result = Sse4aBitFieldEmulator.InsertBitField(
            destination: 0x1122_3344_5566_7788,
            source: 0xAABB_CCDD_EEFF_0011,
            length: 8,
            index: 0);

        Assert.Equal(0x1122_3344_5566_7711UL, result);
    }

    [Fact]
    public void InsertBitField_LengthZeroMeansSixtyFourAndOverwritesEverything()
    {
        var result = Sse4aBitFieldEmulator.InsertBitField(
            destination: 0x1111_1111_1111_1111,
            source: 0xFFFF_FFFF_FFFF_FFFF,
            length: 0,
            index: 0);

        Assert.Equal(0xFFFF_FFFF_FFFF_FFFFUL, result);
    }

    [Fact]
    public void InsertBitField_ZeroSourceFieldClearsOnlyItsOwnWindow()
    {
        // A zero-valued 12-bit field inserted at index 20 clears exactly bits [31:20]
        // (0x234 -> 0x000) and leaves every bit outside that window untouched.
        var result = Sse4aBitFieldEmulator.InsertBitField(
            destination: 0xABCD_EF01_2345_6789,
            source: 0,
            length: 12,
            index: 20);

        Assert.Equal(0xABCD_EF01_0005_6789UL, result);
    }
}
