// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private const ulong AmmSemaphoreMemoryAlignment = 0x4000;
    private const ulong AmmSemaphoreSlotSize = 32;

    private sealed record AmmSemaphoreMemory(ulong BaseAddress, ulong SizeBytes);

    private static readonly object _ammSemaphoreMemoryGate = new();
    private static ConditionalWeakTable<object, AmmSemaphoreMemory>
        _ammSemaphoreMemoryByGuest = new();

    [SysAbiExport(
        Nid = "OQTgEXyihvA",
        ExportName = "sceAgcSetAmmSemaphoreMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetAmmSemaphoreMemory(CpuContext ctx)
    {
        var baseAddress = ctx[CpuRegister.Rdi];
        var sizeBytes = ctx[CpuRegister.Rsi];
        if (baseAddress == 0 ||
            sizeBytes < AmmSemaphoreMemoryAlignment ||
            (baseAddress & (AmmSemaphoreMemoryAlignment - 1)) != 0 ||
            (sizeBytes & (AmmSemaphoreMemoryAlignment - 1)) != 0 ||
            baseAddress > ulong.MaxValue - sizeBytes)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var memoryKey = CanonicalMemory(ctx.Memory);
        lock (_ammSemaphoreMemoryGate)
        {
            if (_ammSemaphoreMemoryByGuest.TryGetValue(memoryKey, out _))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS);
            }

            if (!TryInitializeAmmSemaphoreCounters(ctx.Memory, baseAddress, sizeBytes))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            _ammSemaphoreMemoryByGuest.Add(
                memoryKey,
                new AmmSemaphoreMemory(baseAddress, sizeBytes));
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "hFQ9pUxoLQ4",
        ExportName = "sceAgcGetSemaphoreLabel",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetSemaphoreLabel(CpuContext ctx)
    {
        var slot = unchecked((uint)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        ulong labelAddress;
        lock (_ammSemaphoreMemoryGate)
        {
            if (outputAddress == 0 ||
                !_ammSemaphoreMemoryByGuest.TryGetValue(
                    CanonicalMemory(ctx.Memory),
                    out var memory) ||
                slot >= memory.SizeBytes / AmmSemaphoreSlotSize)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            labelAddress = memory.BaseAddress + (slot * AmmSemaphoreSlotSize);
        }

        return ctx.TryWriteUInt64(outputAddress, labelAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryInitializeAmmSemaphoreCounters(
        ICpuMemory memory,
        ulong baseAddress,
        ulong sizeBytes)
    {
        Span<byte> slot = stackalloc byte[(int)AmmSemaphoreSlotSize];
        for (ulong offset = 0; offset < sizeBytes; offset += AmmSemaphoreSlotSize)
        {
            if (!memory.TryRead(baseAddress + offset, slot))
            {
                return false;
            }
        }

        Span<byte> counter = stackalloc byte[sizeof(ulong)];
        counter.Clear();
        for (ulong offset = 0; offset < sizeBytes; offset += AmmSemaphoreSlotSize)
        {
            if (!memory.TryWrite(baseAddress + offset, counter))
            {
                return false;
            }
        }

        return true;
    }

    internal static void ResetAmmSemaphoreMemoryForTests()
    {
        lock (_ammSemaphoreMemoryGate)
        {
            _ammSemaphoreMemoryByGuest = new();
        }
    }
}
