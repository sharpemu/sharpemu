// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5VopcF16Tests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    public static TheoryData<uint, string> Opcodes = new()
    {
        { 0xC8, "VCmpFF16" },
        { 0xC9, "VCmpLtF16" },
        { 0xCA, "VCmpEqF16" },
        { 0xCB, "VCmpLeF16" },
        { 0xCC, "VCmpGtF16" },
        { 0xCD, "VCmpLgF16" },
        { 0xCE, "VCmpGeF16" },
        { 0xCF, "VCmpOF16" },
        { 0xD8, "VCmpxFF16" },
        { 0xD9, "VCmpxLtF16" },
        { 0xDA, "VCmpxEqF16" },
        { 0xDB, "VCmpxLeF16" },
        { 0xDC, "VCmpxGtF16" },
        { 0xDD, "VCmpxLgF16" },
        { 0xDE, "VCmpxGeF16" },
        { 0xDF, "VCmpxOF16" },
        { 0xE8, "VCmpUF16" },
        { 0xE9, "VCmpNgeF16" },
        { 0xEA, "VCmpNlgF16" },
        { 0xEB, "VCmpNgtF16" },
        { 0xEC, "VCmpNleF16" },
        { 0xED, "VCmpNeqF16" },
        { 0xEE, "VCmpNltF16" },
        { 0xEF, "VCmpTruF16" },
        { 0xF8, "VCmpxUF16" },
        { 0xF9, "VCmpxNgeF16" },
        { 0xFA, "VCmpxNlgF16" },
        { 0xFB, "VCmpxNgtF16" },
        { 0xFC, "VCmpxNleF16" },
        { 0xFD, "VCmpxNeqF16" },
        { 0xFE, "VCmpxNltF16" },
        { 0xFF, "VCmpxTruF16" },
    };

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void F16CompareOpcodeDecodes(uint opcode, string expectedName)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[2 * sizeof(uint)];
        var word = (0x3Eu << 25) | (opcode << 17) | (1u << 9);
        BinaryPrimitives.WriteUInt32LittleEndian(shader, word);
        BinaryPrimitives.WriteUInt32LittleEndian(shader[sizeof(uint)..], SEndpgm);
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
            candidate => candidate.Encoding == Gen5ShaderEncoding.Vopc);
        Assert.Equal(expectedName, instruction.Opcode);
    }

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void F16CompareOpcodeLowersToSpirv(uint _, string opcode)
    {
        var compare = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Vopc,
            opcode,
            [0u],
            [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
            [],
            null);
        var request = ResourceTestProgram.Request(new Gen5ShaderProgram(ShaderAddress, [compare]), userDataCount: 0);

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);
        Assert.NotEmpty(shader.Spirv);
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
