// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SaturatingConversionTests
{
    [Theory]
    [InlineData("VCvtI32F32")]
    [InlineData("VCvtRpiI32F32")]
    [InlineData("VCvtFlrI32F32")]
    public void SignedConversionsUseSafeOperandAndSaturatedResults(string opcode)
    {
        var instructions = Compile(opcode);
        var conversion = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ConvertFToS);

        Assert.Equal(
            SpirvOp.Select,
            Definition(instructions, conversion.Operands[2]).Opcode);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.IsNan);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.FOrdLessThanEqual);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.FOrdGreaterThanEqual);
        AssertExtOperation(instructions, 3);
        AssertConstant(instructions, 0xCF00_0000); // -2^31
        AssertConstant(instructions, 0x4F00_0000); // 2^31
        AssertConstant(instructions, 0x4EFF_FFFF); // largest safe f32 below 2^31
        AssertConstant(instructions, 0x8000_0000);
        AssertConstant(instructions, 0x7FFF_FFFF);
    }

    [Fact]
    public void RoundPositiveInfinityTieBreakerUsesFloorAfterHalfOffset()
    {
        var program = Program(
            Vop1(0, "VCvtRpiI32F32", 1, Gen5Operand.Vector(0)),
            EndProgram(4));
        var instructions = Compile(program);

        AssertExtOperation(instructions, 8);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.FAdd);
        AssertConstant(instructions, 0x3F00_0000); // 0.5f

        var request = Request(program);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var metal, out var error),
            error);
        Assert.Contains("floor(", metal.Source, StringComparison.Ordinal);
        Assert.Contains(" + 0.5f", metal.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("ceil(", metal.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsignedConversionUsesSafeOperandAndSaturatedBoundary()
    {
        var instructions = Compile("VCvtU32F32");
        var conversion = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ConvertFToU);

        Assert.Equal(
            SpirvOp.Select,
            Definition(instructions, conversion.Operands[2]).Opcode);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.IsNan);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.LogicalOr);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.FOrdLessThanEqual);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.FOrdGreaterThanEqual);
        AssertExtOperation(instructions, 3);
        AssertConstant(instructions, 0x4F80_0000); // 2^32
        AssertConstant(instructions, 0x4F7F_FFFF); // largest safe f32 below 2^32
        AssertConstant(instructions, uint.MaxValue);
    }

    [Fact]
    public void PackedU8ConversionSaturatesBeforeInsertion()
    {
        var program = Program(
            Vop3(
                0,
                "VCvtPkU8F32",
                3,
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2)),
            EndProgram(8));
        var instructions = Compile(program);
        var conversion = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ConvertFToU);

        Assert.Equal(
            SpirvOp.Select,
            Definition(instructions, conversion.Operands[2]).Opcode);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.BitFieldInsert);
        AssertConstant(instructions, 0x437F_0000); // 255.0f
        AssertConstant(instructions, 255);
    }

    private static List<Instruction> Compile(string opcode) =>
        Compile(Program(
            Vop1(0, opcode, 1, Gen5Operand.Vector(0)),
            EndProgram(4)));

    private static List<Instruction> Compile(Gen5ShaderProgram program)
    {
        var request = Request(program);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        return ReadInstructions(shader.Spirv);
    }

    private static void AssertExtOperation(
        IReadOnlyList<Instruction> instructions,
        uint operation) =>
        Assert.Contains(
            instructions,
            item => item.Opcode == SpirvOp.ExtInst &&
                item.Operands.Length >= 4 &&
                item.Operands[3] == operation);

    private static void AssertConstant(
        IReadOnlyList<Instruction> instructions,
        uint value) =>
        Assert.Contains(
            instructions,
            item => item.Opcode == SpirvOp.Constant &&
                item.Operands.Length == 3 &&
                item.Operands[2] == value);

    private static Instruction Definition(
        IReadOnlyList<Instruction> instructions,
        uint result) =>
        Assert.Single(
            instructions,
            item => item.Operands.Length >= 2 &&
                item.Operands[1] == result &&
                item.Opcode is not SpirvOp.Store and
                    not SpirvOp.BranchConditional and
                    not SpirvOp.SelectionMerge);

    private static List<Instruction> ReadInstructions(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<Instruction>();
        for (var index = 5; index < words.Length;)
        {
            var count = checked((int)(words[index] >> 16));
            result.Add(new Instruction(
                (SpirvOp)(words[index] & 0xFFFF),
                words[(index + 1)..(index + count)]));
            index += count;
        }

        return result;
    }

    private sealed record Instruction(SpirvOp Opcode, uint[] Operands);
}
