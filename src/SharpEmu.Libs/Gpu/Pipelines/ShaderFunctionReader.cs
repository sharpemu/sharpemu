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

    // Every run of contiguous words the decode read, the first one at the function's address.
    public (ulong Address, byte[] Bytes)[] Runs { get; init; } = [];
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
        return new(program, memory.CapturedBytes(), error) { Runs = memory.CapturedRuns() };
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

        public (ulong Address, byte[] Bytes)[] CapturedRuns()
        {
            var runs = new List<(ulong, byte[])>();
            var addresses = _words.Keys.Order().ToArray();
            for (var first = 0; first < addresses.Length;)
            {
                var last = first;
                while (last + 1 < addresses.Length && addresses[last + 1] == addresses[last] + 4) last++;
                var bytes = new byte[(last - first + 1) * 4];
                for (var index = first; index <= last; index++)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((index - first) * 4, 4), _words[addresses[index]]);
                runs.Add((addresses[first], bytes));
                first = last + 1;
            }
            return [.. runs];
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

// Decoded shader functions by address. A call table names the same functions on every dispatch, so a function is decoded
// again only when a word its decode read has changed: each lookup compares those words with the guest's current ones.
internal sealed class ShaderFunctionCache(ResidentGuestBytesReader? residentReader)
{
    private const int Capacity = 4096;
    private readonly Dictionary<ulong, ShaderFunctionReadResult> _functions = [];
    private byte[] _scratch = [];

    public ShaderFunctionReadResult Read(ulong address, GuestWordReader read)
    {
        if (_functions.TryGetValue(address, out var cached) && Unchanged(cached, read))
            return cached;
        var result = ShaderFunctionReader.Read(address, read);
        if (!result.Complete)
        {
            _functions.Remove(address);
            return result;
        }

        if (_functions.Count >= Capacity && !_functions.ContainsKey(address)) _functions.Clear();
        _functions[address] = result;
        return result;
    }

    // Resident bytes compare in bulk; a range the host cannot copy without synchronizing is read a word at a time, as the
    // decode read it.
    private bool Unchanged(ShaderFunctionReadResult cached, GuestWordReader read)
    {
        foreach (var (start, bytes) in cached.Runs)
        {
            if (_scratch.Length < bytes.Length) _scratch = new byte[bytes.Length];
            var current = _scratch.AsSpan(0, bytes.Length);
            if (residentReader is not null && residentReader(start, current, clean: false))
            {
                if (!current.SequenceEqual(bytes)) return false;
                continue;
            }

            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                if (!read(start + (ulong)offset, out var word) ||
                    word != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)))
                    return false;
            }
        }

        return cached.Runs.Length != 0;
    }
}
