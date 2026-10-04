// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class RectangleListShaderTests
{
    [Fact]
    public void ControlStageReconstructsFourthCornerAndExportsFourVertices()
    {
        var shaders = RectangleListShaderBuilder.Build(
            [new RectangleListShaderParameter(3, 9, Flat: false)]);
        var words = Words(shaders.Control);

        Assert.True(HasCapability(words, SpirvCapability.Tessellation));
        Assert.True(HasEntryPoint(words, SpirvExecutionModel.TessellationControl));
        Assert.True(HasExecutionMode(words, SpirvExecutionMode.OutputVertices, 4));
        Assert.True(HasOpcode(words, SpirvOp.FOrdEqual));
        Assert.True(HasOpcode(words, SpirvOp.Select));
        Assert.True(HasDecoration(words, SpirvDecoration.Location, 3));
        Assert.True(HasDecoration(words, SpirvDecoration.Location, 9));
    }

    [Fact]
    public void EvaluationStageEmitsOneQuadFromTheFourControlPoints()
    {
        var shaders = RectangleListShaderBuilder.Build(
            [new RectangleListShaderParameter(3, 9, Flat: true)]);
        var words = Words(shaders.Evaluation);

        Assert.True(HasCapability(words, SpirvCapability.Tessellation));
        Assert.True(HasEntryPoint(words, SpirvExecutionModel.TessellationEvaluation));
        Assert.True(HasExecutionMode(words, SpirvExecutionMode.Quads));
        Assert.True(HasExecutionMode(words, SpirvExecutionMode.SpacingEqual));
        Assert.True(HasExecutionMode(words, SpirvExecutionMode.VertexOrderCw));
        Assert.True(HasDecoration(words, SpirvDecoration.Location, 9));
    }

    private static uint[] Words(byte[] bytes)
    {
        Assert.Equal(0, bytes.Length % sizeof(uint));
        var words = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        Assert.Equal(0x07230203u, words[0]);
        return words;
    }

    private static bool HasCapability(uint[] words, SpirvCapability capability) =>
        Instructions(words).Any(instruction =>
            instruction.Opcode == SpirvOp.Capability &&
            instruction.Operands.Length == 1 &&
            instruction.Operands[0] == (uint)capability);

    private static bool HasEntryPoint(uint[] words, SpirvExecutionModel model) =>
        Instructions(words).Any(instruction =>
            instruction.Opcode == SpirvOp.EntryPoint &&
            instruction.Operands.Length >= 2 &&
            instruction.Operands[0] == (uint)model);

    private static bool HasExecutionMode(
        uint[] words,
        SpirvExecutionMode mode,
        uint? operand = null) =>
        Instructions(words).Any(instruction =>
            instruction.Opcode == SpirvOp.ExecutionMode &&
            instruction.Operands.Length >= 2 &&
            instruction.Operands[1] == (uint)mode &&
            (!operand.HasValue ||
             (instruction.Operands.Length >= 3 && instruction.Operands[2] == operand.Value)));

    private static bool HasDecoration(
        uint[] words,
        SpirvDecoration decoration,
        uint operand) =>
        Instructions(words).Any(instruction =>
            instruction.Opcode == SpirvOp.Decorate &&
            instruction.Operands.Length >= 3 &&
            instruction.Operands[1] == (uint)decoration &&
            instruction.Operands[2] == operand);

    private static bool HasOpcode(uint[] words, SpirvOp opcode) =>
        Instructions(words).Any(instruction => instruction.Opcode == opcode);

    private static IEnumerable<(SpirvOp Opcode, uint[] Operands)> Instructions(uint[] words)
    {
        for (var offset = 5; offset < words.Length;)
        {
            var wordCount = (int)(words[offset] >> 16);
            Assert.True(wordCount > 0);
            Assert.True(offset + wordCount <= words.Length);
            var operands = new uint[wordCount - 1];
            Array.Copy(words, offset + 1, operands, 0, operands.Length);
            yield return ((SpirvOp)(words[offset] & 0xFFFFu), operands);
            offset += wordCount;
        }
    }
}
