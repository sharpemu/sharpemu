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
    [InlineData(false)]
    [InlineData(true)]
    public void VertexSystemInputsFollowTheFrontendRegisterLayout(bool customLayout)
    {
        var program = ResourceTestProgram.Program(ResourceTestProgram.EndProgram(0));
        if (customLayout)
            program = program with { VertexIndexRegister = 0, InstanceIndexRegisters = [1, 2, 3] };
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Vertex);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout),
            out var shader, out var error), error);
        var instructions = Decode(shader.Spirv);
        void AssertInput(uint builtIn, uint register)
        {
            var input = Assert.Single(instructions, item => item.Opcode == SpirvOp.Decorate &&
                item.Operands.Length == 3 && item.Operands[1] == (uint)SpirvDecoration.BuiltIn && item.Operands[2] == builtIn).Operands[0];
            var loads = instructions.Where(item => item.Opcode == SpirvOp.Load && item.Operands[2] == input)
                .Select(item => item.Operands[1]).ToHashSet();
            var target = Register(instructions, $"v{register}");
            Assert.Contains(instructions, item => item.Opcode == SpirvOp.Store && item.Operands[0] == target && loads.Contains(item.Operands[1]));
        }
        AssertInput((uint)SpirvBuiltIn.VertexIndex, program.VertexIndexRegister);
        foreach (var register in program.InstanceIndexRegisters)
            AssertInput((uint)SpirvBuiltIn.InstanceIndex, register);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VertexCapacityIsInitializedBeforeThePackedCountRead(bool subgroups)
    {
        var instructions = Compile(ShaderStage.Vertex, 8, subgroups);
        var register = Register(instructions, "s2");
        var store = instructions.FindIndex(item => item.Opcode == SpirvOp.Store && item.Operands[0] == register);
        Assert.True(store >= 0);
        var value = instructions[store].Operands[1];
        var read = instructions.FindIndex(item => item.Opcode == SpirvOp.BitFieldUExtract && item.Operands[2] == value);
        Assert.True(store >= 0 && store < read, "The vertex count must be initialized before its first read.");
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

    [Theory]
    [InlineData(32u, 32u)]
    [InlineData(64u, 32u)]
    [InlineData(64u, 64u)]
    public void MeshThreadGroupCountsAreInitializedBeforeThePackedCountRead(uint waveSize, uint subgroupSize)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Sop2(0, "SBfeU32", 10, Gen5Operand.Scalar(2), ResourceTestProgram.Operand((9u << 16) | 12)),
            ResourceTestProgram.EndProgram(8));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Mesh);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            WaveSize = waveSize,
            LocalSizeX = 64,
            Mesh = new MeshShaderConfiguration(64, 16, 0, 8, 24, 0, false, subgroupSize),
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var instructions = Decode(shader.Spirv);
        var register = Register(instructions, "s2");
        var store = instructions.FindLastIndex(item => item.Opcode == SpirvOp.Store && item.Operands[0] == register);
        Assert.True(store >= 0);
        var value = instructions[store].Operands[1];
        var read = instructions.FindIndex(item => item.Opcode == SpirvOp.BitFieldUExtract && item.Operands[2] == value);
        Assert.True(store >= 0 && store < read, "The thread group counts must be initialized before their first read.");
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
        return Decode(shader.Spirv);
    }

    private static List<Instruction> Decode(byte[] spirv)
    {
        var words = new uint[spirv.Length / sizeof(uint)];
        Buffer.BlockCopy(spirv, 0, words, 0, spirv.Length);
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
