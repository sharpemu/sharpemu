// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5IntegerReciprocalTests
{
    [Theory]
    [InlineData(ShaderStage.Compute, 1, false)]
    [InlineData(ShaderStage.Compute, 2, false)]
    [InlineData(ShaderStage.Compute, 0, true)]
    [InlineData(ShaderStage.Compute, 1, true)]
    [InlineData(ShaderStage.Pixel, 1, false)]
    [InlineData(ShaderStage.Vertex, 1, false)]
    public void IntegerReciprocalIsInlineWithNewtonStep(ShaderStage stage, int integerCalls, bool ordinary)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (var index = 0; index < integerCalls; index++)
            instructions.Add(Vop1((uint)instructions.Count * 4, "VRcpIflagF32", (uint)(2 + index), Gen5Operand.Vector((uint)index)));
        if (ordinary)
            instructions.Add(Vop1((uint)instructions.Count * 4, "VRcpF32", (uint)(2 + integerCalls), Gen5Operand.Vector((uint)integerCalls)));
        instructions.Add(EndProgram((uint)instructions.Count * 4));
        var request = Request(Program([.. instructions]), stage);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var module = ReadInstructions(shader.Spirv);
        Assert.DoesNotContain(module, instruction => instruction.Opcode == SpirvOp.FunctionCall);
        Assert.Single(module, instruction => instruction.Opcode == SpirvOp.Function);
        Assert.Equal(integerCalls + (ordinary ? 1 : 0), module.Count(instruction => instruction.Opcode == SpirvOp.FDiv));
        Assert.Equal(2 * integerCalls, module.Count(instruction => instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[3] == 50));
        Assert.True(module.Count(instruction => instruction.Opcode == SpirvOp.Select) >= integerCalls);
    }

    private static List<(SpirvOp Opcode, uint[] Operands)> ReadInstructions(byte[] bytes)
    {
        var words = new uint[bytes.Length / sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            words[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)));
        var result = new List<(SpirvOp, uint[])>();
        for (var index = 5; index < words.Length;)
        {
            var count = (int)(words[index] >> 16);
            Assert.InRange(count, 1, words.Length - index);
            result.Add(((SpirvOp)(words[index] & 0xFFFF), words.AsSpan(index + 1, count - 1).ToArray()));
            index += count;
        }
        return result;
    }
}
