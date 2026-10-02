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
    public void IntegerReciprocalUsesOneHelperAndPreservesDivision(ShaderStage stage, int integerCalls, bool ordinary)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        for (var index = 0; index < integerCalls; index++)
            instructions.Add(Vop1((uint)instructions.Count * 4, "VRcpIflagF32", 2, Gen5Operand.Vector(0)));
        if (ordinary)
            instructions.Add(Vop1((uint)instructions.Count * 4, "VRcpF32", 2, Gen5Operand.Vector(0)));
        instructions.Add(EndProgram((uint)instructions.Count * 4));
        var request = Request(Program([.. instructions]), stage);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var module = ReadInstructions(shader.Spirv);
        var helpers = module.Where(instruction => instruction.Opcode == SpirvOp.Function && instruction.Operands[2] == 2).ToArray();
        var calls = module.Where(instruction => instruction.Opcode == SpirvOp.FunctionCall).ToArray();
        Assert.Equal(integerCalls == 0 ? 0 : 1, helpers.Length);
        Assert.Equal(integerCalls, calls.Length);
        Assert.Equal((integerCalls == 0 ? 0 : 1) + (ordinary ? 1 : 0), module.Count(instruction => instruction.Opcode == SpirvOp.FDiv));
        if (integerCalls == 0) return;

        var helper = Assert.Single(helpers).Operands[1];
        Assert.All(calls, call => Assert.Equal(helper, call.Operands[2]));
        var start = module.FindIndex(instruction => instruction.Opcode == SpirvOp.Function && instruction.Operands[1] == helper);
        var end = module.FindIndex(start + 1, instruction => instruction.Opcode == SpirvOp.FunctionEnd);
        Assert.True(end > start);
        var body = module.GetRange(start + 1, end - start - 1);
        Assert.Equal(new[] { SpirvOp.FunctionParameter, SpirvOp.Label, SpirvOp.FDiv, SpirvOp.ReturnValue }, body.Select(instruction => instruction.Opcode));
        var divide = body[2].Operands;
        Assert.Equal(body[0].Operands[1], divide[3]);
        Assert.Equal(divide[1], body[3].Operands[0]);
        Assert.Contains(module, instruction => instruction.Opcode == SpirvOp.Constant &&
            instruction.Operands[1] == divide[2] && instruction.Operands[2] == 0x3F800000);
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
