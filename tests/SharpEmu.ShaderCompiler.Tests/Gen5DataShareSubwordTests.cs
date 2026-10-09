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

public sealed class Gen5DataShareSubwordTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(0x1Eu, "DsWriteB8")]
    [InlineData(0x1Fu, "DsWriteB16")]
    [InlineData(0x3Au, "DsReadU8")]
    [InlineData(0x3Bu, "DsReadI16")]
    [InlineData(0x3Cu, "DsReadU16")]
    public void ByteAndHalfWordOpsDecode(uint opcode, string name)
    {
        // ds_xxx v4, v1 offset:0x144 / ds_xxx v1, v2 offset:0x144
        var program = Decode(
        [
            0xD8000000u | (opcode << 18) | 0x144u,
            1u | (2u << 8) | (4u << 24),
            SEndpgm,
        ]);

        Assert.Equal([name, "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
    }

    [Theory]
    [InlineData("DsWriteB8", false)]
    [InlineData("DsWriteB16", false)]
    [InlineData("DsReadU8", true)]
    [InlineData("DsReadI16", true)]
    [InlineData("DsReadU16", true)]
    public void ByteAndHalfWordOpsCompileInEveryStage(string opcode, bool read)
    {
        var program = Program(
            DataShare(0, opcode, gds: false, read ? [Gen5Operand.Vector(0)] : [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], read ? [4u] : []),
            EndProgram(8));

        foreach (var stage in new[] { ShaderStage.Compute, ShaderStage.Pixel })
        {
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(Request(program, stage), out _, out var error), $"{stage}: {error}");
        }
    }

    [Theory]
    [InlineData("DsWriteB8")]
    [InlineData("DsWriteB16")]
    public void SubwordWritesAreAtomicOnTheSharedLdsOnly(string opcode)
    {
        var program = Program(
            DataShare(0, opcode, gds: false, [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], []),
            EndProgram(8));

        // Lanes of a compute workgroup write neighbouring bytes of one dword, so the shared LDS is updated
        // with atomics; the Private LDS of a graphics stage cannot take them and needs none.
        Assert.Contains(CompiledOpcodes(program, ShaderStage.Compute), IsAtomic);
        Assert.DoesNotContain(CompiledOpcodes(program, ShaderStage.Pixel), IsAtomic);
    }

    private static bool IsAtomic(ushort opcode) =>
        opcode is >= (ushort)SpirvOp.AtomicLoad and <= (ushort)SpirvOp.AtomicXor;

    private static HashSet<ushort> CompiledOpcodes(Gen5ShaderProgram program, ShaderStage stage)
    {
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(Request(program, stage), out var shader, out var error),
            error);
        return new SpirvModuleInspector(shader.Spirv).Opcodes;
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
