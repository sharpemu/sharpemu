// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal sealed record ShaderFunctionReadResult(Gen5ShaderProgram Program, byte[] Bytes, string Error)
{
    public bool Complete => Error.Length == 0;
}

internal sealed class ShaderFunctionReadException(ulong address, ShaderFunctionReadResult result)
    : InvalidOperationException($"The shader function cannot be decoded: address=0x{address:X16} error={result.Error}.")
{
    public ulong Address { get; } = address;
    public ShaderFunctionReadResult Result { get; } = result;
}

internal static class ShaderFunctionReader
{
    public static ShaderFunctionReadResult Read(ulong address, GuestWordReader read)
    {
        var memory = new FunctionMemory(address, read);
        var context = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderTranslator.TryDecodeFunction(context, address, out var program, out var error);
        return new(program, memory.CapturedBytes(), error);
    }

    private sealed class FunctionMemory(ulong start, GuestWordReader read) : ICpuMemory
    {
        private readonly Dictionary<ulong, uint> _words = [];

        public bool TryRead(ulong address, Span<byte> destination)
        {
            const ulong addressEnd = 1ul << 48;
            if (address < start || address >= addressEnd || (address & 3) != 0 ||
                (destination.Length & 3) != 0 || (ulong)destination.Length > addressEnd - address) return false;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                var current = address + (ulong)offset;
                if (!_words.TryGetValue(current, out var word))
                {
                    if (!read(current, out word)) return false;
                    _words.Add(current, word);
                }
                BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), word);
            }
            return true;
        }

        public byte[] CapturedBytes()
        {
            var bytes = new byte[_words.Count * 4];
            var length = 0;
            while (length < bytes.Length && _words.TryGetValue(start + (ulong)length, out var word))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(length, 4), word);
                length += 4;
            }
            return bytes.AsSpan(0, length).ToArray();
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
