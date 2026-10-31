// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5BufferCacheTests
{
    [Theory]
    [InlineData(0x71u, "BufferGl0Inv")]
    [InlineData(0x72u, "BufferGl1Inv")]
    public void CacheInvalidationCompilesWithoutBufferBindings(uint opcode, string expected)
    {
        uint[] words = [0xE0000000u | (opcode << 18), 0, 0xBF810000];
        var context = new CpuContext(new InstructionMemory(words), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        Assert.Equal(expected, program.Instructions[0].Opcode);
        Assert.Equal(Gen5ShaderEncoding.Mubuf, program.Instructions[0].Encoding);
        Assert.Equal(2, program.Instructions[0].Words.Count);
        Assert.Equal(8u, program.Instructions[1].Pc);
        Assert.Equal("SEndpgm", program.Instructions[1].Opcode);
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 1, ThreadCountX = 1 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out _, out var compileError), compileError);
    }

    private sealed class InstructionMemory(uint[] words) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || (address - 0x1000) % sizeof(uint) != 0 || destination.Length != sizeof(uint))
                return false;
            var index = (address - 0x1000) / sizeof(uint);
            if (index >= (ulong)words.Length) return false;
            BinaryPrimitives.WriteUInt32LittleEndian(destination, words[(int)index]);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
