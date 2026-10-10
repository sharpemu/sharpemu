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

    [Theory]
    [InlineData(new byte[] { 0xC4, 0xE3, 0x7D, 0x1D, 0x47, 0x20, 0x03 }, true)]  // vcvtps2ph [rdi+20h], ymm0, 3
    [InlineData(new byte[] { 0xC4, 0xE3, 0x79, 0x1D, 0x07, 0x00 }, true)]        // vcvtps2ph [rdi], xmm0, 0
    [InlineData(new byte[] { 0xC4, 0xE3, 0x7D, 0x1D, 0xC1, 0x03 }, false)]       // vcvtps2ph xmm1, ymm0, 3
    public void SelectsOnlyMemoryHalfConvertStores(byte[] instructionBytes, bool requiresStoreSplit)
    {
        Assert.Equal(requiresStoreSplit, RosettaVectorStorePatch.RequiresStoreSplit(DecodeInstruction(instructionBytes)));
    }

    [Theory]
    [InlineData(new byte[] { 0xC4, 0xE3, 0x7D, 0x1D, 0x47, 0x20, 0x03 }, Register.RDI, 0x20UL, Code.VEX_Vmovdqu_xmmm128_xmm)]
    [InlineData(new byte[] { 0xC4, 0xE3, 0x79, 0x1D, 0x44, 0x24, 0x08, 0x00 }, Register.RSP, 0x08UL + 0xA0UL, Code.VEX_Vmovq_xmmm64_xmm)]
    public void HalfConvertStoreUsesScratchRegisterAndPlainStore(byte[] instructionBytes, Register expectedBase, ulong expectedDisplacement, Code expectedStore)
    {
        var original = DecodeInstruction(instructionBytes);
        var rewritten = Encode(RosettaVectorStorePatch.SplitVectorStores([original]));

        Assert.Equal(6, rewritten.Count);
        Assert.Equal(Code.Lea_r64_m, rewritten[0].Code);
        Assert.Equal(unchecked((ulong)-0xA0L), rewritten[0].MemoryDisplacement64);
        Assert.Equal(Code.VEX_Vmovdqu_ymmm256_ymm, rewritten[1].Code);
        Assert.Equal(Register.YMM15, rewritten[1].Op1Register);
        Assert.Equal(Register.XMM15, rewritten[2].Op0Register);
        Assert.Equal(original.Op1Register, rewritten[2].Op1Register);
        Assert.Equal(original.Immediate8, rewritten[2].Immediate8);
        Assert.Equal(expectedStore, rewritten[3].Code);
        Assert.Equal(expectedBase, rewritten[3].MemoryBase);
        Assert.Equal(expectedDisplacement, rewritten[3].MemoryDisplacement64);
        Assert.Equal(Register.XMM15, rewritten[3].Op1Register);
        Assert.Equal(Code.VEX_Vmovdqu_ymm_ymmm256, rewritten[4].Code);
        Assert.Equal(Code.Lea_r64_m, rewritten[5].Code);
        Assert.Equal(0xA0UL, rewritten[5].MemoryDisplacement64);
    }

    [Fact]
    public void HalfConvertStoreAvoidsScratchingItsSource()
    {
        // vcvtps2ph [rdi], ymm15, 0
        var rewritten = Encode(RosettaVectorStorePatch.SplitVectorStores([DecodeInstruction([0xC4, 0x63, 0x7D, 0x1D, 0x3F, 0x00])]));
        Assert.Equal(Register.YMM14, rewritten[1].Op1Register);
        Assert.Equal(Register.YMM15, rewritten[2].Op1Register);
        Assert.Equal(Register.XMM14, rewritten[3].Op1Register);
    }

    private static List<Instruction> Encode(IList<Instruction> instructions)
    {
        var writer = new InstructionByteWriter();
        Assert.True(BlockEncoder.TryEncode(64, new InstructionBlock(writer, instructions, 0x200000), out var error, out _), error);
        var bytes = writer.EncodedBytes.ToArray();
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
        decoder.IP = 0x200000;
        var decoded = new List<Instruction>();
        while (decoder.IP < 0x200000 + (ulong)bytes.Length)
            decoded.Add(decoder.Decode());
        return decoded;
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
