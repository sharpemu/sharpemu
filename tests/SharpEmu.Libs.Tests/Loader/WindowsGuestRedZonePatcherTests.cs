// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Iced.Intel;
using SharpEmu.Core.Loader;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class WindowsGuestRedZonePatcherTests
{
    [Theory]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x80 }, true)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0xF8 }, true)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x08 }, false)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x84, 0x24, 0x7F, 0xFF, 0xFF, 0xFF }, false)]
    public void RecognizesOnlyTheSystemVRedZone(byte[] instructionBytes, bool expected)
    {
        var instruction = Decode(instructionBytes);

        Assert.Equal(expected, WindowsGuestRedZonePatcher.UsesRedZone(instruction));
    }

    [Fact]
    public void RelocatesOnlyOrdinaryNonStackMemoryInstructions()
    {
        var ordinaryLoad = Decode([0x8B, 0x01]);
        var addressCalculation = Decode([0x48, 0x8D, 0x01]);
        var stackLoad = Decode([0x48, 0x8B, 0x44, 0x24, 0xF8]);

        Assert.True(WindowsGuestRedZonePatcher.IsFaultableGuestMemoryInstruction(ordinaryLoad));
        Assert.False(WindowsGuestRedZonePatcher.IsFaultableGuestMemoryInstruction(addressCalculation));
        Assert.False(WindowsGuestRedZonePatcher.IsFaultableGuestMemoryInstruction(stackLoad));
    }

    [Fact]
    public void EncodesAReachableRelativeJump()
    {
        Span<byte> destination = stackalloc byte[5];

        Assert.True(WindowsGuestRedZonePatcher.TryWriteRelativeJump(
            destination,
            instructionAddress: 0x1000,
            targetAddress: 0x1800));
        Assert.Equal(0xE9, destination[0]);
        Assert.Equal(0x7FB, BinaryPrimitives.ReadInt32LittleEndian(destination[1..]));
    }

    [Fact]
    public void RejectsAnUnreachableRelativeJump()
    {
        Span<byte> destination = stackalloc byte[5];

        Assert.False(WindowsGuestRedZonePatcher.TryWriteRelativeJump(
            destination,
            instructionAddress: 0x1000,
            targetAddress: 0x1_0000_1000));
    }

    private static Instruction Decode(byte[] instructionBytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(instructionBytes));
        decoder.Decode(out var instruction);
        Assert.NotEqual(Code.INVALID, instruction.Code);
        return instruction;
    }
}
