// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private sealed record FusedHullEntry(ulong Code, ulong Header, byte[] Instructions, byte[] SourceHeader);

    private sealed class FusedHullEntries
    {
        public readonly Dictionary<(ulong Front, ulong Back, ulong FrontCode, ulong BackCode), FusedHullEntry> Entries = new();
    }

    private static readonly ConditionalWeakTable<ICpuMemory, FusedHullEntries> _fusedHullEntries = new();

    private static bool TryGetFusedHullEntry(CpuContext context, ulong front, ulong back, ulong backCode,
        ref ulong entryCode, out ulong entryHeader)
    {
        entryHeader = front;
        if (!context.TryReadUInt32(front + ShaderSizeOffset, out var size)) return false;
        // Incomplete descriptors can be fused before they contain executable code.
        if (size == 0) return true;
        if (!IsValidDeclaredShaderSize(size)) return false;

        var memory = (ICpuMemory)CanonicalMemory(context.Memory);
        if (memory is not IGuestMemoryAllocator allocator) return false;
        var code = new byte[size];
        var header = new byte[ShaderStructBytes];
        if (!memory.TryRead(entryCode, code) || !memory.TryRead(front, header)) return false;
        var entries = _fusedHullEntries.GetValue(memory, static _ => new());
        lock (entries)
        {
            var key = (front, back, entryCode, backCode);
            if (entries.Entries.TryGetValue(key, out var cached) &&
                code.AsSpan().SequenceEqual(cached.Instructions) && header.AsSpan().SequenceEqual(cached.SourceHeader))
            {
                entryCode = cached.Code;
                entryHeader = cached.Header;
                return true;
            }

            // One LS can feed several HS programs in the same submitted frame.
            // Its binding needs a stable entry address for each pair.
            var headerOffset = ((ulong)size + 255) & ~255ul;
            if (!allocator.TryAllocateGuestMemory(headerOffset + ShaderStructBytes, 256, out var address)) return false;
            if (!memory.TryWrite(address, code) || !memory.TryWrite(address + headerOffset, header))
            {
                allocator.TryFreeGuestMemory(address);
                return false;
            }
            // The copied header retains the original code address for PC-relative data.
            entryCode = address;
            entryHeader = address + headerOffset;
            entries.Entries[key] = new(entryCode, entryHeader, code, header);
            _shaderHeadersByCode[entryCode] = entryHeader;
            return true;
        }
    }
}
