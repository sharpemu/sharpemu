// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Integer16Tests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(0x352u, "VMin3I16")]
    [InlineData(0x353u, "VMin3U16")]
    [InlineData(0x355u, "VMax3I16")]
    [InlineData(0x356u, "VMax3U16")]
    [InlineData(0x358u, "VMed3I16")]
    [InlineData(0x359u, "VMed3U16")]
    public void Vop3ThreeOperandIntegerOpsDecodeAndCompile(uint opcode, string name)
    {
        // v_xxx3_x16 v0, v1, v2, v3
        var program = Decode(
        [
            (0x35u << 26) | (opcode << 16),
            257u | (258u << 9) | (259u << 18),
            SEndpgm,
        ]);

        Assert.Equal([name, "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    [Theory]
    [InlineData(0x305u, "VMulLoU16")]
    [InlineData(0x311u, "VPackB32F16")]
    [InlineData(0x340u, "VMadU16")]
    [InlineData(0x35Eu, "VMadI16")]
    [InlineData(0x375u, "VMadI32I16")]
    public void Vop3MultiplyAndPackOpsDecodeAndCompile(uint opcode, string name)
    {
        // v_xxx v0, v1, v2, v3
        var program = Decode(
        [
            (0x35u << 26) | (opcode << 16),
            257u | (258u << 9) | (259u << 18),
            SEndpgm,
        ]);

        Assert.Equal([name, "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
    }

    [Theory]
    [InlineData(0x00u, "VPkMadI16")]
    [InlineData(0x01u, "VPkMulLoU16")]
    [InlineData(0x02u, "VPkAddI16")]
    [InlineData(0x03u, "VPkSubI16")]
    [InlineData(0x04u, "VPkLshlrevB16")]
    [InlineData(0x05u, "VPkLshrrevB16")]
    [InlineData(0x06u, "VPkAshrrevI16")]
    [InlineData(0x07u, "VPkMaxI16")]
    [InlineData(0x08u, "VPkMinI16")]
    [InlineData(0x09u, "VPkMadU16")]
    [InlineData(0x0Au, "VPkAddU16")]
    [InlineData(0x0Bu, "VPkSubU16")]
    [InlineData(0x0Cu, "VPkMaxU16")]
    [InlineData(0x0Du, "VPkMinU16")]
    public void PackedIntegerOpsDecodeAndCompile(uint opcode, string name)
    {
        // v_pk_xxx_x16 v0, v1, v2, v3
        var program = Decode(
        [
            (0x33u << 26) | (opcode << 16),
            257u | (258u << 9) | (259u << 18),
            SEndpgm,
        ]);

        Assert.Equal([name, "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
        var request = ResourceTestProgram.Request(program, userDataCount: 0);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var error), error);
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
