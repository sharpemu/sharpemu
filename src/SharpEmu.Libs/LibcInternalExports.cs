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
    private static readonly nint _gen5CompatErrorName =
        Marshal.StringToHGlobalAnsi("SharpEmu compatibility error");

    // A Gen5 runtime cleanup hook reached from UE's video-output wrapper.  The
    // return register is propagated through the wrapper even though the caller
    // treats this operation as cleanup.  Leaving it unresolved therefore turns
    // an otherwise successful initialization into ORBIS_ERROR_ENOSYS.
    [SysAbiExport(
        Nid = "9ET3A90qn2o",
        ExportName = "Gen5RuntimeCleanupCompat",
        Target = Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int Gen5RuntimeCleanupCompat(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Gen5's error-name helper returns a borrowed C string.  UE calls strlen on
    // every non-null result, so the generic unresolved-import error value must
    // never escape here as though it were a pointer.
    [SysAbiExport(
        Nid = "fMlRbnxQJE4",
        ExportName = "Gen5ErrorNameCompat",
        Target = Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int Gen5ErrorNameCompat(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)_gen5CompatErrorName);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Optional EOS SDK logging/configuration hooks used during UE startup. The
    // title tolerates a no-op implementation, but not the generic Orbis ENOSYS
    // value (which is an invalid EOS_EResult and leaves initialization polling).
    [SysAbiExport(
        Nid = "D0odCqXaXgk",
        ExportName = "Gen5EosLoggingCallbackCompat",
        Target = Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int Gen5EosLoggingCallbackCompat(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Ji+98V2xGZA",
        ExportName = "Gen5EosLoggingLevelCompat",
        Target = Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int Gen5EosLoggingLevelCompat(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // Gen5 SDK pointer-returning compatibility query used by UE during optional
    // media/audio setup. Returning an Orbis error code from an unresolved import
    // is unsafe here: the caller treats every non-null result as a C string.
    // A null result selects UE's supported fallback path.
    [SysAbiExport(
        Nid = "QE4JD3VGhEQ",
        ExportName = "LibcInternalOptionalStringQuery",
        Target = Generation.Gen5,
        LibraryName = "LibcInternalExt")]
    public static int LibcInternalOptionalStringQuery(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "NWtTN10cJzE",
        ExportName = "LibcHeapGetTraceInfo",
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
