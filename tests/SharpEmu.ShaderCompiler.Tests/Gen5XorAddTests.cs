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

public sealed class Gen5XorAddTests
{
    public const uint Sentinel = 0xCAFE_BABE;

    public static TheoryData<uint, uint, uint, uint> Results => new()
    {
        { 0, 0, 0, 0 },
        { 1, 2, 3, 6 },
        { uint.MaxValue, uint.MaxValue, 7, 7 },
        { 0xAAAA_AAAA, 0x5555_5555, 1, 0 },
        { 0x8000_0000, 0, 0x8000_0000, 0 },
        { 0x1234_5678, 0x8765_4321, 0x1020_3040, 0xA571_4599 },
    };

    [Fact]
    public void DecodeKeepsAllThreeSourcesAndOneVectorDestination()
    {
        var instruction = Decode(6);
        Assert.Equal("VXadU32", instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal([Gen5Operand.Vector(2), Gen5Operand.Vector(3), Gen5Operand.Vector(4)], instruction.Sources);
        Assert.Equal(Gen5Operand.Vector(6), Assert.Single(instruction.Destinations));
        Assert.Null(Assert.IsType<Gen5Vop3Control>(instruction.Control).ScalarDestination);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DecodeSupportsALiteralInEachSourcePosition(int sourceIndex)
    {
        uint[] sources = [0x102, 0x103, 0x104];
        sources[sourceIndex] = 0xFF;
        var instruction = DecodeWords(0xD7450006,
            sources[0] | (sources[1] << 9) | (sources[2] << 18), 0xFFFF_FFFF);
        Assert.Equal("VXadU32", instruction.Opcode);
        Assert.Equal(new Gen5Operand(Gen5OperandKind.LiteralConstant, uint.MaxValue), instruction.Sources[sourceIndex]);
    }

    [Theory]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(6u, true)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    [InlineData(6u, false)]
    public void BothBackendsCompileOverlappingAndMaskedDestinations(uint destination, bool operationEnabled)
    {
        var (plan, resources, layout) = Prepare(CreateReadbackProgram(destination, operationEnabled));
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var metalShader, out var metalError), metalError);
        Assert.Contains("^", metalShader.Source);
    }

    [Theory]
    [MemberData(nameof(Results))]
    public void ResourceEvaluationUsesUnsignedXorThenWrappingAddition(uint left, uint right, uint addend, uint expected)
    {
        var program = Program(
            MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9), MoveVectorFromScalar(8, 4, 10),
            Decode(6) with { Pc = 12 }, ReadFirstLane(20, 4, 6),
            MoveScalar(24, 5, 0), MoveScalar(28, 6, 16), MoveScalar(32, 7, 0),
            BufferLoad(36, 4), EndProgram(44));
        var plan = Extract(program);
        var registers = new uint[16];
        registers[8] = left;
        registers[9] = right;
        registers[10] = addend;
        Assert.True(RuntimeValueEvaluator.EvaluateDescriptorSource(plan, plan.Info.Buffers[0].Source,
            Inputs(registers), out var result));
        Assert.Equal(expected, result.Dwords[0]);
    }

    public static Gen5ShaderProgram CreateReadbackProgram(uint destination, bool operationEnabled) => Program(
        MoveVectorFromScalar(0, 2, 8), MoveVectorFromScalar(4, 3, 9), MoveVectorFromScalar(8, 4, 10),
        MoveVector(12, 6, Sentinel), MoveScalar(16, 126, operationEnabled ? 1u : 0u),
        Decode(destination) with { Pc = 20 }, MoveScalar(28, 126, 1),
        BufferAccess(32, "BufferStoreDword", 4, vectorData: destination), EndProgram(40));

    private static Gen5ShaderInstruction Decode(uint destination) =>
        DecodeWords(0xD7450000 | destination, 0x102u | (0x103u << 9) | (0x104u << 18));

    private static Gen5ShaderInstruction DecodeWords(params uint[] words)
    {
        var bytes = new byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
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
