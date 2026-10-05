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

public sealed class Gen5ScalarAbsoluteTests
{
    [Theory]
    [InlineData(12u, true)]
    [InlineData(30u, false)]
    [InlineData(0u, false)]
    public void SampleAdjustQuadmaskOnlyDiscardsReservedBits(uint shift, bool accepted)
    {
        var sample = Image(20, "ImageSampleA", 0, 8) with { Words = [0xF0800709u, 0u] };
        var program = Program(
            Decode(0x7D840A81),
            Decode(0xBEEA2D6A) with { Pc = 4 },
            Decode(0x8F38806Au | (shift << 8)) with { Pc = 8 },
            Decode(0x880B380B) with { Pc = 12 }, sample, EndProgram(28));
        if (!accepted)
        {
            Assert.Throws<ResourcePlanException>(() => Extract(program, userDataCount: 16));
            return;
        }
        var plan = Extract(program, userDataCount: 16);
        var registers = new uint[16];
        registers[8] = 6; // Border clamp keeps the actual border fields live.
        registers[11] = 0xC0000123;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Samplers[0].Source, Inputs(registers), out var result));
        Assert.Equal(0xC0000123u, result.Dwords[3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopCarriedSamplerMaskMustBeInvariant(bool changesMask)
    {
        var sample = Image(28, "ImageSampleA", 0, 8) with { Words = [0xF0800709u, 0u] };
        var update = changesMask
            ? Sop2(36, "SAddI32", 56, Gen5Operand.Scalar(56), Operand(1))
            : Sop2(36, "SAddI32", 24, Gen5Operand.Scalar(24), Operand(1));
        var program = Program(
            Decode(0x7D840A81), Decode(0xBEEA2D6A) with { Pc = 4 },
            Decode(0x8F388C6A) with { Pc = 8 }, MoveScalar(12, 24, 0),
            Decode(0xBE8B030F) with { Pc = 20 }, Decode(0x880B380B) with { Pc = 24 }, sample,
            update, Sopc(40, "SCmpLgU32", Gen5Operand.Scalar(24), Operand(4)),
            Branch(44, "SCbranchScc1", -7), EndProgram(48));

        var plan = Extract(program, userDataCount: 16);
        var registers = new uint[16];
        registers[8] = 6;
        registers[15] = 0xC0000123;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Samplers[0].Source, Inputs(registers), out var result));
        Assert.Equal(changesMask ? 0u : registers[15], result.Dwords[3]);
    }

    public static TheoryData<uint, uint, uint> QuadmaskValues => new()
    {
        { 0, 0, 0 }, { 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFF },
        { 0x80000000, 0, 0x80 }, { 0, 0x80000000, 0x8000 },
        { 0x10101010, 0x01010101, 0x55AA }, { 0xF, 0xF, 0x101 },
    };

    [Theory]
    [InlineData(0u, 0u, 0u, 0u)]
    [InlineData(0x80000000u, 1u, 0xF0000000u, 15u)]
    [InlineData(0x01010101u, 0x10101010u, 0x0F0F0F0Fu, 0xF0F0F0F0u)]
    [InlineData(uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue)]
    public void WholeQuadResourceEvaluationPreservesBothHalvesAndCondition(uint low, uint high, uint expectedLow, uint expectedHigh)
    {
        var program = Program(Decode(0xBE880A08), Decode(0x850B8180) with { Pc = 4 },
            MoveScalar(8, 6, 16), MoveScalar(12, 7, 0), BufferLoad(16, 8), EndProgram(24));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = low;
        registers[9] = high;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(registers), out var result));
        Assert.Equal(expectedLow, result.Dwords[0]);
        Assert.Equal(expectedHigh, result.Dwords[1]);
        Assert.Equal((expectedLow | expectedHigh) == 0 ? 1u : 0u, result.Dwords[3]);
    }

    [Fact]
    public void DecodeReportedQuadmaskWord()
    {
        var instruction = Decode(0xBEEA2D6A);
        Assert.Equal("SQuadmaskB64", instruction.Opcode);
        Assert.Equal(Gen5Operand.Scalar(106), Assert.Single(instruction.Sources));
        Assert.Equal(Gen5Operand.Scalar(106), Assert.Single(instruction.Destinations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuadmaskCompilesOnBothBackends(bool wide)
    {
        var (plan, resources, layout) = Prepare(CreateQuadmaskReadbackProgram(wide, false, true));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [MemberData(nameof(QuadmaskValues))]
    public void QuadmaskResourceEvaluation(uint low, uint high, uint expected)
    {
        var program = Program(Decode(0xBE882D08), MoveScalar(4, 6, 16), MoveScalar(8, 7, 0), BufferLoad(12, 8), EndProgram(20));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = low;
        registers[9] = high;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source, Inputs(registers), out var result));
        Assert.Equal(expected, result.Dwords[0]);
        Assert.Equal(0u, result.Dwords[1]);
    }

    public static Gen5ShaderProgram CreateQuadmaskReadbackProgram(bool wide, bool emptyExecutionMask, bool overlapDestination)
    {
        var destination = overlapDestination ? 8u : 10u;
        return Program(
            MoveScalar(0, 126, emptyExecutionMask ? 0u : 1u),
            Decode(0xBE802C08u | (destination << 16) | (wide ? 0x100u : 0u)) with { Pc = 4 },
            Decode(0x850F8180) with { Pc = 8 },
            MoveScalar(12, 126, 1),
            MoveVectorFromScalar(16, 12, destination),
            MoveVectorFromScalar(20, 13, destination + 1),
            MoveVectorFromScalar(24, 14, 15),
            BufferAccess(28, "BufferStoreDwordx3", 4, dwords: 3, vectorData: 12), EndProgram(36));
    }

    public static TheoryData<uint, uint> Values => new()
    {
        { 0, 0 }, { 1, 1 }, { 0xFFFFFFFF, 1 },
        { 0x7FFFFFFF, 0x7FFFFFFF }, { 0x80000000, 0x80000000 },
        { 0x80000001, 0x7FFFFFFF },
    };

    [Fact]
    public void DecodeReportedWord()
    {
        var instruction = Decode(0xBEAE3430);
        Assert.Equal("SAbsI32", instruction.Opcode);
        Assert.Equal(Gen5Operand.Scalar(48), Assert.Single(instruction.Sources));
        Assert.Equal(Gen5Operand.Scalar(46), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void CompilesOnBothBackends()
    {
        var (plan, resources, layout) = Prepare(CreateReadbackProgram(false, false));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void ResourceEvaluationPreservesMagnitudeAndCondition(uint input, uint expected)
    {
        var condition = Decode(0x85058180) with { Pc = 4 };
        var program = Program(Decode(0xBE843408), condition,
            MoveScalar(8, 6, 16), MoveScalar(12, 7, 0), BufferLoad(16, 4), EndProgram(24));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = input;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs(registers), out var result));
        Assert.Equal(expected, result.Dwords[0]);
        Assert.Equal(input == 0 ? 1u : 0u, result.Dwords[1]);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(bool emptyExecutionMask, bool overlapDestination)
    {
        var destination = overlapDestination ? 8u : 10u;
        return Program(
            MoveScalar(0, 126, emptyExecutionMask ? 0u : 1u),
            Decode(0xBE803408u | (destination << 16)) with { Pc = 4 },
            Decode(0x85098180) with { Pc = 8 },
            MoveScalar(12, 126, 1),
            MoveVectorFromScalar(16, 6, destination),
            MoveVectorFromScalar(20, 7, 9),
            BufferAccess(24, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 6), EndProgram(32));
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
