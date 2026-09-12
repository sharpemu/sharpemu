// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GuestRedZonePatcherTests
{
    [Theory]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x80 }, true)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0xF8 }, true)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x08 }, false)]
    [InlineData(new byte[] { 0x48, 0x8B, 0x84, 0x24, 0x7F, 0xFF, 0xFF, 0xFF }, false)]
    public void RecognizesOnlyTheSystemVRedZone(byte[] instructionBytes, bool expected)
    {
        var instruction = Decode(instructionBytes);

        Assert.Equal(expected, GuestRedZonePatcher.UsesRedZone(instruction));
    }

    [Fact]
    public void RelocatesOnlyOrdinaryNonStackMemoryInstructions()
    {
        var ordinaryLoad = Decode([0x8B, 0x01]);
        var addressCalculation = Decode([0x48, 0x8D, 0x01]);
        var stackLoad = Decode([0x48, 0x8B, 0x44, 0x24, 0xF8]);

        Assert.True(GuestRedZonePatcher.IsFaultableGuestMemoryInstruction(ordinaryLoad));
        Assert.False(GuestRedZonePatcher.IsFaultableGuestMemoryInstruction(addressCalculation));
        Assert.False(GuestRedZonePatcher.IsFaultableGuestMemoryInstruction(stackLoad));
    }

    [Fact]
    public void EncodesAReachableRelativeJump()
    {
        Span<byte> destination = stackalloc byte[5];

        Assert.True(GuestRedZonePatcher.TryWriteRelativeJump(
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

        Assert.False(GuestRedZonePatcher.TryWriteRelativeJump(
            destination,
            instructionAddress: 0x1000,
            targetAddress: 0x1_0000_1000));
    }

    [Fact]
    public unsafe void PatchesWindowsAndMacOsLeafFunctionWhileLeavingLinuxUnchanged()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        using var memory = new PhysicalVirtualMemory();
        const ulong imageSize = 0x10000;
        var imageBase = memory.AllocateAt(0, imageSize);
        const ulong expected = 0x1122_3344_5566_7788;
        // Save a value in the red zone before the memory read.
        // Read the saved value after the memory read and return it.
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0x89, 0x44, 0x24, 0xF8,
            0x8B, OperatingSystem.IsWindows() ? (byte)0x81 : (byte)0x87,
            0x00, 0x01, 0x00, 0x00,
            0x48, 0x8B, 0x44, 0x24, 0xF8,
            0xC3,
        ];
        Assert.True(memory.TryWrite(imageBase, function));

        // Define one function with absolute 64-bit pointers in the exception frame header.
        var exceptionFrameHeader = new byte[32];
        exceptionFrameHeader[0] = 1;
        exceptionFrameHeader[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(exceptionFrameHeader.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(exceptionFrameHeader.AsSpan(16), imageBase);
        Assert.True(memory.TryWrite(imageBase + 0x1000, exceptionFrameHeader));
        ProgramHeader[] headers =
        [
            CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute,
                0, (ulong)function.Length),
            CreateProgramHeader(ProgramHeaderType.GnuEhFrame, ProgramHeaderFlags.Read, 0x1000, (ulong)exceptionFrameHeader.Length),
        ];

        var result = GuestRedZonePatcher.Patch(memory, memory, headers, imageBase, imageSize);
        var patched = new byte[function.Length];
        Assert.True(memory.TryRead(imageBase, patched));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, result.RedZoneFunctions);
            Assert.Equal(1, result.PatchedSites);
            Assert.Equal(0, result.FailedSites);
            Assert.Equal(0xE9, patched[15]);
            var trampolineAddress = (ulong)((long)imageBase + 20 +
                BinaryPrimitives.ReadInt32LittleEndian(patched.AsSpan(16)));
            var trampoline = new byte[24];
            Assert.True(memory.TryRead(trampolineAddress, trampoline));
            Assert.Equal(new byte[] { 0x48, 0x8D, 0x64, 0x24, 0x80 }, trampoline[..5]);
            Assert.Equal(function[15..21], trampoline[5..11]);
            Assert.Equal(new byte[] { 0x48, 0x8D, 0xA4, 0x24, 0x80, 0, 0, 0 }, trampoline[11..19]);
            Assert.Equal(0xE9, trampoline[19]);
            Assert.Equal(imageBase + 21, (ulong)((long)trampolineAddress + 24 +
                BinaryPrimitives.ReadInt32LittleEndian(trampoline.AsSpan(20))));
        }
        else
        {
            Assert.Equal(0, result.PatchedSites);
            Assert.Equal(function, patched);
        }

        // Run the relocated code and compare the saved red zone value.
        // The code must restore the stack pointer before it returns.
        Assert.Equal(expected, ((delegate* unmanaged<ulong, ulong>)imageBase)(imageBase));
    }

    private static ProgramHeader CreateProgramHeader(
        ProgramHeaderType type, ProgramHeaderFlags flags, ulong address, ulong size)
    {
        Span<byte> bytes = stackalloc byte[56];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], size);
        return MemoryMarshal.Read<ProgramHeader>(bytes);
    }

    private static Instruction Decode(byte[] instructionBytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(instructionBytes));
        decoder.Decode(out var instruction);
        Assert.NotEqual(Code.INVALID, instruction.Code);
        return instruction;
    }
}
