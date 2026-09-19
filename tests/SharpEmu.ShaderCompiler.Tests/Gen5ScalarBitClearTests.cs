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

public sealed class Gen5ScalarBitClearTests
{
    public static TheoryData<uint, uint, uint> Values => new()
    {
        { 0xFFFFFFFF, 0, 0xFFFFFFFE },
        { 0xFFFFFFFF, 31, 0x7FFFFFFF },
        { 0xFFFFFFFF, 32, 0xFFFFFFFE },
        { 0xFFFFFFFF, 0xFFFFFFFF, 0x7FFFFFFF },
        { 0, 31, 0 },
        { 0xA5A5A5A5, 2, 0xA5A5A5A1 },
        { 0xA5A5A5A5, 1, 0xA5A5A5A5 },
    };

    [Fact]
    public void DecodeReportedWord()
    {
        var instruction = Decode(0xBEEB1B9F);
        Assert.Equal("SBitset0B32", instruction.Opcode);
        Assert.Equal(Gen5Operand.Scalar(107), Assert.Single(instruction.Destinations));
        Assert.Equal(Gen5Operand.Source(0x9F), Assert.Single(instruction.Sources));
    }

    [Theory]
    [InlineData(0xBE881B09u)]
    [InlineData(0xBE881D09u)]
    public void BitUpdatePreservesTheImplicitDestinationInput(uint word)
    {
        var registers = BindingLayout.CollectUserDataRegisters(Program(Decode(word), EndProgram(4)), 0, 16);
        Assert.Contains(8u, registers);
        Assert.Contains(9u, registers);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void ResourceEvaluationClearsOnlySelectedBit(uint initial, uint selector, uint expected)
    {
        var program = Program(
            MoveScalar(0, 4, initial),
            Decode(0xBE841B08) with { Pc = 4 },
            MoveScalar(8, 5, 0), MoveScalar(12, 6, 16), MoveScalar(16, 7, 0),
            BufferLoad(20, 4), EndProgram(28));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = selector;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs(registers), out var result));
        Assert.Equal(expected, result.Dwords[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompilesOnBothBackends(bool overlapDestination)
    {
        var (plan, resources, layout) = Prepare(CreateReadbackProgram(false, overlapDestination, true));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(bool emptyExecutionMask, bool overlapDestination, bool condition)
    {
        return Program(
            Decode(condition ? 0xBF008080u : 0xBF008180u),
            MoveScalar(4, 126, emptyExecutionMask ? 0u : 1u),
            Decode(overlapDestination ? 0xBE881B08u : 0xBE881B09u) with { Pc = 8 },
            Decode(0x850A8180) with { Pc = 12 },
            MoveScalar(16, 126, 1),
            MoveVectorFromScalar(20, 6, 8),
            MoveVectorFromScalar(24, 7, 10),
            BufferAccess(28, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 6), EndProgram(36));
    }

    private static Gen5ShaderInstruction Decode(uint word)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, word);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0xBF810000);
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
