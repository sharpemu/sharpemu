// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ComputePrefixTests
{
    [Theory]
    [InlineData(ShaderStage.Compute)]
    [InlineData(ShaderStage.Pixel)]
    [InlineData(ShaderStage.Vertex)]
    public void StructuredLoopChecksActiveStateAndAllowsInteriorEntry(ShaderStage stage)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Branch(0, "SBranch", 2),
            ResourceTestProgram.MoveScalar(4, 20, 0),
            ResourceTestProgram.Branch(8, "SBranch", 0),
            ResourceTestProgram.MoveScalar(12, 21, 1),
            ResourceTestProgram.Branch(16, "SCbranchScc1", -4),
            ResourceTestProgram.EndProgram(20));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, stage);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
            out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var instructions = new List<(SpirvOp Opcode, uint[] Operands)>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            instructions.Add(((SpirvOp)(words[offset] & 0xffff),
                words.AsSpan(offset + 1, (int)(words[offset] >> 16) - 1).ToArray()));
        var loop = Assert.Single(instructions, instruction => instruction.Opcode == SpirvOp.LoopMerge);
        var loopIndex = instructions.IndexOf(loop);
        var branch = instructions[loopIndex + 1];
        Assert.Equal(SpirvOp.BranchConditional, branch.Opcode);
        Assert.Equal(loop.Operands[0], branch.Operands[2]);
        var entry = instructions[loopIndex - 1];
        var active = instructions[loopIndex - 2];
        var activeLoad = instructions[loopIndex - 3];
        var range = instructions[loopIndex - 4];
        var upper = instructions[loopIndex - 5];
        var lower = instructions[loopIndex - 6];
        Assert.Equal(SpirvOp.LogicalAnd, entry.Opcode);
        Assert.Equal(entry.Operands[1], branch.Operands[0]);
        Assert.Equal(SpirvOp.INotEqual, active.Opcode);
        Assert.Equal(SpirvOp.Load, activeLoad.Opcode);
        Assert.Equal(activeLoad.Operands[1], active.Operands[2]);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands[1] == active.Operands[3] && instruction.Operands[2] == 0);
        Assert.Equal(active.Operands[1], entry.Operands[2]);
        Assert.Equal(SpirvOp.LogicalAnd, range.Opcode);
        Assert.Equal(range.Operands[1], entry.Operands[3]);
        Assert.Equal(SpirvOp.UGreaterThanEqual, lower.Opcode);
        Assert.Equal(SpirvOp.ULessThanEqual, upper.Opcode);
        Assert.Equal(lower.Operands[2], upper.Operands[2]);
        Assert.Equal(lower.Operands[1], range.Operands[2]);
        Assert.Equal(upper.Operands[1], range.Operands[3]);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands[1] == lower.Operands[3] && instruction.Operands[2] == 1);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands[1] == upper.Operands[3] && instruction.Operands[2] == 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedSuffixUsesTheSelectedControlFlowPath(bool repeats)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveScalar(0, 20, 0),
            ResourceTestProgram.Branch(4, "SBranch", 0),
            ResourceTestProgram.MoveScalar(8, 21, 1),
            repeats ? ResourceTestProgram.Branch(12, "SBranch", -2)
                : ResourceTestProgram.EndProgram(12));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Compute);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
            out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var switches = 0;
        var loops = 0;
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            if ((SpirvOp)(words[offset] & 0xffff) == SpirvOp.LoopMerge) loops++;
            if ((SpirvOp)(words[offset] & 0xffff) != SpirvOp.Switch) continue;
            switches++;
            Assert.Equal(5u, words[offset] >> 16);
            Assert.Equal(1u, words[offset + 3]);
        }
        var structured = Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_CF") != "0" ||
            Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_FORWARD_BLOCKS") != "0";
        Assert.Equal(repeats && !structured ? 1 : 0, switches);
        Assert.Equal(repeats ? 1 : 0, loops);
    }
}
