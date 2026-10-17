// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

// RDNA2 ISA: S_ADDK_I32 is "D.i = D.i + signext(SIMM16); SCC = signed overflow",
// the same SCC rule as S_ADD_I32. S_MOVK_I32 and S_MULK_I32 leave SCC untouched.
public sealed class Gen5ScalarAddImmediateTests
{
    private const ulong ShaderAddress = 0x2000;
    private const uint SEndpgm = 0xBF810000;

    // SOPK: bits 31..30 = 0b10, opcode field (bits 29..23) 0x60 + op,
    // sdst at bits 22..16, SIMM16 in the low half.
    private const uint SAddkI32S4Max = 0xB7847FFF; // s_addk_i32 s4, 0x7FFF
    private const uint SMovkI32S4Max = 0xB0047FFF; // s_movk_i32 s4, 0x7FFF
    private const uint SMulkI32S4Max = 0xB8047FFF; // s_mulk_i32 s4, 0x7FFF

    [Fact]
    public void DecodesTheScalarImmediateOpcodes()
    {
        var program = DecodeProgram(SAddkI32S4Max, SMovkI32S4Max, SMulkI32S4Max, SEndpgm);
        Assert.Equal("SAddkI32", program.Instructions[0].Opcode);
        Assert.Equal("SMovkI32", program.Instructions[1].Opcode);
        Assert.Equal("SMulkI32", program.Instructions[2].Opcode);
        Assert.Equal(4u, program.Instructions[0].Destinations[0].Value);
    }

    [Fact]
    public void AddkWritesSccWhileMovkAndMulkDoNot()
    {
        // The SCC write is SignedAddOverflow: one extra IEqual/INotEqual/LogicalAnd
        // sign-comparison triple over the same program without it. The shader
        // prologue already uses these opcodes, so compare counts rather than
        // presence.
        var withAddk = CompiledOpcodes(SAddkI32S4Max);
        var withMovk = CompiledOpcodes(SMovkI32S4Max);
        var withMulk = CompiledOpcodes(SMulkI32S4Max);
        foreach (var opcode in new[] { SpirvOp.IEqual, SpirvOp.INotEqual, SpirvOp.LogicalAnd })
        {
            Assert.Equal(Count(withMovk, opcode) + 1, Count(withAddk, opcode));
            // S_MOVK_I32 and S_MULK_I32 leave SCC alone.
            Assert.Equal(Count(withMovk, opcode), Count(withMulk, opcode));
        }
    }

    private static int Count(IReadOnlyList<ushort> opcodes, SpirvOp opcode)
    {
        var total = 0;
        foreach (var value in opcodes)
        {
            if (value == (ushort)opcode)
            {
                total++;
            }
        }

        return total;
    }

    private static IReadOnlyList<ushort> CompiledOpcodes(uint word)
    {
        var request = Request(DecodeProgram(word, SEndpgm), userDataCount: 0);
        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(request, out var compiled, out var error),
            error);
        return ReadSpirvOpcodes(compiled.Spirv);
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

    private static Gen5ShaderProgram DecodeProgram(params uint[] words)
    {
        var memory = new AddImmediateTestMemory(ShaderAddress, 0x1000);
        var shader = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(shader.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(ctx, ShaderAddress, out var program, out var error),
            error);
        return program;
    }

    private sealed class AddImmediateTestMemory(ulong baseAddress, int size) : ICpuMemory
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
            if (relative > (ulong)(size - length))
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }
}
