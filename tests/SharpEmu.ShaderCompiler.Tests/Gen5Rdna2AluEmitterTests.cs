// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Rdna2AluEmitterTests
{
    [Fact]
    public void PackedIntegerVop3pFamilyCompilesWithLaneSelectorsAndNegation()
    {
        string[] opcodes =
        [
            "VPkMadI16", "VPkMulLoU16", "VPkAddI16", "VPkSubI16",
            "VPkLshlrevB16", "VPkLshrrevB16", "VPkAshrrevI16",
            "VPkMaxI16", "VPkMinI16", "VPkMadU16", "VPkAddU16",
            "VPkSubU16", "VPkMaxU16", "VPkMinU16",
        ];
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (var index = 0; index < opcodes.Length; index++)
        {
            var sources = opcodes[index] is "VPkMadI16" or "VPkMadU16"
                ? new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2) }
                : new[] { Gen5Operand.Vector(0), Gen5Operand.Vector(1) };
            instructions.Add(new Gen5ShaderInstruction(
                pc,
                Gen5ShaderEncoding.Vop3p,
                opcodes[index],
                [0u, 0u],
                sources,
                [Gen5Operand.Vector((uint)(8 + index))],
                new Gen5Vop3pControl(
                    OpSelMask: 0b010,
                    OpSelHiMask: 0b101,
                    NegLoMask: 0b001,
                    NegHiMask: 0b110,
                    Clamp: false)));
            pc += 8;
        }

        instructions.Add(ResourceTestProgram.EndProgram(pc));
        var program = new Gen5ShaderProgram(0, instructions);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                ResourceTestProgram.Request(program, userDataCount: 0),
                out var shader,
                out var error),
            error);

        var emitted = ReadInstructions(shader.Spirv).Select(static item => item.Opcode).ToArray();
        Assert.Contains((ushort)SpirvOp.IMul, emitted);
        Assert.Contains((ushort)SpirvOp.IAdd, emitted);
        Assert.Contains((ushort)SpirvOp.ISub, emitted);
        Assert.Contains((ushort)SpirvOp.ShiftLeftLogical, emitted);
        Assert.Contains((ushort)SpirvOp.ShiftRightLogical, emitted);
        Assert.Contains((ushort)SpirvOp.ShiftRightArithmetic, emitted);
        Assert.Contains((ushort)SpirvOp.SLessThan, emitted);
        Assert.Contains((ushort)SpirvOp.UGreaterThan, emitted);
        Assert.Contains((ushort)SpirvOp.BitwiseXor, emitted);
    }

    [Fact]
    public void PackedIntegerVop3pRejectsUndefinedClampModifier()
    {
        var program = new Gen5ShaderProgram(
            0,
            [
                new Gen5ShaderInstruction(
                    0,
                    Gen5ShaderEncoding.Vop3p,
                    "VPkAddU16",
                    [0u, 0u],
                    [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                    [Gen5Operand.Vector(2)],
                    new Gen5Vop3pControl(
                        OpSelMask: 0,
                        OpSelHiMask: 0b111,
                        NegLoMask: 0,
                        NegHiMask: 0,
                        Clamp: true)),
                ResourceTestProgram.EndProgram(8),
            ]);

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                ResourceTestProgram.Request(program, userDataCount: 0),
                out _,
                out var error));
        Assert.Contains("VOP3P integer clamp", error, StringComparison.Ordinal);
    }

    [Fact]
    public void F16FmaUnaryAndFrexpFamiliesCompileWithoutNativeFloat16()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        foreach (var opcode in new[]
                 {
                     "VCvtF16U16", "VCvtF16I16", "VCvtU16F16", "VCvtI16F16",
                     "VRcpF16", "VSqrtF16", "VLogF16", "VExpF16", "VFloorF16",
                     "VCeilF16", "VTruncF16", "VRndneF16", "VFractF16",
                     "VSinF16", "VCosF16", "VFrexpExpI32F32", "VFrexpMantF32",
                 })
        {
            instructions.Add(ResourceTestProgram.Vop1(
                pc,
                opcode,
                8 + pc / 4,
                Gen5Operand.Vector(0)));
            pc += 4;
        }

        instructions.Add(new Gen5ShaderInstruction(
            pc,
            Gen5ShaderEncoding.Vop2,
            "VFmacF16",
            [0u],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            [Gen5Operand.Vector(40)],
            null));
        pc += 4;
        foreach (var opcode in new[] { "VFmaMkF16", "VFmaAkF16" })
        {
            instructions.Add(new Gen5ShaderInstruction(
                pc,
                Gen5ShaderEncoding.Vop2,
                opcode,
                [0u, 0u],
                [
                    Gen5Operand.Vector(0),
                    new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x3C00),
                    Gen5Operand.Vector(1),
                ],
                [Gen5Operand.Vector(41 + pc / 4)],
                null));
            pc += 8;
        }

        instructions.Add(ResourceTestProgram.EndProgram(pc));
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                ResourceTestProgram.Request(
                    new Gen5ShaderProgram(0, instructions),
                    userDataCount: 0),
                out var shader,
                out var error),
            error);

        var emitted = ReadInstructions(shader.Spirv).ToArray();
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.FDiv);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.ConvertFToS);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.ConvertSToF);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.BitFieldSExtract);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.ExtInst);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.FOrdLessThan);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.FOrdEqual);
        Assert.DoesNotContain(
            emitted,
            item => item.Opcode == (ushort)SpirvOp.Capability &&
                    item.FirstOperand == (uint)SpirvCapability.Float16);
    }

    [Fact]
    public void Fp64Vop1FamilyDeclaresCapabilityAndStoresRegisterPairs()
    {
        var program = new Gen5ShaderProgram(
            0,
            [
                ResourceTestProgram.Vop1(0, "VCvtF64I32", 2, Gen5Operand.Vector(0)),
                ResourceTestProgram.Vop1(4, "VRcpF64", 4, Gen5Operand.Vector(2)),
                ResourceTestProgram.Vop1(8, "VCvtF32F64", 6, Gen5Operand.Vector(4)),
                ResourceTestProgram.EndProgram(12),
            ]);
        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                ResourceTestProgram.Request(program, userDataCount: 0),
                out _,
                out var unsupportedError));
        Assert.Equal(
            "the host does not support the shaderFloat64 feature required by the shader's FP64 instructions",
            unsupportedError);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                ResourceTestProgram.Request(
                    program,
                    userDataCount: 0,
                    shaderFloat64Supported: true),
                out var shader,
                out var error),
            error);

        var emitted = ReadInstructions(shader.Spirv).ToArray();
        Assert.Contains(
            emitted,
            item => item.Opcode == (ushort)SpirvOp.Capability &&
                    item.FirstOperand == (uint)SpirvCapability.Float64);
        Assert.Contains(
            emitted,
            item => item.Opcode == (ushort)SpirvOp.TypeFloat && item.SecondOperand == 64);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.ConvertSToF);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.FDiv);
        Assert.Contains(emitted, item => item.Opcode == (ushort)SpirvOp.FConvert);
    }

    [Fact]
    public void ScalarRdna2FamilyCompilesWithExactDataFlow()
    {
        var program = new Gen5ShaderProgram(
            0,
            [
                Scalar(0, Gen5ShaderEncoding.Sop1, "SCmovB64", [Gen5Operand.Scalar(0)], 2),
                Scalar(4, Gen5ShaderEncoding.Sop1, "SFlbitI32B64", [Gen5Operand.Scalar(2)], 4),
                Scalar(8, Gen5ShaderEncoding.Sop1, "SQuadmaskB64", [Gen5Operand.Scalar(6)], 8),
                Scalar(
                    12,
                    Gen5ShaderEncoding.Sop2,
                    "SAbsdiffI32",
                    [Gen5Operand.Scalar(4), Gen5Operand.Scalar(5)],
                    10),
                new Gen5ShaderInstruction(
                    16,
                    Gen5ShaderEncoding.Sopp,
                    "SSleep",
                    [0u],
                    [],
                    [],
                    null),
                ResourceTestProgram.EndProgram(20),
            ]);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                ResourceTestProgram.Request(program, userDataCount: 12),
                out var shader,
                out var error),
            error);

        var emitted = ReadInstructions(shader.Spirv).Select(static item => item.Opcode).ToArray();
        Assert.Contains((ushort)SpirvOp.Select, emitted);
        Assert.Contains((ushort)SpirvOp.BitwiseXor, emitted);
        Assert.Contains((ushort)SpirvOp.ShiftRightLogical, emitted);
        Assert.Contains((ushort)SpirvOp.ISub, emitted);
    }

    private static Gen5ShaderInstruction Scalar(
        uint pc,
        Gen5ShaderEncoding encoding,
        string opcode,
        IReadOnlyList<Gen5Operand> sources,
        uint destination) =>
        new(
            pc,
            encoding,
            opcode,
            [0u],
            sources,
            [Gen5Operand.Scalar(destination)],
            null);

    private static IEnumerable<(ushort Opcode, uint FirstOperand, uint SecondOperand)> ReadInstructions(
        byte[] spirv)
    {
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            uint Operand(int index) => wordCount > index
                ? BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + index * sizeof(uint)))
                : 0;
            yield return ((ushort)header, Operand(1), Operand(2));
            offset += wordCount * sizeof(uint);
        }
    }
}
