// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.GnmMode.Shaders;

public sealed partial class GcnShaderFrontend
{
    private const int MaxCachedFetchShaders = 4096;
    private sealed record FetchShaderEntry(byte[] Code, Gen5ShaderProgram Program);
    private readonly ConcurrentDictionary<ulong, FetchShaderEntry> _fetchShaders = new();

    private bool TryDecodeFetch(ICpuMemory memory, ulong address, out Gen5ShaderProgram program,
        out uint size, out string error)
    {
        if (_fetchShaders.TryGetValue(address, out var cached) && FetchCodeMatches(memory, address, cached.Code))
        {
            program = cached.Program;
            size = (uint)cached.Code.Length;
            error = string.Empty;
            return true;
        }

        if (!GcnShaderDecoder.TryDecodeFunction(memory, address, out program, out size, out error))
        {
            _fetchShaders.TryRemove(address, out _);
            return false;
        }

        // Retain the decoded bytes, so a concurrent code write cannot validate stale IR.
        var code = new byte[size];
        foreach (var instruction in program.Instructions)
            for (var index = 0; index < instruction.Words.Count; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan((int)instruction.Pc + index * 4), instruction.Words[index]);
        if (_fetchShaders.Count < MaxCachedFetchShaders || _fetchShaders.ContainsKey(address))
            _fetchShaders[address] = new FetchShaderEntry(code, program);
        return true;
    }

    private static bool FetchCodeMatches(ICpuMemory memory, ulong address, byte[] code)
    {
        // TryRead uses the backing view's fast path; TryCompare takes the VM's global lock.
        byte[]? rented = null;
        Span<byte> current = code.Length <= 512 ? stackalloc byte[code.Length]
            : (rented = ArrayPool<byte>.Shared.Rent(code.Length)).AsSpan(0, code.Length);
        try
        {
            return memory.TryRead(address, current) && current.SequenceEqual(code);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
