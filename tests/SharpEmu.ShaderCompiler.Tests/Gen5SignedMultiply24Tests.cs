// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SignedMultiply24Tests
{
    public static TheoryData<uint, uint, uint> Products => new()
    {
        { 0, 0xFFFFFF, 0 },
        { 19, 7, 133 },
        { 0xFFFFFF, 2, 0xFFFFFFFE },
        { 2, 0xFFFFFF, 0xFFFFFFFE },
        { 0xFFFFFE, 0xFFFFFD, 6 },
        { 0x800000, 1, 0xFF800000 },
        { 0x7FFFFF, 0x7FFFFF, 0xFF000001 },
        { 0x800000, 0x800000, 0 },
        { 0xAA000003, 0xBBFFFFFE, 0xFFFFFFFA },
    };

    [Fact]
    public void DecodeReportedWord()
    {
        var instruction = Decode(0x120C1693);
        Assert.Equal("VMulI32I24", instruction.Opcode);
        Assert.Equal(Gen5Operand.Source(147), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(11), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(6), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void DecodeLiteralSource()
    {
        var instruction = Decode(0x120C06FF, 0xAAFFFFFE);
        Assert.Equal("VMulI32I24", instruction.Opcode);
        Assert.Equal(new Gen5Operand(Gen5OperandKind.LiteralConstant, 0xAAFFFFFE), instruction.Sources[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothEncodingsCompileOnBothBackends(bool extendedEncoding)
    {
        var program = CreateReadbackProgram(extendedEncoding, true);
        var multiply = Assert.Single(program.Instructions, instruction => instruction.Opcode == "VMulI32I24");
        Assert.Equal(extendedEncoding ? Gen5ShaderEncoding.Vop3 : Gen5ShaderEncoding.Vop2, multiply.Encoding);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metalShader, out var metalError), metalError);
        Assert.Contains("<< 8u", metalShader.Source);
        Assert.Contains(">> 8", metalShader.Source);
    }

    [Theory]
    [MemberData(nameof(Products))]
    public void ResourceEvaluationUsesSignedLow24Bits(uint left, uint right, uint expected)
    {
        var program = Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9),
            Decode(0x120C0702) with { Pc = 8 },
            Vop1(12, "VReadfirstlaneB32", 4, Gen5Operand.Vector(6)) with { Destinations = [Gen5Operand.Scalar(4)] },
            MoveScalar(16, 5, 0), MoveScalar(20, 6, 16), MoveScalar(24, 7, 0),
            BufferLoad(28, 4), EndProgram(36));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = left;
        registers[9] = right;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs(registers), out var result));
        Assert.Equal(expected, result.Dwords[0]);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(bool extendedEncoding, bool multiplyEnabled)
    {
        var multiply = extendedEncoding ? Decode(0xD5090006, 0x00020702) : Decode(0x120C0702);
        return Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9),
            MoveVector(8, 6, 0xCAFEBABE), MoveScalar(12, 126, multiplyEnabled ? 1u : 0u),
            multiply with { Pc = 16 }, MoveScalar(24, 126, 1),
            BufferAccess(28, "BufferStoreDword", 4, vectorData: 6), EndProgram(36));
    }

    private static Gen5ShaderInstruction Decode(params uint[] instructionWords)
    {
        var bytes = new byte[(instructionWords.Length + 1) * sizeof(uint)];
        for (var index = 0; index < instructionWords.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), instructionWords[index]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(instructionWords.Length * sizeof(uint)), 0xBF810000);
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
