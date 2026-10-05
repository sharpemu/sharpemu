// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Iced.Intel;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class RosettaVectorStorePatchTests
{
    [Theory]
    [InlineData(new byte[] { 0xC4, 0xC1, 0x7C, 0x11, 0x86, 0x90, 4, 0, 0 }, true)]
    [InlineData(new byte[] { 0xC5, 0xFD, 0x11, 0x07 }, true)]
    [InlineData(new byte[] { 0xC5, 0xFE, 0x7F, 0x07 }, true)]
    [InlineData(new byte[] { 0xC5, 0xFC, 0x10, 0x07 }, false)]
    [InlineData(new byte[] { 0xC5, 0xF8, 0x11, 0x07 }, false)]
    [InlineData(new byte[] { 0xC5, 0xFC, 0x29, 0x07 }, false)]
    [InlineData(new byte[] { 0xC5, 0xFC, 0x11, 0xC1 }, false)]
    public void SelectsOnlyUnaligned256BitMemoryStores(byte[] instructionBytes, bool requiresStoreSplit)
    {
        Assert.Equal(requiresStoreSplit, RosettaVectorStorePatch.RequiresStoreSplit(DecodeInstruction(instructionBytes)));
    }

    [Theory]
    [InlineData(new byte[] { 0xC4, 0x41, 0x7C, 0x11, 0x66, 0xF0 })]
    [InlineData(new byte[] { 0xC5, 0xFC, 0x11, 0x5C, 0xF3, 0x60 })]
    [InlineData(new byte[] { 0xC5, 0xFC, 0x11, 0x05, 0x00, 0x10, 0, 0 })]
    [InlineData(new byte[] { 0x67, 0xC5, 0xFC, 0x11, 0x07 })]
    public void SplitStoresPreserveAddressAndSourceAfterRelocation(byte[] instructionBytes)
    {
        var originalInstruction = DecodeInstruction(instructionBytes);
        var instructions = RosettaVectorStorePatch.SplitVectorStores([originalInstruction]);
        var writer = new InstructionByteWriter();
        Assert.True(BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, 0x200000), out var error, out _), error);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(writer.EncodedBytes.ToArray()));
        decoder.IP = 0x200000;
        var lowerStore = decoder.Decode();
        var upperStore = decoder.Decode();

        Assert.Equal(Code.VEX_Vmovups_xmmm128_xmm, lowerStore.Code);
        Assert.Equal(Register.XMM0 + (originalInstruction.Op1Register - Register.YMM0), lowerStore.Op1Register);
        Assert.Equal(Code.VEX_Vextractf128_xmmm128_ymm_imm8, upperStore.Code);
        Assert.Equal(originalInstruction.Op1Register, upperStore.Op1Register);
        Assert.Equal(1, upperStore.Immediate8);
        Assert.Equal(originalInstruction.MemoryBase, lowerStore.MemoryBase);
        Assert.Equal(originalInstruction.MemoryIndex, lowerStore.MemoryIndex);
        Assert.Equal(originalInstruction.MemoryIndexScale, upperStore.MemoryIndexScale);
        Assert.Equal(originalInstruction.MemoryDisplacement64, lowerStore.MemoryDisplacement64);
        Assert.Equal(unchecked(originalInstruction.MemoryDisplacement64 + 16), upperStore.MemoryDisplacement64);
    }

    [Fact]
    public unsafe void RosettaSplitsStoresWithoutRedZoneUseAndPreservesAllBytes()
    {
        if (!RosettaVectorStorePatch.IsRequired)
            return;

        using var memory = new PhysicalVirtualMemory();
        const ulong imageSize = 0x10000;
        var imageBase = memory.AllocateAt(0, imageSize);
        // Load a vector, write it to the test address, and copy the source register.
        // Clear the upper vector registers before the function returns.
        byte[] function =
        [
            0xC5, 0xFE, 0x6F, 0x06,
            0xC5, 0xFC, 0x11, 0x87, 0x90, 4, 0, 0,
            0xC5, 0xFE, 0x7F, 0x02,
            0xC5, 0xF8, 0x77, 0xC3,
        ];
        Assert.True(memory.TryWrite(imageBase, function));
        var exceptionFrameHeader = new byte[32];
        exceptionFrameHeader[0] = 1;
        exceptionFrameHeader[2] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(exceptionFrameHeader.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(exceptionFrameHeader.AsSpan(16), imageBase);
        Assert.True(memory.TryWrite(imageBase + 0x1000, exceptionFrameHeader));
        ProgramHeader[] programHeaders =
        [
            CreateProgramHeader(ProgramHeaderType.Load, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, 0, (ulong)function.Length),
            CreateProgramHeader(ProgramHeaderType.GnuEhFrame, ProgramHeaderFlags.Read, 0x1000, (ulong)exceptionFrameHeader.Length),
        ];

        var result = GuestRedZonePatcher.Patch(memory, memory, programHeaders, imageBase, imageSize);
        Assert.Equal(0, result.RedZoneFunctions);
        Assert.Equal(2, result.VectorStoreCount);
        Assert.Equal(2, result.PatchedSites);
        Assert.Equal(0, result.FailedSites);

        byte* source = stackalloc byte[32];
        byte* copiedSource = stackalloc byte[32];
        byte* destination = stackalloc byte[32];
        for (int byteIndex = 0; byteIndex < 32; byteIndex++) source[byteIndex] = (byte)(byteIndex * 7 + 1);
        ((delegate* unmanaged<byte*, byte*, byte*, void>)imageBase)(destination - 0x490, source, copiedSource);
        Assert.Equal(new ReadOnlySpan<byte>(source, 32).ToArray(), new ReadOnlySpan<byte>(destination, 32).ToArray());
        Assert.Equal(new ReadOnlySpan<byte>(source, 32).ToArray(), new ReadOnlySpan<byte>(copiedSource, 32).ToArray());
    }

    private static ProgramHeader CreateProgramHeader(ProgramHeaderType type, ProgramHeaderFlags flags, ulong virtualAddress, ulong segmentSize)
    {
        Span<byte> bytes = stackalloc byte[56];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)flags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], segmentSize);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], segmentSize);
        return MemoryMarshal.Read<ProgramHeader>(bytes);
    }

    private static Instruction DecodeInstruction(byte[] instructionBytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(instructionBytes));
        decoder.IP = 0x100000;
        return decoder.Decode();
    }

    private sealed class InstructionByteWriter : CodeWriter
    {
        public List<byte> EncodedBytes { get; } = [];
        public override void WriteByte(byte value) => EncodedBytes.Add(value);
    }
}
