// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5VertexSystemRegisterTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VertexCapacityIsInitializedBeforeThePackedCountRead(bool subgroups)
    {
        var instructions = Compile(ShaderStage.Vertex, 8, subgroups);
        var register = Register(instructions, "s2");
        var read = instructions.FindIndex(item => item.Opcode == SpirvOp.Load && item.Operands[2] == register);
        var store = instructions.FindIndex(item => item.Opcode == SpirvOp.Store && item.Operands[0] == register);
        Assert.True(store >= 0 && store < read, "The vertex count must be initialized before its first read.");
        var value = instructions[store].Operands[1];
        var packed = Assert.Single(instructions, item => item.Opcode == SpirvOp.Constant && item.Operands[1] == value).Operands[2];
        Assert.Equal(32u << 12, packed);
        Assert.Equal(0u, (packed >> 22) & 0x1ff);
        // Capacity must admit high lane ordinals even when only three lanes are active.
        foreach (var lane in new uint[] { 0, 17, 31 })
            Assert.True(((packed >> 12) & 0x1ff) > lane);
        Assert.False(((packed >> 12) & 0x1ff) > 32);
    }

    [Theory]
    [InlineData(ShaderStage.Vertex, 0u)]
    [InlineData(ShaderStage.Vertex, 3u)]
    [InlineData(ShaderStage.Compute, 8u)]
    [InlineData(ShaderStage.Pixel, 8u)]
    public void OtherEntryLayoutsDoNotReceiveVertexCapacity(ShaderStage stage, uint userDataBase)
    {
        var instructions = Compile(stage, userDataBase, true);
        var register = Register(instructions, "s2");
        var capacity = instructions.Where(item => item.Opcode == SpirvOp.Constant &&
            item.Operands.Length == 3 && item.Operands[2] == 32u << 12).Select(item => item.Operands[1]).ToHashSet();
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.Store &&
            item.Operands[0] == register && capacity.Contains(item.Operands[1]));
    }

    private static List<Instruction> Compile(ShaderStage stage, uint userDataBase, bool subgroups)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Sop2(0, "SBfeU32", 10, Gen5Operand.Scalar(2), ResourceTestProgram.Operand((9u << 16) | 12)),
            ResourceTestProgram.EndProgram(4));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, stage, userDataBase, 4);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = 32,
            EnableGraphicsSubgroupOperations = subgroups,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var words = new uint[shader.Spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(shader.Spirv, 0, words, 0, shader.Spirv.Length);
        var result = new List<Instruction>();
        for (var offset = 5; offset < words.Length;)
        {
            var count = (int)(words[offset] >> 16);
            Assert.True(count > 0);
            result.Add(new Instruction((SpirvOp)(words[offset] & 0xffff), words[(offset + 1)..(offset + count)]));
            offset += count;
        }
        return result;
    }

    private static uint Register(List<Instruction> instructions, string name) =>
        Assert.Single(instructions, item => item.Opcode == SpirvOp.Name && Name(item.Operands) == name).Operands[0];

    private static string Name(uint[] operands)
    {
        var bytes = new byte[(operands.Length - 1) * sizeof(uint)];
        Buffer.BlockCopy(operands, sizeof(uint), bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private sealed record Instruction(SpirvOp Opcode, uint[] Operands);
}
