// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Float16ArithmeticTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void CompactFloat16ArithmeticDecodesAndCompilesWithoutNativeFloat16()
    {
        var program = Decode(
        [
            0x7E00AD01, // v_rsq_f16 v0, v1
            0x64000501, // v_add_f16 v0, v1, v2
            0x66060B04, // v_sub_f16 v3, v4, v5
            0x680C1107, // v_subrev_f16 v6, v7, v8
            0x6A12170A, // v_mul_f16 v9, v10, v11
            0x72181D0D, // v_max_f16 v12, v13, v14
            0x741E2310, // v_min_f16 v15, v16, v17
            SEndpgm,
        ]);

        Assert.Equal(
            ["VRsqF16", "VAddF16", "VSubF16", "VSubrevF16", "VMulF16", "VMaxF16", "VMinF16", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var request = ResourceTestProgram.Request(program, userDataCount: 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var opcodes = ReadOpcodes(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.FAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.FSub, opcodes);
        Assert.Contains((ushort)SpirvOp.FMul, opcodes);
        Assert.True(opcodes.Count(opcode => opcode == (ushort)SpirvOp.ExtInst) >= 3);
        Assert.DoesNotContain((ushort)SpirvCapability.Float16, ReadCapabilities(shader.Spirv));
    }

    [Theory]
    [InlineData(0x54u, "VRcpF16")]
    [InlineData(0x55u, "VSqrtF16")]
    [InlineData(0x57u, "VLogF16")]
    [InlineData(0x58u, "VExpF16")]
    [InlineData(0x5Bu, "VFloorF16")]
    [InlineData(0x5Cu, "VCeilF16")]
    [InlineData(0x5Du, "VTruncF16")]
    [InlineData(0x5Eu, "VRndneF16")]
    [InlineData(0x5Fu, "VFractF16")]
    [InlineData(0x60u, "VSinF16")]
    [InlineData(0x61u, "VCosF16")]
    public void Float16UnaryDecodesAndCompilesInBothEncodings(uint opcode, string name)
    {
        // VOP1 v0, v1 (Silent Hill f: v_log_f16), then the VOP3 form v2, v3.
        var program = Decode(
        [
            0x7E000000u | (opcode << 9) | 257u,
            (0x35u << 26) | ((0x180u + opcode) << 16) | 2u,
            259u,
            SEndpgm,
        ]);

        Assert.Equal([name, name, "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.DoesNotContain((ushort)SpirvCapability.Float16, ReadCapabilities(shader.Spirv));
    }

    [Theory]
    [InlineData(0x351u, "VMin3F16")]
    [InlineData(0x354u, "VMax3F16")]
    [InlineData(0x357u, "VMed3F16")]
    public void Vop3Float16ThreeOperandMinMaxDecodesAndCompiles(uint opcode, string name)
    {
        // Silent Hill's pixel shaders use v_min3_f16.
        var program = Decode(
        [
            (0x35u << 26) | (opcode << 16) | 122u,
            261u | (262u << 9) | (263u << 18),
            SEndpgm,
        ]);

        Assert.Equal(name, program.Instructions[0].Opcode);
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.DoesNotContain((ushort)SpirvCapability.Float16, ReadCapabilities(shader.Spirv));
    }

    [Fact]
    public void Vop3Float16FmaDecodesAndCompiles()
    {
        var program = Decode(
        [
            (0x35u << 26) | (0x34Bu << 16) | 122u,
            261u | (262u << 9) | (263u << 18),
            SEndpgm,
        ]);

        Assert.Equal("VFmaF16", program.Instructions[0].Opcode);
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        Assert.Contains((ushort)SpirvOp.ExtInst, ReadOpcodes(shader.Spirv));
        Assert.DoesNotContain((ushort)SpirvCapability.Float16, ReadCapabilities(shader.Spirv));
    }

    [Fact]
    public void PackedFloat16MultiplyAcceptsLiteralOperand()
    {
        var program = Decode(
        [
            (0x33u << 26) | (0x10u << 16) | 1u,
            0xFFu | (258u << 9),
            0x00002C00u,
            SEndpgm,
        ]);

        Assert.Equal("VPkMulF16", program.Instructions[0].Opcode);
        Assert.Equal(Gen5OperandKind.LiteralConstant, program.Instructions[0].Sources[0].Kind);
        Assert.Equal(0x00002C00u, program.Instructions[0].Sources[0].Value);
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error),
            error);
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static IReadOnlyList<ushort> ReadOpcodes(byte[] spirv) =>
        ReadInstructions(spirv)
            .Select(instruction => instruction.Opcode)
            .ToArray();

    private static IReadOnlyList<ushort> ReadCapabilities(byte[] spirv) =>
        ReadInstructions(spirv)
            .Where(instruction => instruction.Opcode == (ushort)SpirvOp.Capability)
            .Select(instruction => (ushort)instruction.FirstOperand)
            .ToArray();

    private static IReadOnlyList<(ushort Opcode, uint FirstOperand)> ReadInstructions(
        byte[] spirv)
    {
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));
        var instructions = new List<(ushort Opcode, uint FirstOperand)>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var firstOperand = wordCount > 1
                ? BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + sizeof(uint)))
                : 0;
            instructions.Add(((ushort)header, firstOperand));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < baseAddress || address - baseAddress > int.MaxValue)
            {
                return false;
            }

            offset = (int)(address - baseAddress);
            return offset <= _storage.Length - length;
        }
    }
}
