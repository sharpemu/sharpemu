// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class ShaderOpcodePatternTests
{
    private static Gen5ShaderInstruction Instruction(string opcode) => new(0, default, opcode, [], [], [], null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamingMatch_PreservesFilteredSequenceRules(bool ignoreMarkers)
    {
        string[] pattern = ["VLshlAddU32", "VMovB32", "SEndpgm"];
        var valid = pattern.Select(Instruction).ToArray();
        var marker = Instruction("SMovB32") with { Destinations = [Gen5Operand.Scalar(124)] };
        var ignored = new[] { Instruction("SWaitcnt"), Instruction("STtraceData"), Instruction("SInstPrefetch"), marker };
        List<Gen5ShaderInstruction[]> samples =
        [
            [], valid, valid[..^1], [.. valid, Instruction("SNop")],
            [Instruction("SNop"), .. valid], [.. valid[..^1], Instruction("SNop")],
            [marker with { Destinations = [Gen5Operand.Scalar(123)] }, .. valid],
            [marker with { Destinations = [Gen5Operand.Scalar(124), Gen5Operand.Scalar(125)] }, .. valid],
        ];
        foreach (var item in ignored)
        {
            samples.Add([item, .. valid]);
            samples.Add([valid[0], item, .. valid[1..], item]);
        }
        var random = new System.Random(174);
        var choices = valid.Concat(ignored).Append(Instruction("Other")).ToArray();
        for (var sample = 0; sample < 1000; sample++)
            samples.Add(Enumerable.Range(0, random.Next(30)).Select(_ => choices[random.Next(choices.Length)]).ToArray());
        foreach (var sample in samples)
        {
            var expected = sample.Where(instruction => instruction.Opcode != "SWaitcnt" &&
                (!ignoreMarkers || instruction.Opcode is not ("STtraceData" or "SInstPrefetch") &&
                !(instruction.Opcode == "SMovB32" && instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: 124 }]))).ToArray();
            var matches = expected.Select(instruction => instruction.Opcode).SequenceEqual(pattern);
            var actual = ShaderOpcodePattern.Match(sample, pattern, ignoreMarkers);
            if (matches) Assert.Equal(expected, actual);
            else Assert.Null(actual);
        }
    }

    [Fact]
    public void ConstantFill_PreservesOperandsAndIgnoredWaits()
    {
        Gen5ShaderInstruction[] instructions =
        [
            Instruction("VLshlAddU32") with { Destinations = [Gen5Operand.Vector(0)], Sources = [Gen5Operand.Scalar(8), Gen5Operand.Source(134), Gen5Operand.Vector(0)] },
            Instruction("SBufferLoadDword") with { Destinations = [Gen5Operand.Scalar(12)], Sources = [Gen5Operand.Scalar(16), Gen5Operand.Source(128)], Control = new Gen5ScalarMemoryControl(1, 0, null) },
            Instruction("SWaitcnt"),
            Instruction("VMovB32") with { Destinations = [Gen5Operand.Vector(1)], Sources = [Gen5Operand.Scalar(12)] },
            Instruction("BufferStoreFormatX") with { Sources = [Gen5Operand.Vector(1), Gen5Operand.Vector(0), Gen5Operand.Source(128)], Control = new Gen5BufferMemoryControl(1, 0, 1, 20, 0, true, false, false, false) },
            Instruction("SEndpgm"),
        ];
        Assert.Equal(new ConstantFill(8, 20, 16), ConstantFillDetector.Detect(new(0, instructions)));
        Assert.Null(ConstantFillDetector.Detect(new(0, [.. instructions, Instruction("STtraceData")])));
        Assert.Null(ConstantFillDetector.Detect(new(0, [.. instructions, Instruction("SNop")])));
        instructions[3] = instructions[3] with { Sources = [Gen5Operand.Scalar(13)] };
        Assert.Null(ConstantFillDetector.Detect(new(0, instructions)));
    }
}
