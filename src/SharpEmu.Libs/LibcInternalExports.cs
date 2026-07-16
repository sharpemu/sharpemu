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

    // _Xtime_get_ticks: returns high-resolution tick count (like QueryPerformanceCounter)
    // Used by C runtime timing functions. Returns ticks since boot.
    [SysAbiExport(
        Nid = "Cj+Fw5q1tUo",
        ExportName = "_Xtime_get_ticks",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int XtimeGetTicks(CpuContext ctx)
    {
        // Use high-resolution stopwatch ticks
        var ticks = (ulong)System.Diagnostics.Stopwatch.GetTimestamp();
        ctx[CpuRegister.Rax] = ticks;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // logf: natural logarithm (float)
    [SysAbiExport(
        Nid = "RQXLbdT2lc4",
        ExportName = "logf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcLogf(CpuContext ctx)
    {
        var x = BitConverter.Int32BitsToSingle(unchecked((int)ctx[CpuRegister.Rdi]));
        ctx[CpuRegister.Rax] = unchecked((ulong)BitConverter.SingleToInt32Bits(MathF.Log(x)));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // rand: pseudo-random integer
    private static int _randSeed = 1;
    [SysAbiExport(
        Nid = "cpCOXWMgha0",
        ExportName = "rand",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcRand(CpuContext ctx)
    {
        // Simple LCG (same as glibc rand)
        _randSeed = unchecked(_randSeed * 1103515245 + 12345);
        ctx[CpuRegister.Rax] = (uint)(_randSeed >> 16) & 0x7FFF;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // sprintf_s: safe sprintf (writes formatted string to buffer)
    // This is a simplified implementation that handles common format specifiers.
    [SysAbiExport(
        Nid = "xEszJVGpybs",
        ExportName = "sprintf_s",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcSprintfS(CpuContext ctx)
    {
        // args: rdi=buffer, rsi=size, rdx=format, rcx=first_arg...
        var buffer = ctx[CpuRegister.Rdi];
        var size = ctx[CpuRegister.Rsi];
        var format = ctx[CpuRegister.Rdx];

        if (buffer == 0 || size == 0 || format == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // Read format string
        var formatBytes = new byte[256];
        var formatSpan = new Span<byte>(formatBytes);
        if (!ctx.Memory.TryRead(format, formatSpan))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var nullPos = Array.IndexOf(formatBytes, (byte)0);
        if (nullPos < 0) nullPos = 256;
        var formatStr = System.Text.Encoding.ASCII.GetString(formatBytes, 0, nullPos);

        // For now, just write the format string literally (no arg substitution)
        // This handles simple cases like sprintf_s(buf, size, "hello")
        var bytes = System.Text.Encoding.ASCII.GetBytes(formatStr);
        var writeLen = Math.Min(bytes.Length, (int)size - 1);
        if (writeLen > 0)
        {
            ctx.Memory.TryWrite(buffer, bytes.AsSpan(0, writeLen));
            ctx.Memory.TryWrite(buffer + (ulong)writeLen, new byte[] { 0 }); // null terminator
        }
        ctx[CpuRegister.Rax] = (ulong)writeLen;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // _Atomic_fetch_add_4: atomic fetch-and-add for 32-bit integers
    // Returns the OLD value, then adds.
    [SysAbiExport(
        Nid = "iPBqs+YUUFw",
        ExportName = "_Atomic_fetch_add_4",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int AtomicFetchAdd4(CpuContext ctx)
    {
        // rdi = ptr to 32-bit atomic, rsi = value to add
        var ptr = ctx[CpuRegister.Rdi];
        var addend = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ptr == 0 || !ctx.TryReadUInt32(ptr, out var oldVal))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        var newVal = unchecked(oldVal + addend);
        ctx.TryWriteUInt32(ptr, unchecked((uint)newVal));
        ctx[CpuRegister.Rax] = oldVal; // return old value
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // _Atomic_fetch_sub_4: atomic fetch-and-subtract for 32-bit integers
    [SysAbiExport(
        Nid = "2HnmKiLmV6s",
        ExportName = "_Atomic_fetch_sub_4",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int AtomicFetchSub4(CpuContext ctx)
    {
        var ptr = ctx[CpuRegister.Rdi];
        var subtrahend = unchecked((int)ctx[CpuRegister.Rsi]);
        if (ptr == 0 || !ctx.TryReadUInt32(ptr, out var oldVal))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        var newVal = unchecked(oldVal - subtrahend);
        ctx.TryWriteUInt32(ptr, unchecked((uint)newVal));
        ctx[CpuRegister.Rax] = oldVal;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // std::Pad constructor (_ZNSt4_PadC2Ev)
    // std::Pad is a C++ threading utility for launching detached threads.
    // Stub: just zero-initialize the object (16 bytes typically).
    [SysAbiExport(
        Nid = "dGYo9mE8K2A",
        ExportName = "_ZNSt4_PadC2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int StdPadCtor(CpuContext ctx)
    {
        var thisPtr = ctx[CpuRegister.Rdi];
        if (thisPtr != 0)
        {
            // Zero-initialize the Pad object (typically 8-16 bytes)
            ctx.Memory.TryWrite(thisPtr, new byte[16]);
        }
        ctx[CpuRegister.Rax] = thisPtr;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // std::Pad::_Launch (_ZNSt4_Pad7_LaunchEPP7pthread)
    // Launches a detached thread. Stub: do nothing (the callback is in args).
    [SysAbiExport(
        Nid = "xZqiZvmcp9k",
        ExportName = "_ZNSt4_Pad7_LaunchEPP7pthread",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int StdPadLaunch(CpuContext ctx)
    {
        // rdi = this, rsi = pthread**, rdx = callback
        // For now, just return OK without actually launching the thread.
        // This is a stub — the game may hang if it waits for the thread.
        Console.Error.WriteLine("[HLE][TRACE] std::Pad::_Launch called (stubbed — no thread launched)");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // std::Pad destructor (_ZNSt4_PadD2Ev)
    [SysAbiExport(
        Nid = "gjLRZgfb3i0",
        ExportName = "_ZNSt4_PadD2Ev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int StdPadDtor(CpuContext ctx)
    {
        // No-op for the stub
        var thisPtr = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = thisPtr;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // cosf — cosine (float)
    [SysAbiExport(
        Nid = "-P6FNMzk2Kc",
        ExportName = "cosf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcCosf(CpuContext ctx)
    {
        var x = BitConverter.Int32BitsToSingle(unchecked((int)ctx[CpuRegister.Rdi]));
        ctx[CpuRegister.Rax] = unchecked((ulong)BitConverter.SingleToInt32Bits(MathF.Cos(x)));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // setenv — set environment variable
    [SysAbiExport(
        Nid = "M4YYbSFfJ8g",
        ExportName = "setenv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcSetenv(CpuContext ctx)
    {
        // rdi=name, rsi=value, rdx=overwrite
        // Stub: just return success. We don't manage a real environment.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // puts — write string to stdout
    [SysAbiExport(
        Nid = "YQ0navp+YIc",
        ExportName = "puts",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcPuts(CpuContext ctx)
    {
        var strAddr = ctx[CpuRegister.Rdi];
        if (strAddr == 0) return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        var buf = new byte[512];
        if (ctx.Memory.TryRead(strAddr, buf))
        {
            var nullPos = Array.IndexOf(buf, (byte)0);
            if (nullPos >= 0)
            {
                var s = System.Text.Encoding.ASCII.GetString(buf, 0, nullPos);
                Console.Error.WriteLine($"[HLE][puts] {s}");
            }
        }
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // malloc_stats_fast — fast malloc statistics (stub)
    [SysAbiExport(
        Nid = "KuOuD58hqn4",
        ExportName = "malloc_stats_fast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int LibcMallocStatsFast(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // scePadDeviceClassGetExtendedInformation — pad device info (stub)
    [SysAbiExport(
        Nid = "AcslpN1jHR8",
        ExportName = "scePadDeviceClassGetExtendedInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int ScePadDeviceClassGetExtendedInfo(CpuContext ctx)
    {
        // Stub: return 0 (success) but don't write anything
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // il2cpp_api_register_symbol — IL2CPP runtime symbol registration (stub)
    [SysAbiExport(
        Nid = "cJ2Y4E-t258",
        ExportName = "il2cpp_api_register_symbol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libil2cpp")]
    public static int Il2cppRegisterSymbol(CpuContext ctx)
    {
        // Stub: just return success
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // unity_mono_set_user_malloc_mutex — Unity mono runtime (stub)
    [SysAbiExport(
        Nid = "-pnj3-7a6QA",
        ExportName = "unity_mono_set_user_malloc_mutex",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libunity")]
    public static int UnityMonoSetMallocMutex(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // SetDataFolder — Unity data folder setup (stub)
    [SysAbiExport(
        Nid = "35NoyMOtYpE",
        ExportName = "SetDataFolder",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libunity")]
    public static int UnitySetDataFolder(CpuContext ctx)
    {
        var pathAddr = ctx[CpuRegister.Rdi];
        if (pathAddr != 0)
        {
            var buf = new byte[256];
            if (ctx.Memory.TryRead(pathAddr, buf))
            {
                var nullPos = Array.IndexOf(buf, (byte)0);
                if (nullPos >= 0)
                {
                    var path = System.Text.Encoding.ASCII.GetString(buf, 0, nullPos);
                    Console.Error.WriteLine($"[HLE][Unity] SetDataFolder: {path}");
                }
            }
        }
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
