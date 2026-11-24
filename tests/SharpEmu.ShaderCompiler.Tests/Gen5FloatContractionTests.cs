// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5FloatContractionTests
{
    // A depth prepass and the pass that tests its depth for equality compute one position in two programs. A driver
    // that fuses a multiply with an add in only one of them moves a vertex by an ulp and the second pass drops the
    // triangle, so programs before rasterization keep each operation's rounding; pixel and compute programs may fuse.
    [Theory]
    [InlineData(ShaderStage.Vertex, true)]
    [InlineData(ShaderStage.Pixel, false)]
    [InlineData(ShaderStage.Compute, false)]
    public void ProgramsBeforeRasterizationKeepEachFloatRounding(ShaderStage stage, bool kept)
    {
        var request = Request(Program(
            Vop2(0, "VMulF32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(1)),
            Vop2(4, "VAddF32", 3, Gen5Operand.Vector(2), Gen5Operand.Vector(1)),
            Vop2(8, "VSubF32", 4, Gen5Operand.Vector(3), Gen5Operand.Vector(0)),
            Vop3(12, "VFmaF32", 5, Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(4)),
            EndProgram(20)), stage);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var module = ReadInstructions(shader.Spirv);
        var glsl = Assert.Single(module, instruction => instruction.Opcode == SpirvOp.ExtInstImport &&
            Literal(instruction.Operands.AsSpan(1)) == "GLSL.std.450").Operands[0];
        var arithmetic = module.Where(instruction => instruction.Opcode is SpirvOp.FMul or SpirvOp.FAdd or SpirvOp.FSub ||
                instruction.Opcode == SpirvOp.ExtInst && instruction.Operands[2] == glsl && instruction.Operands[3] == 50)
            .Select(instruction => (instruction.Opcode, Result: instruction.Operands[1])).ToArray();
        foreach (var opcode in new[] { SpirvOp.FMul, SpirvOp.FAdd, SpirvOp.FSub, SpirvOp.ExtInst })
            Assert.Contains(arithmetic, operation => operation.Opcode == opcode);
        var decorated = module.Where(instruction => instruction.Opcode == SpirvOp.Decorate && instruction.Operands.Length == 2 &&
            instruction.Operands[1] == (uint)SpirvDecoration.NoContraction).Select(instruction => instruction.Operands[0]).ToList();
        Assert.Equal(decorated.Count, decorated.Distinct().Count());
        Assert.All(arithmetic, operation => Assert.Equal(kept, decorated.Contains(operation.Result)));
        Assert.Equal(kept ? arithmetic.Length : 0, decorated.Count);
    }

    private static string Literal(ReadOnlySpan<uint> words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        var length = Array.IndexOf(bytes, (byte)0);
        return Encoding.ASCII.GetString(bytes, 0, length < 0 ? bytes.Length : length);
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
