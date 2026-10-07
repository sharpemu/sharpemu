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

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 31u)]
    [InlineData(false, 32u)]
    [InlineData(false, 63u)]
    [InlineData(false, 64u)]
    [InlineData(false, 0xFFFFFFFFu)]
    [InlineData(true, 0u)]
    [InlineData(true, 31u)]
    [InlineData(true, 32u)]
    [InlineData(true, 63u)]
    [InlineData(true, 64u)]
    public void BitUpdates64PreserveTheOtherWord(bool set, uint selector)
    {
        const ulong initial = 0x80000001A5A5A5A5;
        var program = Program(MoveScalar(0, 4, unchecked((uint)initial)), MoveScalar(4, 5, (uint)(initial >> 32)),
            MoveScalar(8, 6, 16), MoveScalar(12, 7, 0), MoveScalar(16, 8, selector),
            Decode(set ? 0xBE841E08u : 0xBE841C08u) with { Pc = 20 }, BufferLoad(24, 4), EndProgram(32));
        var plan = Extract(program);
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs([]), out var result));
        var bit = 1ul << (int)(selector & 63);
        var expected = set ? initial | bit : initial & ~bit;
        Assert.Equal((uint)expected, result.Dwords[0]);
        Assert.Equal((uint)(expected >> 32), result.Dwords[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BitUpdates64CollectBothDestinationWordsAndCompile(bool set)
    {
        var program = Program(Decode(set ? 0xBE881E0Au : 0xBE881C0Au), EndProgram(4));
        var registers = BindingLayout.CollectUserDataRegisters(program, 0, 16);
        Assert.Contains(8u, registers);
        Assert.Contains(9u, registers);
        Assert.Contains(10u, registers);
        Assert.DoesNotContain(11u, registers);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x80000000u)]
    [InlineData(0xA5A5A5A5u)]
    [InlineData(0xFFFFFFFFu)]
    public void BitReplicationDoublesEverySourceBitAndCompilesOnBothBackends(uint source)
    {
        var program = Program(MoveScalar(0, 8, source), Decode(0xBE843B08u) with { Pc = 4 },
            MoveScalar(8, 6, 16), MoveScalar(12, 7, 0), BufferLoad(16, 4), EndProgram(24));
        var plan = Extract(program);
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs([]), out var descriptor));
        ulong expected = 0;
        for (var bit = 0; bit < 32; bit++)
            if ((source & (1u << bit)) != 0) expected |= 3ul << (bit * 2);
        Assert.Equal((uint)expected, descriptor.Dwords[0]);
        Assert.Equal((uint)(expected >> 32), descriptor.Dwords[1]);
        var (compiledPlan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(compiledPlan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
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
