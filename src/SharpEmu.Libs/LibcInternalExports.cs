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

    // C++ thread_local destructors the guest's libc registered on this thread; they run, newest first, when it exits.
    [ThreadStatic]
    private static List<(ulong Destructor, ulong Object)>? _threadAtexitDestructors;

    // The guest's libc registers a destructor for each thread_local object here: (destructor, object, DSO handle).
    [SysAbiExport(
        Nid = "qBS714-Jr3g",
        ExportName = "_sceLibcInternalThreadAtexit",
        Target = Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int LibcInternalThreadAtexit(CpuContext ctx)
    {
        var destructor = ctx[CpuRegister.Rdi];
        if (destructor != 0)
        {
            (_threadAtexitDestructors ??= []).Add((destructor, ctx[CpuRegister.Rsi]));
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Runs on the exiting guest thread before its pthread key destructors. A destructor can register another one,
    // which runs in the same pass.
    public static void RunThreadAtexitDestructors(CpuContext ctx)
    {
        var destructors = _threadAtexitDestructors;
        if (destructors is null || GuestThreadExecution.Scheduler is not { } scheduler)
        {
            return;
        }

        while (destructors.Count != 0)
        {
            var (destructor, obj) = destructors[^1];
            destructors.RemoveAt(destructors.Count - 1);
            _ = scheduler.TryCallGuestFunction(ctx, destructor, obj, 0, 0, 0, "thread_atexit_destructor", out _);
        }
    }

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
}
