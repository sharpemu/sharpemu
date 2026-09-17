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

public sealed class Gen5SignedPack16Tests
{
    public static TheoryData<uint, uint, uint> PackedValues => new()
    {
        { 0, 0, 0 },
        { 0x1234, 0x5678, 0x56781234 },
        { 0xFFFFFFFF, 2, 0x0002FFFF },
        { 2, 0xFFFFFFFF, 0xFFFF0002 },
        { 0xFFFF8000, 0x7FFF, 0x7FFF8000 },
        { 0x7FFF, 0xFFFF8000, 0x80007FFF },
        { 0xFFFF7FFF, 0x8000, 0x7FFF8000 },
        { 0x8000, 0xFFFF7FFF, 0x80007FFF },
        { 0x80000000, 0x7FFFFFFF, 0x7FFF8000 },
        { 0x7FFFFFFF, 0x80000000, 0x80007FFF },
        { 0x80000000, 0x80000000, 0x80008000 },
        { 0x7FFFFFFF, 0x7FFFFFFF, 0x7FFF7FFF },
    };

    [Fact]
    public void DecodeReportedWords()
    {
        var instruction = Decode(0xD76B0005, 0x00022107);
        Assert.Equal("VCvtPkI16I32", instruction.Opcode);
        Assert.Equal(Gen5Operand.Vector(7), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(16), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(5), Assert.Single(instruction.Destinations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothEncodingsCompileOnBothBackends(bool extendedEncoding)
    {
        var program = CreateReadbackProgram(extendedEncoding, true);
        var conversion = Assert.Single(program.Instructions, instruction => instruction.Opcode == "VCvtPkI16I32");
        Assert.Equal(extendedEncoding ? Gen5ShaderEncoding.Vop3 : Gen5ShaderEncoding.Vop2, conversion.Encoding);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metalShader, out var metalError), metalError);
        Assert.Contains("clamp(as_type<int>", metalShader.Source);
        Assert.Contains("-32768, 32767", metalShader.Source);
        Assert.Contains("<< 16u", metalShader.Source);
    }

    [Theory]
    [MemberData(nameof(PackedValues))]
    public void ResourceEvaluationClampsEachSignedInput(uint low, uint high, uint expected)
    {
        var program = Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9),
            Decode(0xD76B0006, 0x00020702) with { Pc = 8 },
            Vop1(16, "VReadfirstlaneB32", 4, Gen5Operand.Vector(6)) with { Destinations = [Gen5Operand.Scalar(4)] },
            MoveScalar(20, 5, 0), MoveScalar(24, 6, 16), MoveScalar(28, 7, 0),
            BufferLoad(32, 4), EndProgram(40));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = low;
        registers[9] = high;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs(registers), out var result));
        Assert.Equal(expected, result.Dwords[0]);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(bool extendedEncoding, bool conversionEnabled, bool overlapDestination = false)
    {
        var conversion = extendedEncoding ? Decode(0xD76B0006, 0x00020702) : Decode(0x620C0702);
        if (overlapDestination)
            conversion = conversion with { Destinations = [Gen5Operand.Vector(2)] };
        return Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9),
            MoveVector(8, 6, 0xCAFEBABE), MoveScalar(12, 126, conversionEnabled ? 1u : 0u),
            conversion with { Pc = 16 }, MoveScalar(24, 126, 1),
            BufferAccess(28, "BufferStoreDword", 4, vectorData: overlapDestination ? 2u : 6u), EndProgram(36));
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
