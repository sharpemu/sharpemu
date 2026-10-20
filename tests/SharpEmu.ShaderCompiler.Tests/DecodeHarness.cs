// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Decodes raw instruction words through the public decoder entry point.
internal static class DecodeHarness
{
    private const ulong ShaderAddress = 0x1000;

    public static Gen5ShaderProgram Decode(params uint[] words)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        var shader = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(shader.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
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

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress || virtualAddress - baseAddress + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)(virtualAddress - baseAddress);
            return true;
        }
    }
}
