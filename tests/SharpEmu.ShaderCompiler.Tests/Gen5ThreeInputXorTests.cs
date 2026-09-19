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

public sealed class Gen5ThreeInputXorTests
{
    public static TheoryData<uint, uint, uint, uint> Values => new()
    {
        { 0, 0, 0, 0 },
        { 1, 2, 4, 7 },
        { 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF },
        { 0xAAAAAAAA, 0x55555555, 0xFFFFFFFF, 0 },
        { 0x80000000, 0, 1, 0x80000001 },
        { 0x12345678, 0x12345678, 0x87654321, 0x87654321 },
    };

    [Fact]
    public void DecodeExtendedWords()
    {
        var instruction = Decode(0xD5780011, 0x0404D46B);
        Assert.Equal("VXor3B32", instruction.Opcode);
        Assert.Equal(Gen5Operand.Scalar(107), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(106), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(1), instruction.Sources[2]);
        Assert.Equal(Gen5Operand.Vector(17), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void CompilesOnBothBackends()
    {
        var program = CreateReadbackProgram(true);
        var conversion = Assert.Single(program.Instructions, instruction => instruction.Opcode == "VXor3B32");
        Assert.Equal(Gen5ShaderEncoding.Vop3, conversion.Encoding);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metalShader, out var metalError), metalError);
        Assert.Contains(" ^ ", metalShader.Source);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void ResourceEvaluationUsesAllThreeInputs(uint first, uint second, uint third, uint expected)
    {
        var program = Program(
            MoveVectorFromScalar(0, 4, 10),
            Decode(0xD5780006, 0x04101208) with { Pc = 8 },
            Vop1(16, "VReadfirstlaneB32", 4, Gen5Operand.Vector(6)) with { Destinations = [Gen5Operand.Scalar(4)] },
            MoveScalar(20, 5, 0), MoveScalar(24, 6, 16), MoveScalar(28, 7, 0),
            BufferLoad(32, 4), EndProgram(40));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = first;
        registers[9] = second;
        registers[10] = third;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs(registers), out var result));
        Assert.Equal(expected, result.Dwords[0]);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(bool conversionEnabled, bool overlapDestination = false)
    {
        var conversion = Decode(0xD5780006, 0x04101208);
        if (overlapDestination)
            conversion = conversion with { Destinations = [Gen5Operand.Vector(4)] };
        return Program(
            MoveVectorFromScalar(0, 4, 10),
            MoveVector(8, 6, 0xCAFEBABE), MoveScalar(12, 126, conversionEnabled ? 1u : 0u),
            conversion with { Pc = 16 }, MoveScalar(24, 126, 1),
            BufferAccess(28, "BufferStoreDword", 4, vectorData: overlapDestination ? 4u : 6u), EndProgram(36));
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
