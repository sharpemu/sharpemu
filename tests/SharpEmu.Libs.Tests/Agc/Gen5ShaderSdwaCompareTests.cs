// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5ShaderSdwaCompareTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Theory]
    [InlineData(0x7D84_18F9u, 0x0686_8081u, "VCmpEqU32", 0u)]
    [InlineData(0x7C03_E4F9u, 0x8626_8202u, "VCmpLtF32", 2u)]
    public void RealSdwaCompare_DecodesExplicitScalarDestination(
        uint word,
        uint extension,
        string opcode,
        uint expectedDestination)
    {
        var instruction = DecodeSingle(word, extension);

        Assert.Equal(Gen5ShaderEncoding.Vopc, instruction.Encoding);
        Assert.Equal(opcode, instruction.Opcode);
        var control = Assert.IsType<Gen5SdwaControl>(instruction.Control);
        Assert.Equal(expectedDestination, control.ScalarDestination);
        Assert.Equal(0u, control.DestinationSelect);
        Assert.Equal(0u, control.OutputModifier);
        Assert.False(control.Clamp);
    }

    [Fact]
    public void SdwaCompareWithoutSdBit_DecodesImplicitVccDestination()
    {
        var instruction = DecodeSingle(0x7D84_18F9, 0x0686_0081);

        Assert.Null(
            Assert.IsType<Gen5SdwaControl>(instruction.Control).ScalarDestination);
    }

    [Theory]
    [InlineData(0x7D84_18F9u, 0x0686_8081u, 0u)]
    [InlineData(0x7C03_E4F9u, 0x8626_8202u, 2u)]
    public void SdwaCompareTranslation_WritesEncodedScalarMaskPair(
        uint word,
        uint extension,
        uint expectedDestination)
    {
        var accesses = AnalyzeScalarAccesses(
            ParseModule(CompileCompute(word, extension)));

        Assert.True(accesses.StoredRegisters.ContainsKey(expectedDestination));
        Assert.True(accesses.StoredRegisters.ContainsKey(expectedDestination + 1));
    }

    [Fact]
    public void Vop3CompareTranslation_WritesEncodedScalarMaskPair()
    {
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(
                ShaderAddress,
                [
                    new Gen5ShaderInstruction(
                        0,
                        Gen5ShaderEncoding.Vop3,
                        "VCmpEqU32",
                        [0],
                        [Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
                        [],
                        new Gen5Vop3Control(0, 0, 0, false, 0, 4)),
                    new Gen5ShaderInstruction(
                        4,
                        Gen5ShaderEncoding.Sopp,
                        "SEndpgm",
                        [0],
                        [],
                        [],
                        null),
                ]),
            new uint[32],
            null,
            null);
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out var shader,
                out error),
            error);

        var accesses = AnalyzeScalarAccesses(ParseModule(shader.Spirv));
        Assert.True(accesses.StoredRegisters.ContainsKey(4));
        Assert.True(accesses.StoredRegisters.ContainsKey(5));
    }

    private static Gen5ShaderInstruction DecodeSingle(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint> { [0x213] = 0 },
                0x240,
                out var state,
                out var error),
            error);
        return state.Program.Instructions[0];
    }

    private static byte[] CompileCompute(params uint[] words)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, words);
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                new Dictionary<uint, uint> { [0x213] = 0 },
                0x240,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                32,
                1,
                1,
                out var shader,
                out error),
            error);
        return shader.Spirv;
    }

    private static IReadOnlyList<ParsedInstruction> ParseModule(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(header >> 16), 1);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint)), sizeof(uint)));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static ScalarAccesses AnalyzeScalarAccesses(
        IReadOnlyList<ParsedInstruction> module)
    {
        var scalarRegistersId = module
            .Where(instruction => instruction.Opcode == SpirvOp.Name)
            .Single(instruction => DecodeString(instruction.Operands, 1) == "sgpr")
            .Operands[0];
        var constants = module
            .Where(instruction =>
                instruction.Opcode == SpirvOp.Constant &&
                instruction.Operands.Length == 3)
            .ToDictionary(
                instruction => instruction.Operands[1],
                instruction => instruction.Operands[2]);
        var pointers = new Dictionary<uint, uint>();
        foreach (var instruction in module.Where(instruction =>
                     instruction.Opcode == SpirvOp.AccessChain &&
                     instruction.Operands.Length == 4 &&
                     instruction.Operands[2] == scalarRegistersId))
        {
            if (constants.TryGetValue(instruction.Operands[3], out var register))
            {
                pointers[instruction.Operands[1]] = register;
            }
        }

        var stored = new Dictionary<uint, int>();
        foreach (var instruction in module.Where(instruction =>
                     instruction.Opcode == SpirvOp.Store &&
                     instruction.Operands.Length >= 2))
        {
            if (pointers.TryGetValue(instruction.Operands[0], out var register))
            {
                stored[register] = stored.GetValueOrDefault(register) + 1;
            }
        }

        return new ScalarAccesses(stored);
    }

    private static string DecodeString(uint[] operands, int startIndex)
    {
        var bytes = new List<byte>();
        foreach (var word in operands.Skip(startIndex))
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                var value = (byte)(word >> shift);
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);

    private sealed record ScalarAccesses(
        IReadOnlyDictionary<uint, int> StoredRegisters);
}
