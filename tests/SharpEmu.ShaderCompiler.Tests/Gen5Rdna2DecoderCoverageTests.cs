// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Rdna2DecoderCoverageTests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint SEndpgm = 0xBF810000u;

    public static TheoryData<uint, string, uint> ScalarOpcodes => new()
    {
        { EncodeSop1(0x06), "SCmovB64", 1 },
        { EncodeSop1(0x16), "SFlbitI32B64", 1 },
        { EncodeSop1(0x2D), "SQuadmaskB64", 1 },
        { EncodeSop2(0x2C), "SAbsdiffI32", 1 },
        { 0xBF8E0000u, "SSleep", 1 },
        { EncodeSopk(0x1B), "SSubvectorLoopBegin", 1 },
        { EncodeSopk(0x1C), "SSubvectorLoopEnd", 1 },
    };

    [Theory]
    [MemberData(nameof(ScalarOpcodes))]
    public void ScalarAndControlOpcodesDecode(uint word, string expected, uint expectedDwords) =>
        AssertPreflight(word, expected, expectedDwords);

    public static TheoryData<uint, string> Vop1Opcodes => new()
    {
        { 0x04, "VCvtF64I32" },
        { 0x0F, "VCvtF32F64" },
        { 0x2F, "VRcpF64" },
        { 0x3B, "VFfbhI32" },
        { 0x3F, "VFrexpExpI32F32" },
        { 0x40, "VFrexpMantF32" },
        { 0x50, "VCvtF16U16" },
        { 0x51, "VCvtF16I16" },
        { 0x53, "VCvtI16F16" },
        { 0x54, "VRcpF16" },
        { 0x55, "VSqrtF16" },
        { 0x57, "VLogF16" },
        { 0x58, "VExpF16" },
        { 0x5B, "VFloorF16" },
        { 0x5C, "VCeilF16" },
        { 0x5D, "VTruncF16" },
        { 0x5E, "VRndneF16" },
        { 0x5F, "VFractF16" },
        { 0x60, "VSinF16" },
        { 0x61, "VCosF16" },
    };

    [Theory]
    [MemberData(nameof(Vop1Opcodes))]
    public void Rdna2Vop1OpcodesDecode(uint opcode, string expected) =>
        AssertPreflight(EncodeVop1(opcode), expected, 1);

    [Theory]
    [InlineData(0x36u, "VFmacF16", 1u)]
    [InlineData(0x37u, "VFmaMkF16", 2u)]
    [InlineData(0x38u, "VFmaAkF16", 2u)]
    public void F16FmaVop2OpcodesHaveArchitecturalWidths(
        uint opcode,
        string expected,
        uint expectedDwords) =>
        AssertPreflight(EncodeVop2(opcode), expected, expectedDwords);

    public static TheoryData<uint, string> PackedIntegerOpcodes => new()
    {
        { 0x00, "VPkMadI16" },
        { 0x01, "VPkMulLoU16" },
        { 0x02, "VPkAddI16" },
        { 0x03, "VPkSubI16" },
        { 0x04, "VPkLshlrevB16" },
        { 0x05, "VPkLshrrevB16" },
        { 0x06, "VPkAshrrevI16" },
        { 0x07, "VPkMaxI16" },
        { 0x08, "VPkMinI16" },
        { 0x09, "VPkMadU16" },
        { 0x0A, "VPkAddU16" },
        { 0x0B, "VPkSubU16" },
        { 0x0C, "VPkMaxU16" },
        { 0x0D, "VPkMinU16" },
    };

    [Theory]
    [MemberData(nameof(PackedIntegerOpcodes))]
    public void PackedIntegerOpcodesDecode(uint opcode, string expected)
    {
        var instruction = Assert.Single(Decode(
            0xCC000000u | (opcode << 16) | 4u,
            0x00040200u,
            SEndpgm).Instructions, item => item.Opcode != "SEndpgm");

        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal(2, instruction.Words.Count);
    }

    public static TheoryData<uint, string> DataShareOpcodes => new()
    {
        { 0x1E, "DsWriteB8" },
        { 0x1F, "DsWriteB16" },
        { 0x3A, "DsReadU8" },
        { 0x3B, "DsReadI16" },
        { 0x3C, "DsReadU16" },
        { 0x78, "DsRead2St64B64" },
        { 0xA0, "DsWriteB8D16Hi" },
        { 0xA1, "DsWriteB16D16Hi" },
        { 0xA6, "DsReadU16D16" },
        { 0xA7, "DsReadU16D16Hi" },
    };

    [Theory]
    [MemberData(nameof(DataShareOpcodes))]
    public void DataShareSubdwordAndSt64OpcodesDecode(uint opcode, string expected) =>
        AssertPreflight(0xD8000000u | (opcode << 18), expected, 2);

    [Fact]
    public void NewlyDecodedOperandsPreserveLiteralAndRegisterShapes()
    {
        var multiplyLiteral = Assert.Single(Decode(
            EncodeVop2(0x37, destination: 71, source0: 261, source1: 8),
            0x3C003C00u,
            SEndpgm).Instructions, item => item.Opcode == "VFmaMkF16");
        Assert.Equal(
            [Gen5Operand.Vector(5), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x3C003C00), Gen5Operand.Vector(8)],
            multiplyLiteral.Sources);

        var addLiteral = Assert.Single(Decode(
            EncodeVop2(0x38, destination: 72, source0: 265, source1: 10),
            0x40004000u,
            SEndpgm).Instructions, item => item.Opcode == "VFmaAkF16");
        Assert.Equal(
            [Gen5Operand.Vector(9), Gen5Operand.Vector(10), new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x40004000)],
            addLiteral.Sources);

        var write = DecodeDataShare(0xA0, address: 3, data0: 4, destination: 9);
        Assert.Equal([Gen5Operand.Vector(3), Gen5Operand.Vector(4)], write.Sources);
        Assert.Empty(write.Destinations);

        var read = DecodeDataShare(0xA7, address: 3, data0: 4, destination: 9);
        Assert.Equal([Gen5Operand.Vector(3)], read.Sources);
        Assert.Equal([Gen5Operand.Vector(9)], read.Destinations);

        var pairedRead = DecodeDataShare(0x78, address: 3, data0: 4, destination: 9);
        Assert.Equal(
            [Gen5Operand.Vector(9), Gen5Operand.Vector(10), Gen5Operand.Vector(11), Gen5Operand.Vector(12)],
            pairedRead.Destinations);
    }

    [Fact]
    public void MemRealtimeIsAClockReadNotAResourceRead()
    {
        var word = (0x3Du << 26) | (0x25u << 18) | (20u << 6);
        var instruction = Assert.Single(Decode(word, 0u, SEndpgm).Instructions,
            item => item.Opcode == "SMemrealtime");

        Assert.Empty(instruction.Sources);
        Assert.Equal([Gen5Operand.Scalar(20), Gen5Operand.Scalar(21)], instruction.Destinations);
        Assert.Null(instruction.Control);
    }

    [Fact]
    public void ExplicitLodGatherDecodes()
    {
        AssertPreflight((0x3Cu << 26) | (0x44u << 18), "ImageGather4L", 2);
    }

    private static Gen5ShaderInstruction DecodeDataShare(
        uint opcode,
        uint address,
        uint data0,
        uint destination)
    {
        var word0 = 0xD8000000u | (opcode << 18);
        var word1 = address | (data0 << 8) | (destination << 24);
        return Assert.Single(Decode(word0, word1, SEndpgm).Instructions,
            item => item.Opcode != "SEndpgm");
    }

    private static void AssertPreflight(uint word, string expected, uint expectedDwords)
    {
        Assert.True(
            Gen5ShaderTranslator.TryDecodeInstructionForPreflight(
                new CpuContext(new InstructionMemory([]), Generation.Gen5),
                0,
                word,
                out var name,
                out var sizeDwords,
                out var error),
            error);
        Assert.Equal(expected, name);
        Assert.Equal(expectedDwords, sizeDwords);
    }

    private static Gen5ShaderProgram Decode(params uint[] words)
    {
        var memory = new InstructionMemory(words);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                new CpuContext(memory, Generation.Gen5),
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static uint EncodeSop1(uint opcode, uint destination = 4, uint source = 1) =>
        0x80000000u | (0x7Du << 23) | (destination << 16) | (opcode << 8) | source;

    private static uint EncodeSop2(uint opcode, uint destination = 4, uint source0 = 1, uint source1 = 2) =>
        0x80000000u | (opcode << 23) | (destination << 16) | (source1 << 8) | source0;

    private static uint EncodeSopk(uint opcode, uint destination = 4, uint immediate = 0) =>
        0x80000000u | ((0x60u + opcode) << 23) | (destination << 16) | immediate;

    private static uint EncodeVop1(uint opcode, uint destination = 4, uint source = 257) =>
        (0x3Fu << 25) | (destination << 17) | (opcode << 9) | source;

    private static uint EncodeVop2(
        uint opcode,
        uint destination = 4,
        uint source0 = 257,
        uint source1 = 2) =>
        (opcode << 25) | (destination << 17) | (source1 << 9) | source0;

    private sealed class InstructionMemory : ICpuMemory
    {
        private readonly byte[] _bytes;

        public InstructionMemory(IReadOnlyList<uint> words)
        {
            _bytes = new byte[Math.Max(sizeof(uint), words.Count * sizeof(uint))];
            for (var index = 0; index < words.Count; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    _bytes.AsSpan(index * sizeof(uint)),
                    words[index]);
            }
        }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < ShaderAddress ||
                address - ShaderAddress > (ulong)(_bytes.Length - destination.Length))
            {
                return false;
            }

            _bytes.AsSpan((int)(address - ShaderAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
