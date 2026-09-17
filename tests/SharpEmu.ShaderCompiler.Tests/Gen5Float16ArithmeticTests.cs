// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Float16ArithmeticTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void CompactFloat16ArithmeticDecodesAndCompilesWithoutNativeFloat16()
    {
        var program = Decode(
        [
            0x64000501, // v_add_f16 v0, v1, v2
            0x66060B04, // v_sub_f16 v3, v4, v5
            0x680C1107, // v_subrev_f16 v6, v7, v8
            0x6A12170A, // v_mul_f16 v9, v10, v11
            0x72181D0D, // v_max_f16 v12, v13, v14
            0x741E2310, // v_min_f16 v15, v16, v17
            SEndpgm,
        ]);

        Assert.Equal(
            ["VAddF16", "VSubF16", "VSubrevF16", "VMulF16", "VMaxF16", "VMinF16", "SEndpgm"],
            program.Instructions.Select(instruction => instruction.Opcode));

        var request = ResourceTestProgram.Request(program, userDataCount: 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);

        var opcodes = ReadOpcodes(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.FAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.FSub, opcodes);
        Assert.Contains((ushort)SpirvOp.FMul, opcodes);
        Assert.True(opcodes.Count(opcode => opcode == (ushort)SpirvOp.ExtInst) >= 2);
        Assert.DoesNotContain((ushort)SpirvCapability.Float16, ReadCapabilities(shader.Spirv));
    }

    private static Gen5ShaderProgram Decode(IReadOnlyList<uint> words)
    {
        var memory = new TestCpuMemory(ShaderAddress, words.Count * sizeof(uint));
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var index = 0; index < words.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint)),
                words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, bytes));
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static IReadOnlyList<ushort> ReadOpcodes(byte[] spirv) =>
        ReadInstructions(spirv)
            .Select(instruction => instruction.Opcode)
            .ToArray();

    private static IReadOnlyList<ushort> ReadCapabilities(byte[] spirv) =>
        ReadInstructions(spirv)
            .Where(instruction => instruction.Opcode == (ushort)SpirvOp.Capability)
            .Select(instruction => (ushort)instruction.FirstOperand)
            .ToArray();

    private static IReadOnlyList<(ushort Opcode, uint FirstOperand)> ReadInstructions(
        byte[] spirv)
    {
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));
        var instructions = new List<(ushort Opcode, uint FirstOperand)>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var firstOperand = wordCount > 1
                ? BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + sizeof(uint)))
                : 0;
            instructions.Add(((ushort)header, firstOperand));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
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
