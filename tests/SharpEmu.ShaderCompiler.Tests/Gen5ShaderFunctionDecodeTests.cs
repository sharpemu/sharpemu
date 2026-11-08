// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ShaderFunctionDecodeTests
{
    [Fact]
    public void Return_StopsBeforeTrailingData()
    {
        var context = Context(0xBE80200E, 0);
        Assert.True(Gen5ShaderTranslator.TryDecodeFunction(context, 0x1000, 8, out var program, out var error), error);
        Assert.Equal("SSetpcB64", Assert.Single(program.Instructions).Opcode);
    }

    [Fact]
    public void ForwardBranch_CoversBothReturns()
    {
        var context = Context(0xBF840001, 0xBE80200E, 0xBE80200E, 0);
        Assert.True(Gen5ShaderTranslator.TryDecodeFunction(context, 0x1000, 16, out var program, out var error), error);
        Assert.Equal(3, program.Instructions.Count);
    }

    [Fact]
    public void MissingReturn_StopsAtByteLimit()
    {
        var context = Context(0xBF800000, 0xBE80200E);
        Assert.False(Gen5ShaderTranslator.TryDecodeFunction(context, 0x1000, 4, out _, out var error));
        Assert.Contains("unterminated", error);
    }

    private static CpuContext Context(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), words[index]);
        return new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
