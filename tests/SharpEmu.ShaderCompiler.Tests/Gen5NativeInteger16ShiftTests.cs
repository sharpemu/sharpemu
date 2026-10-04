// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5NativeInteger16ShiftTests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint SourcesV1V2V3 = 0x040E0501;

    [Theory]
    [InlineData(0x307u, "VLshrrevB16")]
    [InlineData(0x308u, "VAshrrevI16")]
    [InlineData(0x314u, "VLshlrevB16")]
    public void NativeInteger16ShiftFamilyDecodes(uint opcode, string expected)
    {
        // Select the high half of both sources and the high destination half.
        var instruction = Decode(0xD0005805u | (opcode << 16), SourcesV1V2V3);

        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(
            [Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)],
            instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(5), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
        Assert.Equal(0b1011u, control.OperandSelect);
    }

    [Fact]
    public void NativeInteger16ShiftFamilyEmitsAllShiftKindsAndHalfMerges()
    {
        var lowControl = Control(operandSelect: 0);
        var highControl = Control(operandSelect: 0b1011);
        var program = Program(
            Shift(0, "VLshrrevB16", destination: 4, lowControl),
            Shift(8, "VAshrrevI16", destination: 5, highControl),
            Shift(16, "VLshlrevB16", destination: 6, highControl),
            EndProgram(24));

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                Request(program, userDataCount: 0),
                out var shader,
                out var error),
            error);

        var opcodes = new SpirvModuleInspector(shader.Spirv).Opcodes;
        Assert.Contains((ushort)SpirvOp.ShiftRightLogical, opcodes);
        Assert.Contains((ushort)SpirvOp.ShiftRightArithmetic, opcodes);
        Assert.Contains((ushort)SpirvOp.ShiftLeftLogical, opcodes);
        Assert.Contains((ushort)SpirvOp.BitFieldSExtract, opcodes);
        Assert.Contains((ushort)SpirvOp.BitwiseAnd, opcodes);
        Assert.Contains((ushort)SpirvOp.BitwiseOr, opcodes);
    }

    [Fact]
    public void NativeInteger16SubtractionDecodesAndEmitsWithHalfMerge()
    {
        // Select the high half of both sources and the high destination half.
        var decoded = Decode(0xD0005805u | (0x30Eu << 16), SourcesV1V2V3);
        Assert.Equal("VSubNcI16", decoded.Opcode);
        Assert.Equal(0b1011u, Assert.IsType<Gen5Vop3Control>(decoded.Control).OperandSelect);

        var program = Program(
            new Gen5ShaderInstruction(
                0,
                Gen5ShaderEncoding.Vop3,
                "VSubNcI16",
                [0u, 0u],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
                [Gen5Operand.Vector(4)],
                Control(operandSelect: 0b1011)),
            EndProgram(8));

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                Request(program, userDataCount: 0),
                out var shader,
                out var error),
            error);

        var opcodes = new SpirvModuleInspector(shader.Spirv).Opcodes;
        Assert.Contains((ushort)SpirvOp.ISub, opcodes);
        Assert.Contains((ushort)SpirvOp.BitwiseAnd, opcodes);
        Assert.Contains((ushort)SpirvOp.BitwiseOr, opcodes);
    }

    [Fact]
    public void NativeInteger16ShiftRejectsUndefinedArithmeticModifiers()
    {
        var invalidControl = Control(operandSelect: 0) with { AbsoluteMask = 1 };
        var program = Program(
            Shift(0, "VLshrrevB16", destination: 4, invalidControl),
            EndProgram(8));

        Assert.False(
            Gen5SpirvTranslator.TryCompileProgram(
                Request(program, userDataCount: 0),
                out _,
                out var error));
        Assert.Contains("invalid native 16-bit shift modifiers", error, StringComparison.Ordinal);
    }

    private static Gen5Vop3Control Control(uint operandSelect) =>
        new(
            AbsoluteMask: 0,
            NegateMask: 0,
            OutputModifier: 0,
            Clamp: false,
            OperandSelect: operandSelect,
            ScalarDestination: null);

    private static Gen5ShaderInstruction Shift(
        uint pc,
        string opcode,
        uint destination,
        Gen5Vop3Control control) =>
        new(
            pc,
            Gen5ShaderEncoding.Vop3,
            opcode,
            [0u, 0u],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
            [Gen5Operand.Vector(destination)],
            control);

    private static Gen5ShaderInstruction Decode(params uint[] words)
    {
        var bytes = new byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(words.Length * sizeof(uint)),
            0xBF810000u);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < ShaderAddress ||
                destination.Length > bytes.Length ||
                address - ShaderAddress > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - ShaderAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
