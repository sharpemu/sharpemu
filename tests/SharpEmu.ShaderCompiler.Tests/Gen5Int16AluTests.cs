// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5Int16AluTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const uint SEndpgm = 0xBF810000;

    public static TheoryData<uint, string> Opcodes = new()
    {
        { 0x303, "VAddNcU16" },
        { 0x304, "VSubNcU16" },
        { 0x307, "VLshrrevB16" },
        { 0x308, "VAshrrevI16" },
        { 0x309, "VMaxU16" },
        { 0x30A, "VMaxI16" },
        { 0x30B, "VMinU16" },
        { 0x30C, "VMinI16" },
        { 0x30D, "VAddNcI16" },
        { 0x30E, "VSubNcI16" },
        { 0x314, "VLshlrevB16" },
    };

    [Theory]
    [MemberData(nameof(Opcodes))]
    public void SixteenBitIntegerOpcodeDecodesAndCompiles(uint opcode, string expectedName)
    {
        var program = Decode(Vop3(opcode));
        Assert.Equal([expectedName, "SEndpgm"], program.Instructions.Select(instruction => instruction.Opcode));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(ResourceTestProgram.Request(program, userDataCount: 0), out var shader, out var error), error);
        Assert.NotEmpty(shader.Spirv);
    }

    private static uint[] Vop3(uint opcode) => [(0x35u << 26) | (opcode << 16) | 3u, (0x102u << 9) | 0x101u, SEndpgm];

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
