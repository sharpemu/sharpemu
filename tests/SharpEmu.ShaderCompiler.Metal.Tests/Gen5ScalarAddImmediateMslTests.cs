// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// RDNA2 ISA: S_ADDK_I32 writes SCC = signed overflow, the same rule as
// S_ADD_I32. S_MOVK_I32 and S_MULK_I32 leave SCC untouched.
public sealed class Gen5ScalarAddImmediateMslTests
{
    private const ulong ShaderAddress = 0x2000;
    private const uint SEndpgm = 0xBF810000;

    // SOPK: bits 31..30 = 0b10, opcode field (bits 29..23) 0x60 + op,
    // sdst at bits 22..16, SIMM16 in the low half.
    private const uint SAddkI32S4Max = 0xB7847FFF; // s_addk_i32 s4, 0x7FFF
    private const uint SMovkI32S4Max = 0xB0047FFF; // s_movk_i32 s4, 0x7FFF
    private const uint SMulkI32S4Max = 0xB8047FFF; // s_mulk_i32 s4, 0x7FFF

    private static string Compile(uint word)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(
            DecodeProgram(word, SEndpgm),
            ShaderStage.Compute,
            localSizeX: 1);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Source;
    }

    [Fact]
    public void AddkEmitsTheSignedOverflowSccUpdate()
    {
        var source = Compile(SAddkI32S4Max);
        Assert.Contains("scc = ((~(", source, StringComparison.Ordinal);
        Assert.Contains(">> 31) != 0u;", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SMovkI32S4Max)]
    [InlineData(SMulkI32S4Max)]
    public void MovkAndMulkLeaveSccAlone(uint word)
    {
        Assert.DoesNotContain("scc = ((~(", Compile(word), StringComparison.Ordinal);
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
