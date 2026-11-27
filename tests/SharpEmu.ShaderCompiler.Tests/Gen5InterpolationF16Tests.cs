// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5InterpolationF16Tests
{
    // VOP3 word 0: encoding D4, opcode [25:16], OPSEL[3] at 14, VDST [7:0].
    private static uint Word0(uint opcode, uint destination, bool highDestination = false) =>
        0xD4000000u | (opcode << 16) | (highDestination ? 1u << 14 : 0) | destination;

    // VOP3 word 1: ATTR [5:0], ATTRCHAN [7:6], HIGH [8] in the SRC0 field, the I/J VGPR in SRC1,
    // SRC2 at [26:18].
    private static uint Word1(uint attribute, uint channel, uint ij, uint source2 = 0, bool high = false) =>
        attribute | (channel << 6) | (high ? 1u << 8 : 0) | ((0x100u + ij) << 9) | (source2 << 18);

    [Theory]
    [InlineData(0x342u, "VInterpP1llF16")]
    [InlineData(0x343u, "VInterpP1lvF16")]
    [InlineData(0x35Au, "VInterpP2F16")]
    public void Decode_ReadsAttributeChannelAndOperands(uint opcode, string name)
    {
        var instruction = Decode(Word0(opcode, 9, highDestination: true), Word1(5, 2, 3, 0x107u, high: true));
        Assert.Equal(name, instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        var control = Assert.IsType<Gen5InterpolationControl>(instruction.Control);
        Assert.Equal(5u, control.Attribute);
        Assert.Equal(2u, control.Channel);
        Assert.True(control.HighAttribute);
        Assert.True(control.HighDestination);
        Assert.Equal(Gen5Operand.Vector(9), Assert.Single(instruction.Destinations));
        Assert.Equal(Gen5Operand.Vector(3), instruction.Sources[0]);
        if (opcode != 0x342)
        {
            Assert.Equal(Gen5Operand.Vector(7), instruction.Sources[1]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void P1llThenP2_TranslatesToAValidModule(bool perVertexSibling)
    {
        var p1 = Decode(Word0(0x342, 8), Word1(1, 0, 3)) with { Pc = 0 };
        var p2 = Decode(Word0(0x35A, 9), Word1(1, 0, 4, 0x108u)) with { Pc = 8 };
        var instructions = new List<Gen5ShaderInstruction> { p1, p2 };
        if (perVertexSibling)
        {
            instructions.Add(new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Vintrp, "VInterpMovF32",
                [2], [Gen5Operand.Vector(1)], [Gen5Operand.Vector(10)], new Gen5InterpolationControl(1, 1)));
        }

        instructions.Add(ResourceTestProgram.EndProgram(24));
        var program = ResourceTestProgram.Program(instructions.ToArray());
        var (plan, resources, layout) = ResourceTestProgram.Prepare(program, ShaderStage.Pixel, userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = 2,
            PixelInputEnable = 2,
            PixelInputCntl = [0, 0x1],
            PixelCustomInterpolationMask = 0,
            SupportsPerVertexPixelInputs = true,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    private static Gen5ShaderInstruction Decode(params uint[] words)
    {
        var bytes = new byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(words.Length * sizeof(uint)), 0xBF810000);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
