// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

// SceNetMemoryPoolStats, SceSslMemoryPoolStats and SceHttp2MemoryPoolStats share one 32-byte layout: the pool
// size, the peak and current bytes in use, a reserved word and padding. These libraries take nothing from the
// guest's pool, so both in-use sizes are zero.
internal static class MemoryPoolStats
{
    public const int Size = 32;

    public static bool TryWrite(CpuContext ctx, ulong address, ulong poolSize)
    {
        Span<byte> stats = stackalloc byte[Size];
        BinaryPrimitives.WriteUInt64LittleEndian(stats, poolSize);
        return ctx.Memory.TryWrite(address, stats);
    }
}
