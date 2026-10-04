// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SpecialFloatSemanticsTests
{
    [Theory]
    [InlineData("VRcpF32", 0u)]
    [InlineData("VLogF32", 30u)]
    [InlineData("VExpF32", 29u)]
    [InlineData("VRsqF32", 32u)]
    [InlineData("VSqrtF32", 31u)]
    public void SpecialF32OperationsFlushDenormalInputToSignedZero(
        string opcode,
        uint glslOperation)
    {
        var instructions = CompileUnary(opcode);
        var operation = glslOperation == 0
            ? Assert.Single(instructions, item => item.Opcode == SpirvOp.FDiv)
            : Assert.Single(
                instructions,
                item => item.Opcode == SpirvOp.ExtInst &&
                    item.Operands.Length >= 5 &&
                    item.Operands[3] == glslOperation);
        var source = operation.Operands[glslOperation == 0 ? 3 : 4];

        AssertFlushResult(instructions, source);
        Assert.Contains(instructions, item =>
            item.Opcode == SpirvOp.Constant &&
            item.Operands.Length == 3 &&
            item.Operands[2] == 0x0080_0000);
    }

    [Fact]
    public void ReciprocalIflagDoesNotAddDenormalFlush()
    {
        var instructions = CompileUnary("VRcpIflagF32");
        var division = Assert.Single(instructions, item => item.Opcode == SpirvOp.FDiv);

        Assert.False(IsFlushResult(instructions, division.Operands[3]));
        Assert.DoesNotContain(instructions, item =>
            item.Opcode == SpirvOp.Constant &&
            item.Operands.Length == 3 &&
            item.Operands[2] == 0x0080_0000);
    }

    [Theory]
    [InlineData("VSinF32", 13u, true)]
    [InlineData("VCosF32", 14u, false)]
    [InlineData("VSinF16", 13u, true)]
    [InlineData("VCosF16", 14u, false)]
    public void TrigonometricOperationsReduceGuestCycles(
        string opcode,
        uint glslOperation,
        bool preserveSignedZero)
    {
        var instructions = CompileUnary(opcode);
        var trig = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ExtInst &&
                item.Operands.Length >= 5 &&
                item.Operands[3] == glslOperation);
        var multiply = Definition(instructions, trig.Operands[4]);
        var cycle = Definition(instructions, multiply.Operands[2]);

        Assert.Equal(SpirvOp.FMul, multiply.Opcode);
        Assert.Equal(SpirvOp.Select, cycle.Opcode);
        Assert.Contains(instructions, item =>
            item.Opcode == SpirvOp.ExtInst &&
            item.Operands.Length >= 4 &&
            item.Operands[3] == 10u);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.UGreaterThanEqual);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.ULessThan);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.LogicalAnd);
        Assert.Contains(instructions, item => item.Opcode == SpirvOp.Select);
        AssertConstant(instructions, 0x4B00_0000);
        AssertConstant(instructions, 0x7F80_0000);
        AssertConstant(instructions, 0x40C9_0FDB);
        Assert.Equal(
            preserveSignedZero ? SpirvOp.IEqual : SpirvOp.LogicalAnd,
            Definition(instructions, cycle.Operands[2]).Opcode);
    }

    [Theory]
    [InlineData("VSinF16", 13u)]
    [InlineData("VCosF16", 14u)]
    public void Float16TrigonometricEdgeChecksUseOriginalSource(
        string opcode,
        uint glslOperation)
    {
        var instructions = CompileUnary(opcode);
        var trig = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ExtInst &&
                item.Operands.Length >= 5 &&
                item.Operands[3] == glslOperation);
        var multiply = Definition(instructions, trig.Operands[4]);
        var cycle = Definition(instructions, multiply.Operands[2]);
        var reduced = opcode == "VSinF16"
            ? Definition(instructions, cycle.Operands[4])
            : cycle;
        var reductionFraction = Definition(instructions, reduced.Operands[4]);
        var fractions = instructions
            .Where(item => item.Opcode == SpirvOp.ExtInst &&
                item.Operands.Length >= 5 &&
                item.Operands[3] == 10u)
            .ToArray();

        Assert.Equal(2, fractions.Length);
        Assert.Contains(reductionFraction, fractions);
        var cardinalFraction = Assert.Single(
            fractions,
            item => item != reductionFraction);
        var originalSource = reductionFraction.Operands[4];
        Assert.Equal(originalSource, cardinalFraction.Operands[4]);
        Assert.Equal(2, instructions.Count(item =>
            item.Opcode == SpirvOp.FOrdEqual &&
            item.Operands.Length >= 4 &&
            item.Operands[2] == cardinalFraction.Operands[1]));

        var infinityConstant = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.Constant &&
                item.Operands.Length == 3 &&
                item.Operands[2] == 0x7F80_0000);
        var infinity = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.IEqual &&
                item.Operands.Length >= 4 &&
                (item.Operands[2] == infinityConstant.Operands[1] ||
                 item.Operands[3] == infinityConstant.Operands[1]));
        var magnitudeId = infinity.Operands[2] == infinityConstant.Operands[1]
            ? infinity.Operands[3]
            : infinity.Operands[2];
        var magnitude = Definition(instructions, magnitudeId);
        var sourceBits = Definition(instructions, magnitude.Operands[2]);

        Assert.Equal(SpirvOp.BitwiseAnd, magnitude.Opcode);
        Assert.Equal(SpirvOp.Bitcast, sourceBits.Opcode);
        Assert.Equal(originalSource, sourceBits.Operands[2]);
    }

    private static List<Instruction> CompileUnary(string opcode)
    {
        var program = Program(
            Vop1(0, opcode, 1, Gen5Operand.Vector(0)),
            EndProgram(4));
        var request = Request(program);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);
        return ReadInstructions(shader.Spirv);
    }

    private static void AssertFlushResult(
        IReadOnlyList<Instruction> instructions,
        uint result) =>
        Assert.True(IsFlushResult(instructions, result));

    private static bool IsFlushResult(
        IReadOnlyList<Instruction> instructions,
        uint result)
    {
        var finalBitcast = Definition(instructions, result);
        if (finalBitcast.Opcode != SpirvOp.Bitcast || finalBitcast.Operands.Length < 3)
        {
            return false;
        }

        var selection = Definition(instructions, finalBitcast.Operands[2]);
        return selection.Opcode == SpirvOp.Select;
    }

    private static void AssertConstant(
        IReadOnlyList<Instruction> instructions,
        uint value) =>
        Assert.Contains(instructions, item =>
            item.Opcode == SpirvOp.Constant &&
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
