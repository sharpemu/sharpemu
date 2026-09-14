// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5FlatMemoryTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void SadU32CompilesToUnsignedMinMaxDifferenceAndAdd()
    {
        var program = DecodeProgram(0xD15D0003u, 0x040A0300u, SEndpgm);
        var addition = program.Instructions[0];
        Assert.Equal("VSadU32", addition.Opcode);
        Assert.Equal([Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)], addition.Sources);
        Assert.Equal([Gen5Operand.Vector(3)], addition.Destinations);
        var scalarRegisters = new uint[256];
        var state = new Gen5ShaderState(
            program,
            [],
            null);
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var compiled,
                out var error),
            error);
        var opcodes = ReadSpirvOpcodes(compiled.Spirv);
        Assert.Contains((ushort)SpirvOp.ExtInst, opcodes);
        Assert.Contains((ushort)SpirvOp.ISub, opcodes);
        Assert.Contains((ushort)SpirvOp.IAdd, opcodes);
        var extendedOperations = new List<uint>();
        for (var offset = 5 * sizeof(uint); offset < compiled.Spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(compiled.Spirv.AsSpan(offset));
            if ((ushort)header == (ushort)SpirvOp.ExtInst)
            {
                extendedOperations.Add(BinaryPrimitives.ReadUInt32LittleEndian(compiled.Spirv.AsSpan(offset + 4 * sizeof(uint))));
            }

            offset += checked((int)(header >> 16) * sizeof(uint));
        }

        Assert.Contains(38u, extendedOperations);
        Assert.Contains(41u, extendedOperations);
    }

    public static TheoryData<uint, string> F16CompareOpcodes => new()
    {
        { 0xC8, "VCmpFF16" },
        { 0xC9, "VCmpLtF16" },
        { 0xCA, "VCmpEqF16" },
        { 0xCB, "VCmpLeF16" },
        { 0xCC, "VCmpGtF16" },
        { 0xCD, "VCmpLgF16" },
        { 0xCE, "VCmpGeF16" },
        { 0xCF, "VCmpOF16" },
        { 0xD8, "VCmpxFF16" },
        { 0xD9, "VCmpxLtF16" },
        { 0xDA, "VCmpxEqF16" },
        { 0xDB, "VCmpxLeF16" },
        { 0xDC, "VCmpxGtF16" },
        { 0xDD, "VCmpxLgF16" },
        { 0xDE, "VCmpxGeF16" },
        { 0xDF, "VCmpxOF16" },
        { 0xE8, "VCmpUF16" },
        { 0xE9, "VCmpNgeF16" },
        { 0xEA, "VCmpNlgF16" },
        { 0xEB, "VCmpNgtF16" },
        { 0xEC, "VCmpNleF16" },
        { 0xED, "VCmpNeqF16" },
        { 0xEE, "VCmpNltF16" },
        { 0xEF, "VCmpTruF16" },
        { 0xF8, "VCmpxUF16" },
        { 0xF9, "VCmpxNgeF16" },
        { 0xFA, "VCmpxNlgF16" },
        { 0xFB, "VCmpxNgtF16" },
        { 0xFC, "VCmpxNleF16" },
        { 0xFD, "VCmpxNeqF16" },
        { 0xFE, "VCmpxNltF16" },
        { 0xFF, "VCmpxTruF16" },
    };

    [Theory]
    [MemberData(nameof(F16CompareOpcodes))]
    public void DecodesF16CompareOpcodes(uint opcode, string expected)
    {
        var program = DecodeProgram(
            (0x3Eu << 25) | (opcode << 17) | (1u << 9) | 256u,
            SEndpgm);

        Assert.Equal(expected, program.Instructions[0].Opcode);
        Assert.Equal(
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            program.Instructions[0].Sources);
    }

    [Fact]
    public void F16CompareCompilesToOrderedSpirv()
    {
        var program = DecodeProgram(
            (0x3Eu << 25) | (0xC9u << 17) | (1u << 9) | 256u,
            SEndpgm);
        var scalarRegisters = new uint[256];
        var state = new Gen5ShaderState(program, [], null);
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var compiled,
                out var error),
            error);
        Assert.Contains(
            (ushort)SpirvOp.FOrdLessThan,
            ReadSpirvOpcodes(compiled.Spirv));
    }

    [Fact]
    public void FlatLoadUbyteInfersScalarBaseAndCompiles()
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x4000);
        uint[] words =
        [
            // v_add_co_u32 v1, vcc_lo, s12, v6
            0xD70F6A01,
            0x00020C0C,
            // v_add_co_ci_u32_sdwa v2, vcc_lo, 0, s13, vcc_lo
            0x50041AF9,
            0x86860680,
            // flat_load_ubyte v0, v[1:2]
            0xDC200000,
            0x007D0001,
            SEndpgm,
        ];
        var shader = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint)),
                words[index]);
        }
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "FlatLoadUbyte");
        var control = Assert.IsType<Gen5GlobalMemoryControl>(
            instruction.Control);
        Assert.True(control.UsesFlatAddress);
        Assert.Equal(1u, control.VectorAddress);
        Assert.Equal(0u, control.VectorData);
        Assert.Equal(12u, control.ScalarAddress);
        Assert.Equal(
            [
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Scalar(12),
            ],
            instruction.Sources);

        uint[] userData =
        [
            unchecked((uint)ShaderAddress),
            unchecked((uint)(ShaderAddress >> 32)),
        ];
        var state = new Gen5ShaderState(
            program,
            userData,
            null,
            UserDataScalarRegisterBase: 12);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var evaluationError),
            evaluationError);

        var binding = Assert.Single(evaluation.GlobalMemoryBindings);
        Assert.Equal(12u, binding.ScalarAddress);
        Assert.Contains(instruction.Pc, binding.InstructionPcs);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var compiled,
                out var compileError),
            compileError);
        Assert.Contains(
            (ushort)SpirvOp.ISub,
            ReadSpirvOpcodes(compiled.Spirv));
    }

    [Fact]
    public void GlobalLoadUsesHighByteForVectorDestination()
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x4000);
        uint[] words =
        [
            // global_load_dwordx4 v[8:11], v9, s[16:17]
            0xDC388000,
            0x08100009,
            SEndpgm,
        ];
        var shader = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint)),
                words[index]);
        }
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var decodeError),
            decodeError);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "GlobalLoadDwordx4");
        var control = Assert.IsType<Gen5GlobalMemoryControl>(
            instruction.Control);
        Assert.Equal(9u, control.VectorAddress);
        Assert.Equal(8u, control.VectorData);
        Assert.Equal(16u, control.ScalarAddress);
        Assert.Equal(
            [
                Gen5Operand.Vector(8),
                Gen5Operand.Vector(9),
                Gen5Operand.Vector(10),
                Gen5Operand.Vector(11),
            ],
            instruction.Destinations);
    }

    private static IReadOnlyList<ushort> ReadSpirvOpcodes(byte[] spirv)
    {
        Assert.Equal(0, spirv.Length % sizeof(uint));
        Assert.True(spirv.Length >= 5 * sizeof(uint));
        Assert.Equal(
            0x07230203u,
            BinaryPrimitives.ReadUInt32LittleEndian(spirv));

        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction =
                BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(
                wordCount,
                1,
                (spirv.Length - offset) / sizeof(uint));
            opcodes.Add((ushort)instruction);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
    }

    private static Gen5ShaderProgram DecodeProgram(params uint[] words)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x4000);
        var shader = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
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

        public bool TryWrite(
            ulong virtualAddress,
            ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(
            ulong virtualAddress,
            int length,
            out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
