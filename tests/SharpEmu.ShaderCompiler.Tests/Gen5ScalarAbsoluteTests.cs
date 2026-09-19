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
