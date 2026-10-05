// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareRead64Tests
{
    public const uint LowerWord = 0x12345678;
    public const uint UpperWord = 0x90ABCDEF;
    public const uint UnchangedUpperWord = 0xCAFEBABE;
    public const uint SourceAddress = 8;

    public static IEnumerable<object[]> PairedReadCases()
    {
        (uint First, uint Second)[] offsets = [(0, 16), (16, 0), (3, 3), (255, 1), (1, 255), (255, 255)];
        foreach (var global in new[] { false, true })
        foreach (var readEnabled in new[] { false, true })
        foreach (var (first, second) in offsets)
            yield return [global, first, second, readEnabled];
    }

    [Theory]
    [MemberData(nameof(PairedReadCases))]
    public void EncodedPairedReadHasFourDestinationsAndCompilesOnBothBackends(
        bool global, uint firstOffset, uint secondOffset, bool readEnabled)
    {
        var program = CreatePairedReadbackProgram(global, firstOffset, secondOffset, readEnabled);
        var read = Assert.Single(program.Instructions, instruction => instruction.Opcode == "DsRead2B64");
        Assert.Equal([Gen5Operand.Vector(3), Gen5Operand.Vector(4), Gen5Operand.Vector(5), Gen5Operand.Vector(6)], read.Destinations);
        Assert.Equal([Gen5Operand.Vector(3)], read.Sources);
        var control = Assert.IsType<Gen5DataShareControl>(read.Control);
        Assert.Equal(firstOffset, control.Offset0);
        Assert.Equal(secondOffset, control.Offset1);
        Assert.Equal(global, control.Gds);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    public static uint[] PairedReadExpected(uint firstOffset, uint secondOffset, bool readEnabled) => readEnabled
        ? [LowerWord ^ firstOffset, UpperWord ^ firstOffset, LowerWord ^ secondOffset, UpperWord ^ secondOffset]
        : [SourceAddress, UnchangedUpperWord, 0x11112222, 0x33334444];

    public static Gen5ShaderProgram CreatePairedReadbackProgram(bool global, uint firstOffset, uint secondOffset, bool readEnabled)
    {
        var memory = new ShaderMemory();
        uint[] words = [0xD9DC0000u | (global ? 1u << 17 : 0) | firstOffset | (secondOffset << 8), 0x03000003, 0xBF810000];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(memory.TryWrite(0x1000, bytes));
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), 0x1000,
            out var decoded, out var error), error);

        var firstByteOffset = firstOffset * sizeof(ulong);
        var secondByteOffset = secondOffset * sizeof(ulong);
        return Program(
            MoveVector(0, 3, SourceAddress), MoveVector(4, 4, UnchangedUpperWord),
            MoveVector(8, 5, 0x11112222), MoveVector(12, 6, 0x33334444),
            MoveVector(16, 10, LowerWord ^ firstOffset), MoveVector(20, 11, UpperWord ^ firstOffset),
            MoveVector(24, 12, LowerWord ^ secondOffset), MoveVector(28, 13, UpperWord ^ secondOffset),
            DataShare(32, "DsWriteB64", global, [Gen5Operand.Vector(3), Gen5Operand.Vector(10), Gen5Operand.Vector(11)], [],
                firstByteOffset & 0xFF, firstByteOffset >> 8),
            DataShare(40, "DsWriteB64", global, [Gen5Operand.Vector(3), Gen5Operand.Vector(12), Gen5Operand.Vector(13)], [],
                secondByteOffset & 0xFF, secondByteOffset >> 8),
            MoveScalar(48, 126, readEnabled ? 1u : 0u), decoded.Instructions[0] with { Pc = 52 },
            MoveScalar(60, 126, 1), BufferAccess(64, "BufferStoreDwordx4", 4, dwords: 4, vectorData: 3), EndProgram(72));
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 0x108u)]
    [InlineData(true, 0u)]
    [InlineData(true, 0x108u)]
    public void EncodedReadHasTwoDestinationsAndCompilesOnBothBackends(bool global, uint offset)
    {
        var program = CreateReadbackProgram(global, offset, true);
        var read = Assert.Single(program.Instructions, instruction => instruction.Opcode == "DsReadB64");
        Assert.Equal([Gen5Operand.Vector(3), Gen5Operand.Vector(4)], read.Destinations);
        Assert.Equal([Gen5Operand.Vector(3)], read.Sources);
        var control = Assert.IsType<Gen5DataShareControl>(read.Control);
        Assert.Equal(offset, control.SingleOffsetBytes);
        Assert.Equal(global, control.Gds);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var spirvError), spirvError);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out _, out var metalError), metalError);
    }

    // The address is also the first result register. Both reads must use its old value.
    public static Gen5ShaderProgram CreateReadbackProgram(bool global, uint offset, bool readEnabled)
    {
        const ulong shaderAddress = 0x1000;
        var memory = new ShaderMemory();
        uint[] words = [0xD9D80000u | (global ? 1u << 17 : 0) | offset, 0x03000003u, 0xBF810000u];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        Assert.True(memory.TryWrite(shaderAddress, bytes));
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), shaderAddress,
            out var decoded, out var error), error);

        return Program(
            MoveVector(0, 3, SourceAddress),
            MoveVector(4, 4, UnchangedUpperWord),
            MoveVector(8, 5, LowerWord),
            MoveVector(12, 6, UpperWord),
            DataShare(16, "DsWriteB64", global, [Gen5Operand.Vector(3), Gen5Operand.Vector(5), Gen5Operand.Vector(6)], [],
                offset & 0xFF, offset >> 8),
            MoveScalar(24, 126, readEnabled ? 1u : 0u),
            decoded.Instructions[0] with { Pc = 28 },
            MoveScalar(36, 126, 1),
            BufferAccess(40, "BufferStoreDwordx2", 4, dwords: 2, vectorData: 3),
            EndProgram(48));
    }

    private sealed class ShaderMemory : ICpuMemory
    {
        private const ulong BaseAddress = 0x1000;
        private readonly byte[] _bytes = new byte[256];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (destination.Length > _bytes.Length || address < BaseAddress || address - BaseAddress > (ulong)(_bytes.Length - destination.Length)) return false;
            _bytes.AsSpan((int)(address - BaseAddress), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (source.Length > _bytes.Length || address < BaseAddress || address - BaseAddress > (ulong)(_bytes.Length - source.Length)) return false;
            source.CopyTo(_bytes.AsSpan((int)(address - BaseAddress), source.Length));
            return true;
        }
    }
}
