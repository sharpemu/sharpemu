// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.GnmMode.Libraries.VideoOut;

namespace SharpEmu.GnmMode.Gpu;

internal static class GnmCommandBufferCopies
{
    private const ulong RingBytes = 64UL << 20;
    private static readonly object Gate = new();
    private static ICpuMemory? _memory;
    private static ulong _ring;
    private static ulong _offset;
    private static bool _unavailable;

    internal static void Reset(ICpuMemory memory)
    {
        lock (Gate)
        {
            _memory = memory;
            _ring = 0;
            _offset = 0;
            _unavailable = false;
        }
    }

    internal static ulong Copy(ReadOnlySpan<uint> commands)
    {
        var bytes = (ulong)commands.Length * sizeof(uint);
        lock (Gate)
        {
            if (_memory is not { } memory || _unavailable || bytes == 0 || bytes > RingBytes / 4)
                return 0;
            if (_ring == 0)
            {
                if (!GnmVideoOutExports.TryAllocateTrackedShadow(new CpuContext(memory, Generation.Gen4), RingBytes, out _ring))
                {
                    _ring = 0;
                    _unavailable = true;
                    Console.Error.WriteLine("[GNMMODE][ERROR] GNM command buffer copies are unavailable; submissions use guest memory directly.");
                    return 0;
                }
                if (GnmDiagnostics.Log)
                    Console.Error.WriteLine($"[GNMMODE][TRACE] GNM command buffer copies use 0x{_ring:X16}+0x{RingBytes:X}.");
            }
            if (_offset + bytes > RingBytes)
                _offset = 0;
            var address = _ring + _offset;
            if (!memory.TryWrite(address, MemoryMarshal.AsBytes(commands)))
                return 0;
            _offset = (_offset + bytes + 255) & ~255UL;
            return address;
        }
    }
}
