// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[CollectionDefinition(GuestRedZonePatcherCollection.Name, DisableParallelization = true)]
public sealed class GuestRedZonePatcherCollection
{
    public const string Name = "GuestRedZonePatcher";
}

[Collection(GuestRedZonePatcherCollection.Name)]
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

    // After 'push rbp; mov rbp, rsp' and five more pushes, RSP = RBP - 0x28: [rbp-0x30] is below
    // RSP inside the red zone, [rbp-0x28] is the last pushed register, [rbp-0xB0] is below the zone.
    [Theory]
    [InlineData(new byte[] { 0x48, 0x89, 0x45, 0xD0 }, 0x28, true)]
    [InlineData(new byte[] { 0x48, 0x89, 0x45, 0xD8 }, 0x28, false)]
    [InlineData(new byte[] { 0x48, 0x89, 0x85, 0x50, 0xFF, 0xFF, 0xFF }, 0x28, false)]
    [InlineData(new byte[] { 0x48, 0x89, 0x45, 0xD0 }, -1, false)]
    [InlineData(new byte[] { 0x48, 0x89, 0x44, 0x24, 0xD0 }, 0x28, false)]
    public void RecognizesFramePointerSpillsBelowTheStackPointer(byte[] instructionBytes, int frameDelta, bool expected)
    {
        var instruction = Decode(instructionBytes);

        Assert.Equal(expected, GuestRedZonePatcher.UsesFramePointerRedZone(instruction, frameDelta));
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
        // Put the image at the start of a large free hole so the trampoline range beside it
        // is free too: the runtime's heaps around an arbitrary address can leave none in reach.
        const ulong holeSize = 0x4000000;
        var hole = (ulong)HostMemory.Alloc(null, (nuint)holeSize, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
        Assert.NotEqual(0UL, hole);
        Assert.True(HostMemory.Free((void*)hole, 0, HostMemory.MEM_RELEASE));
        var imageBase = memory.AllocateAt(hole, imageSize);
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

    // Demon's Souls FUN_800ad8fe0: a four-byte compare followed by JZ had no room
    // for the jump, so a fault there let Windows overwrite the saved red-zone pointer.
    [Theory]
    [InlineData(1u, 0x1122_3344_5566_7788UL)]
    [InlineData(0u, 0UL)]
    public unsafe void PatchesShortAccessFollowedByConditionalBranch(uint value, ulong expected)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var argument = OperatingSystem.IsWindows() ? (byte)0x79 : (byte)0x7F;
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, // mov rax, value
            0x48, 0x89, 0x44, 0x24, 0xF8,                               // mov [rsp-8], rax
            0x83, argument, 0x40, 0x00,                                 // cmp dword [arg+0x40], 0
            0x74, 0x06,                                                 // jz zero
            0x48, 0x8B, 0x44, 0x24, 0xF8,                               // mov rax, [rsp-8]
            0xC3,                                                       // ret
            0x31, 0xC0,                                                 // zero: xor eax, eax
            0xC3,                                                       // ret
        ];

        using var image = PatchedImage.Create(function, [(0x40, value)]);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, image.Result.PatchedSites);
            Assert.Equal(0xE9, image.ReadByte(15));
        }

        Assert.Equal(expected, ((delegate* unmanaged<ulong, ulong>)image.Base)(image.Base));
    }

    [Fact]
    public unsafe void PatchesShortAccessBeforeBranchTargetFromThePreviousInstruction()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var argument = OperatingSystem.IsWindows() ? (byte)0x91 : (byte)0x97;
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0x89, 0x44, 0x24, 0xF8,
            0x48, 0x8D, argument, 0x00, 0x20, 0x00, 0x00,
            0xB8, 0xFE, 0xFF, 0xFF, 0xFF,
            0x21, 0x02,
            0x48, 0x8B, 0x44, 0x24, 0xF8,
            0xC3,
            0xEB, 0xF8,
        ];

        using var image = PatchedImage.Create(function, [(0x2000, 7u)]);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, image.Result.PatchedSites);
            Assert.Equal(0xE9, image.ReadByte(22));
        }

        Assert.Equal(0x1122_3344_5566_7788UL, ((delegate* unmanaged<ulong, ulong>)image.Base)(image.Base));
        Assert.Equal(6u, image.ReadUInt32(0x2000));
    }

    [Fact]
    public unsafe void LeavesShortAccessBeforeBranchTargetUnchangedWhenAnIndirectBranchIsUnresolved()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        var argument = OperatingSystem.IsWindows() ? (byte)0x91 : (byte)0x97;
        byte[] function =
        [
            0x48, 0xB8, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11,
            0x48, 0x89, 0x44, 0x24, 0xF8,
            0x48, 0x8D, argument, 0x00, 0x20, 0x00, 0x00,
            0xB8, 0xFE, 0xFF, 0xFF, 0xFF,
            0x21, 0x02,
            0x48, 0x8B, 0x44, 0x24, 0xF8,
            0xC3,
            0xEB, 0xF8,
            0xFF, 0xE0,
        ];

        using var image = PatchedImage.Create(function, [(0x2000, 7u)]);
        Assert.Equal(0, image.Result.PatchedSites);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Equal(1, image.Result.RedZoneFunctions);
            Assert.Equal(1, image.Result.UnrelocatableSites);
            Assert.Equal(1, image.Result.IndirectBranchRefusals);
        }

        for (var index = 0; index < function.Length; index++)
        {
            Assert.Equal(function[index], image.ReadByte((ulong)index));
        }

        Assert.Equal(0x1122_3344_5566_7788UL, ((delegate* unmanaged<ulong, ulong>)image.Base)(image.Base));
        Assert.Equal(6u, image.ReadUInt32(0x2000));
    }

    private sealed unsafe class PatchedImage : IDisposable
    {
        private const ulong ImageSize = 0x10000;
        private readonly PhysicalVirtualMemory _memory;

        private PatchedImage(PhysicalVirtualMemory memory, ulong imageBase, GuestRedZonePatcher.PatchResult result)
        {
            _memory = memory;
            Base = imageBase;
            Result = result;
        }

        public ulong Base { get; }

        public GuestRedZonePatcher.PatchResult Result { get; }

        public static PatchedImage Create(byte[] function, (ulong Offset, uint Value)[] data)
        {
            var memory = new PhysicalVirtualMemory();
            const ulong holeSize = 0x4000000;
            var hole = (ulong)HostMemory.Alloc(null, (nuint)holeSize, HostMemory.MEM_RESERVE, HostMemory.PAGE_NOACCESS);
            Assert.NotEqual(0UL, hole);
            Assert.True(HostMemory.Free((void*)hole, 0, HostMemory.MEM_RELEASE));
            var imageBase = memory.AllocateAt(hole, ImageSize);
            Assert.True(memory.TryWrite(imageBase, function));
            Span<byte> word = stackalloc byte[sizeof(uint)];
            foreach (var (offset, value) in data)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(word, value);
                Assert.True(memory.TryWrite(imageBase + offset, word));
            }

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

            var result = GuestRedZonePatcher.Patch(memory, memory, headers, imageBase, ImageSize);
            return new PatchedImage(memory, imageBase, result);
        }

        public byte ReadByte(ulong offset)
        {
            Span<byte> value = stackalloc byte[1];
            Assert.True(_memory.TryRead(Base + offset, value));
            return value[0];
        }

        public uint ReadUInt32(ulong offset)
        {
            Span<byte> value = stackalloc byte[sizeof(uint)];
            Assert.True(_memory.TryRead(Base + offset, value));
            return BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

        public void Dispose() => _memory.Dispose();
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
