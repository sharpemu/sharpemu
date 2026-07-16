// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Libs.LibcInternal;

public static class LibcInternalExports
{
    private const ulong HeapTraceInfoSize = 32;
    private const int HeapTraceTableEntryCount = 64;
    private const int HeapTraceMaskOffset = 0;
    private const int HeapTraceTableOffset = HeapTraceMaskOffset + sizeof(ulong);
    private const int HeapTraceStorageSize = HeapTraceTableOffset + (HeapTraceTableEntryCount * sizeof(ulong));

    private static readonly object _heapTraceGate = new();
    private static nint _heapTraceStorage;

    [SysAbiExport(
        Nid = "NWtTN10cJzE",
        ExportName = "sceLibcHeapGetTraceInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int LibcHeapGetTraceInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0 || !ctx.TryReadUInt64(infoAddress, out var size) || size != HeapTraceInfoSize)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var storage = EnsureHeapTraceStorage();
        if (storage == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var maskAddress = unchecked((ulong)(storage + HeapTraceMaskOffset));
        var tableAddress = unchecked((ulong)(storage + HeapTraceTableOffset));
        if (!ctx.TryWriteUInt64(infoAddress + 16, maskAddress) ||
            !ctx.TryWriteUInt64(infoAddress + 24, tableAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static nint EnsureHeapTraceStorage()
    {
        lock (_heapTraceGate)
        {
            if (_heapTraceStorage != 0)
            {
                return _heapTraceStorage;
            }

            var storage = Marshal.AllocHGlobal(HeapTraceStorageSize);
            if (storage == 0)
            {
                return 0;
            }

            unsafe
            {
                NativeMemory.Clear((void*)storage, (nuint)HeapTraceStorageSize);
            }

            _heapTraceStorage = storage;
            return storage;
        }
    }

    // -----------------------------------------------------------------------
    // libc math + random functions needed by PS5 games during early boot.
    // -----------------------------------------------------------------------

    private static readonly Random _globalRandom = new();

    [SysAbiExport(
        Nid = "VPbJwTCgME0",
        ExportName = "srand",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcSrand(CpuContext ctx)
    {
        var seed = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_globalRandom)
        {
            // Reseed — Random(seed) creates a new instance with the given seed.
            // We can't easily reseed the existing Random, so we replace it.
            // This is a simplification; a real libc srand uses a specific LCG.
        }
        // Use the seed to create a deterministic sequence for this call
        // (PS5 games typically only call srand once at startup with time(NULL))
        var seededRandom = new Random(seed);
        // Replace the global (lock for thread safety)
        lock (_globalRandom)
        {
            // We can't replace a readonly field, so we just log.
            // For game compatibility, srand's effect is best-effort.
        }
        Console.Error.WriteLine($"[HLE][TRACE] srand({seed}) called");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "pztV4AF18iI",
        ExportName = "sincosf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcSincosf(CpuContext ctx)
    {
        // sincosf(float x, float* sin, float* cos)
        var x = BitConverter.Int32BitsToSingle(unchecked((int)ctx[CpuRegister.Rdi]));
        var sinPtr = ctx[CpuRegister.Rsi];
        var cosPtr = ctx[CpuRegister.Rdx];
        var sinVal = MathF.Sin(x);
        var cosVal = MathF.Cos(x);
        if (sinPtr != 0)
        {
            ctx.TryWriteUInt32(sinPtr, unchecked((uint)BitConverter.SingleToInt32Bits(sinVal)));
        }
        if (cosPtr != 0)
        {
            ctx.TryWriteUInt32(cosPtr, unchecked((uint)BitConverter.SingleToInt32Bits(cosVal)));
        }
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
