// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Vop3Tests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;
    private const ushort OpBitFieldSExtract = 202;
    private const ushort OpBitFieldUExtract = 203;
    private const ushort OpArrayLength = 68;
    private const ushort OpULessThan = 176;

    [Fact]
    public void VBfeI32DecodesFromVop3Opcode149()
    {
        var program = DecodeProgram(0x149);

        var instruction = Assert.Single(program.Instructions, item => item.Opcode == "VBfeI32");
        Assert.Equal(Gen5ShaderEncoding.Vop3, instruction.Encoding);
        Assert.Equal(Gen5Operand.Vector(0), instruction.Sources[0]);
        Assert.Equal(Gen5Operand.Scalar(0), instruction.Sources[1]);
        Assert.Equal(Gen5Operand.Scalar(1), instruction.Sources[2]);
        Assert.Equal(Gen5Operand.Vector(3), Assert.Single(instruction.Destinations));
    }

    [Fact]
    public void VBfeI32LowersToSignedBitFieldExtract()
    {
        var signedOpcodes = CompileAndReadSpirvOpcodes(0x149);
        var unsignedOpcodes = CompileAndReadSpirvOpcodes(0x148);

        Assert.Equal(
            unsignedOpcodes.Count(opcode => opcode == OpBitFieldSExtract) + 1,
            signedOpcodes.Count(opcode => opcode == OpBitFieldSExtract));
        Assert.Equal(
            unsignedOpcodes.Count(opcode => opcode == OpBitFieldUExtract),
            signedOpcodes.Count(opcode => opcode == OpBitFieldUExtract) + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferBoundsDoNotUseRuntimeArrayLength(bool runtimeScalars)
    {
        var instruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Smem,
            "SBufferLoadDword",
            [],
            [Gen5Operand.Scalar(0)],
            [Gen5Operand.Scalar(4)],
            new Gen5ScalarMemoryControl(1, 0, null));
        var program = new Gen5ShaderProgram(ShaderAddress, [instruction]);
        var state = new Gen5ShaderState(program, [], null);
        var scalarRegisters = new uint[256];
        var binding = new Gen5GlobalMemoryBinding(
            0,
            0x1003,
            [0],
            new byte[17],
            17,
            false);
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            [],
            [binding]);
        var initialScalarBufferIndex = runtimeScalars ? 1 : -1;
        var totalGlobalBufferCount = runtimeScalars ? 2 : 1;

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error,
                totalGlobalBufferCount,
                initialScalarBufferIndex,
                storageBufferOffsetAlignment: 256),
            error);

        var opcodes = ReadSpirvOpcodes(shader.Spirv);
        Assert.DoesNotContain(OpArrayLength, opcodes);
        Assert.Contains(OpULessThan, opcodes);
    }

    [Fact]
    public void RuntimeScalarLayoutInterleavesMetadataByBinding()
    {
        Assert.Equal(256, Gen5RuntimeScalarLayout.GetByteBiasDwordIndex(0));
        Assert.Equal(257, Gen5RuntimeScalarLayout.GetBufferDwordCountDwordIndex(0));
        Assert.Equal(258, Gen5RuntimeScalarLayout.GetByteBiasDwordIndex(1));
        Assert.Equal(259, Gen5RuntimeScalarLayout.GetBufferDwordCountDwordIndex(1));
        Assert.Equal(266, Gen5RuntimeScalarLayout.GetDwordLength(5));
    }

    private static Gen5ShaderProgram DecodeProgram(uint opcode)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[3 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, 0xD0000003u | (opcode << 16));
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[sizeof(uint)..],
            0x100u | (1u << 18)); // v0, s0, s1
        BinaryPrimitives.WriteUInt32LittleEndian(shader[(2 * sizeof(uint))..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error),
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
        Assert.Equal(0, spirv.Length % sizeof(uint));
        Assert.True(spirv.Length >= 5 * sizeof(uint));
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(spirv));

        var opcodes = new List<ushort>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
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
