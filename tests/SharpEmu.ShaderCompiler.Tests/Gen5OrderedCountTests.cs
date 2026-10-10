// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5OrderedCountTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void OrderedCountDecodes()
    {
        // ds_ordered_count v4, v1 gds offset0:0x0 offset1:0x3
        var program = Decode(
        [
            0xD8FE0300u,
            1u | (4u << 24),
            SEndpgm,
        ]);

        Assert.Equal(["DsOrderedCount", "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
        var instruction = program.Instructions[0];
        Assert.Equal(new[] { Gen5OperandKind.ScalarRegister, Gen5OperandKind.VectorRegister }, instruction.Sources.Select(source => source.Kind));
        Assert.Equal(1u, instruction.Sources[1].Value);
        Assert.Equal(4u, Assert.Single(instruction.Destinations).Value);
    }

    [Theory]
    [InlineData(ShaderStage.Compute)]
    [InlineData(ShaderStage.Pixel)]
    public void OrderedCountOnTheGlobalDataShareIsAnAtomicAdd(ShaderStage stage)
    {
        var program = Program(
            DataShare(0, "DsOrderedCount", gds: true, [Gen5Operand.Scalar(124), Gen5Operand.Vector(1)], [4u], offset0: 8, offset1: 3),
            EndProgram(8));

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, stage), out var shader, out var error), error);
        var opcodes = new SpirvModuleInspector(shader.Spirv).Opcodes;
        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.AtomicISub, opcodes);
        if (stage == ShaderStage.Compute)
        {
            // Every active lane gets the old counter back, which takes the same broadcast as append and consume.
            Assert.Contains((ushort)SpirvOp.GroupNonUniformShuffle, opcodes);
        }
    }

    [Fact]
    public void OrderedCountWithoutTheGlobalDataShareBitIsRejected()
    {
        var program = Program(
            DataShare(0, "DsOrderedCount", gds: false, [Gen5Operand.Scalar(124), Gen5Operand.Vector(1)], [4u], offset1: 3),
            EndProgram(8));

        Assert.False(Gen5SpirvTranslator.TryCompileProgram(Request(program, ShaderStage.Compute), out _, out var error));
        Assert.Contains("DsOrderedCount", error, StringComparison.Ordinal);
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        return program;
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < baseAddress || address - baseAddress > int.MaxValue)
            {
                return false;
            }

            offset = (int)(address - baseAddress);
            return offset <= _storage.Length - length;
        }
    }
}
