// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DataShareTests
{
    private const ulong ShaderAddress = 0x1_0000_4000;
    private const uint SEndpgm = 0xBF810000;
    private const ushort OpStore = 62;
    private const ushort OpAtomicIAdd = 234;
    private const ushort OpSelectionMerge = 247;

    [Fact]
    public void DsAddRtnU32DecodesOpcode20WithDestination()
    {
        var program = DecodeProgram(0x20, offset: 128);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsAddRtnU32");
        Assert.Equal(Gen5ShaderEncoding.Ds, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(2), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Vector(3), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Vector(7), Assert.Single(instruction.Destinations));
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(128U, control.Offset0);
        Assert.False(control.Gds);
    }

    [Fact]
    public void DsAddRtnU32PreservesFullUnsignedOffset16()
    {
        var program = DecodeProgram(0x20, offset: 0xAB80);

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "DsAddRtnU32");
        var control = Assert.IsType<Gen5DataShareControl>(instruction.Control);
        Assert.Equal(0x80U, control.Offset0);
        Assert.Equal(0xABU, control.Offset1);
    }

    [Fact]
    public void DsAddRtnU32LowersToAtomicAddAndReturnedValueStore()
    {
        var noReturnOpcodes = CompileAndReadSpirvOpcodes(0x00);
        var returnOpcodes = CompileAndReadSpirvOpcodes(0x20);

        Assert.Equal(
            noReturnOpcodes.Count(opcode => opcode == OpAtomicIAdd),
            returnOpcodes.Count(opcode => opcode == OpAtomicIAdd));
        Assert.Equal(
            noReturnOpcodes.Count(opcode => opcode == OpStore) + 1,
            returnOpcodes.Count(opcode => opcode == OpStore));
        var returnOpcodeList = returnOpcodes.ToList();
        Assert.True(
            returnOpcodeList.IndexOf(OpSelectionMerge) <
            returnOpcodeList.IndexOf(OpAtomicIAdd));
    }

    private static Gen5ShaderProgram DecodeProgram(uint opcode, uint offset = 0)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader,
            0xD8000000u | (opcode << 18) | offset);
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[sizeof(uint)..],
            2u | (3u << 8) | (7u << 24));
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[(2 * sizeof(uint))..],
            SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return program;
    }

    private static IReadOnlyList<ushort> CompileAndReadSpirvOpcodes(uint opcode)
    {
        var program = DecodeProgram(opcode);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);

        return ReadSpirvOpcodes(shader.Spirv);
    }

    private static IReadOnlyList<ushort> ReadSpirvOpcodes(byte[] spirv)
    {
        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            opcodes.Add((ushort)instruction);
            offset += wordCount * sizeof(uint);
        }

        return opcodes;
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

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress)
            {
                return false;
            }

            var relative = virtualAddress - baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
