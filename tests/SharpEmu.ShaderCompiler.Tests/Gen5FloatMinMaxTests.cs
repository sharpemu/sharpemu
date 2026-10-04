// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5FloatMinMaxTests
{
    public static readonly uint[] Inputs =
    [
        0x4000_0000, // 2.0
        0x7FC0_0000, // quiet NaN
        0x0000_0000, // +0
        0x8000_0000, // -0
        0x4040_0000, // 3.0
        0x3F80_0000, // 1.0
        0x7FA0_0001, // signalling NaN
    ];

    public static readonly uint[] ExpectedResults =
    [
        0x4000_0000, // min(2, qNaN) = 2
        0x4000_0000, // max(2, qNaN) = 2
        0x8000_0000, // min(-0, +0) = -0
        0x0000_0000, // max(+0, -0) = +0
        0x4000_0000, // min(sNaN, 2) = 2
        0x4000_0000, // max(2, sNaN) = 2
        0x4000_0000, // min3(2, qNaN, 3) = 2
        0x4000_0000, // max3(2, qNaN, 1) = 2
        0x4000_0000, // med3(2, 3, sNaN) uses min3
        0x4000_0000, // med3(2, 3, qNaN) uses min3
        0x4000_0000, // med3(sNaN, 2, 3) uses min3
    ];

    [Fact]
    public void VulkanTranslatorEmitsExplicitNanAndSignedZeroSemantics()
    {
        var request = Request(CreateReadbackProgram(), userDataCount: 15);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error),
            error);

        var instructions = ReadInstructions(shader.Spirv);
        Assert.DoesNotContain(
            instructions,
            instruction => instruction.Opcode == SpirvOp.ExtInst &&
                instruction.Operands.Length > 3 &&
                (instruction.Operands[3] == 37 || instruction.Operands[3] == 40));
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.FOrdLessThan);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.FOrdGreaterThanEqual);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.BitwiseOr);
        Assert.Contains(instructions, instruction => instruction.Opcode == SpirvOp.BitwiseAnd);
        Assert.True(instructions.Count(instruction => instruction.Opcode == SpirvOp.LogicalOr) >= 2);
    }

    public static Gen5ShaderProgram CreateReadbackProgram() => Program(
        MoveVectorFromScalar(0, 0, 8),
        MoveVectorFromScalar(4, 1, 9),
        MoveVectorFromScalar(8, 2, 10),
        MoveVectorFromScalar(12, 3, 11),
        MoveVectorFromScalar(16, 4, 12),
        MoveVectorFromScalar(20, 5, 13),
        MoveVectorFromScalar(24, 6, 14),
        Vop2(28, "VMinF32", 10, Gen5Operand.Vector(0), Gen5Operand.Vector(1)),
        Vop2(32, "VMaxF32", 11, Gen5Operand.Vector(0), Gen5Operand.Vector(1)),
        Vop2(36, "VMinF32", 12, Gen5Operand.Vector(3), Gen5Operand.Vector(2)),
        Vop2(40, "VMaxF32", 13, Gen5Operand.Vector(2), Gen5Operand.Vector(3)),
        Vop2(44, "VMinF32", 14, Gen5Operand.Vector(6), Gen5Operand.Vector(0)),
        Vop2(48, "VMaxF32", 15, Gen5Operand.Vector(0), Gen5Operand.Vector(6)),
        Vop3(52, "VMin3F32", 16, Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(4)),
        Vop3(60, "VMax3F32", 17, Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(5)),
        Vop3(68, "VMed3F32", 18, Gen5Operand.Vector(0), Gen5Operand.Vector(4), Gen5Operand.Vector(6)),
        Vop3(76, "VMed3F32", 19, Gen5Operand.Vector(0), Gen5Operand.Vector(4), Gen5Operand.Vector(1)),
        Vop3(84, "VMed3F32", 20, Gen5Operand.Vector(6), Gen5Operand.Vector(0), Gen5Operand.Vector(4)),
        BufferAccess(92, "BufferStoreDwordx4", 4, dwords: 4, vectorData: 10),
        BufferAccess(100, "BufferStoreDwordx4", 4, offset: 16, dwords: 4, vectorData: 14),
        BufferAccess(108, "BufferStoreDwordx3", 4, offset: 32, dwords: 3, vectorData: 18),
        EndProgram(116));

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
