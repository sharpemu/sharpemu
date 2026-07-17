// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ScalarControlTests
{
    private const ulong ShaderAddress = 0x1_0000_8000;
    private const uint SEndpgm = 0xBF810000;

    [Fact]
    public void STrapDecodesOpcode12AndPreservesTrapId()
    {
        const uint trapId = 0x34;
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader,
            0xBF920000u | trapId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            shader[sizeof(uint)..],
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

        var instruction = Assert.Single(
            program.Instructions,
            item => item.Opcode == "STrap");
        Assert.Equal(Gen5ShaderEncoding.Sopp, instruction.Encoding);
        Assert.Equal(trapId, instruction.Words[0] & 0xFFFFu);
    }

    [Fact]
    public void ProgramLongerThan4096InstructionsReachesEndProgram()
    {
        const int nopCount = 4096;
        const uint sNop = 0xBF800000;
        var shader = new byte[(nopCount + 1) * sizeof(uint)];
        for (var index = 0; index < nopCount; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                shader.AsSpan(index * sizeof(uint), sizeof(uint)),
                sNop);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            shader.AsSpan(nopCount * sizeof(uint), sizeof(uint)),
            SEndpgm);
        var memory = new TestCpuMemory(ShaderAddress, shader.Length);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                ctx,
                ShaderAddress,
                out var program,
                out var error),
            error);
        Assert.Equal(nopCount + 1, program.Instructions.Count);
        Assert.Equal("SEndpgm", program.Instructions[^1].Opcode);
    }

    [Fact]
    public void ProgramCacheIncludesDeclaredShaderSize()
    {
        const ulong headerAddress = ShaderAddress + 0x100;
        const uint computePgmRsrc2 = 0x213;
        const uint computeUserData = 0x240;
        const uint sNop = 0xBF800000;
        var memory = new TestCpuMemory(ShaderAddress, 0x200);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(shader, sNop);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
        Assert.True(memory.TryWrite(ShaderAddress, shader));

        Span<byte> size = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)shader.Length);
        Assert.True(memory.TryWrite(headerAddress + 0x44, size));

        var ctx = new CpuContext(memory, Generation.Gen5);
        var registers = new Dictionary<uint, uint>
        {
            [computePgmRsrc2] = 0,
        };
        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                headerAddress,
                registers,
                computeUserData,
                out _,
                out var error),
            error);

        BinaryPrimitives.WriteUInt32LittleEndian(size, sizeof(uint));
        Assert.True(memory.TryWrite(headerAddress + 0x44, size));
        Assert.False(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                headerAddress,
                registers,
                computeUserData,
                out _,
                out error));
        Assert.Contains("unterminated", error);
        Assert.Contains("size=0x4", error);
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                destination.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            _storage.AsSpan((int)(address - baseAddress), destination.Length)
                .CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            if (address < baseAddress ||
                address - baseAddress > (ulong)_storage.Length ||
                source.Length > _storage.Length - (int)(address - baseAddress))
            {
                return false;
            }

            source.CopyTo(
                _storage.AsSpan((int)(address - baseAddress), source.Length));
            return true;
        }
    }
}
