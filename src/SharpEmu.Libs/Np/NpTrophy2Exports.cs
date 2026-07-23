// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpTrophy2Exports
{
    private const int TrophyDataSize = 0x20;
    private const int MaxTrophyCount = 128;
    private static int _nextContext = 1;
    private static int _nextHandle = 1;

    [SysAbiExport(
        Nid = "Bagshr7OQ6Q",
        ExportName = "sceNpTrophy2CreateContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateContext(CpuContext ctx)
    {
        return WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextContext);
    }

    [SysAbiExport(
        Nid = "Gz1rmUZpROM",
        ExportName = "sceNpTrophy2CreateHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2CreateHandle(CpuContext ctx)
    {
        return WriteIdAndReturn(ctx, ctx[CpuRegister.Rdi], ref _nextHandle);
    }

    [SysAbiExport(
        Nid = "sysY2FHYff4",
        ExportName = "sceNpTrophy2DestroyContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyContext(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "d8P11CI40KE",
        ExportName = "sceNpTrophy2DestroyHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2DestroyHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "fYapWA9xVmA",
        ExportName = "sceNpTrophy2AbortHandle",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2AbortHandle(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "bIDov3wBu5Q",
        ExportName = "sceNpTrophy2RegisterContext",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterContext(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "sUXGfNMalIo",
        ExportName = "sceNpTrophy2RegisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2RegisterUnlockCallback(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "wVqxM58sIKs",
        ExportName = "sceNpTrophy2UnregisterUnlockCallback",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2UnregisterUnlockCallback(CpuContext ctx) => ReturnOk(ctx);

    [SysAbiExport(
        Nid = "EHQEDVXZ0TI",
        ExportName = "sceNpTrophy2ShowTrophyList",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2ShowTrophyList(CpuContext ctx) => ReturnOk(ctx);

    /// <summary>
    /// Supplies the data-only form used by the Gen5 startup probe. The full
    /// details structure is intentionally left unsupported; callers receive a
    /// deterministic empty/locked-compatible record set instead of an
    /// unresolved import that leaves the output count uninitialized.
    /// </summary>
    [SysAbiExport(
        Nid = "y3zHpdZO6ME",
        ExportName = "sceNpTrophy2GetTrophyInfoArray",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfoArray(CpuContext ctx)
    {
        var stackAddress = ctx[CpuRegister.Rsp];
        if (stackAddress > ulong.MaxValue - sizeof(ulong) ||
            !ctx.TryReadUInt64(stackAddress + sizeof(ulong), out var outCountAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (outCountAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The caller reads this value even when the optional details form is
        // unavailable, so always establish a safe count before validation.
        if (!ctx.TryWriteUInt32(outCountAddress, 0))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var capacity = ctx[CpuRegister.Rcx];
        if (capacity > MaxTrophyCount)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // R8 points at the richer details structure, whose exact layout is not
        // yet modelled. Keep this path explicit rather than writing guessed data.
        if (ctx[CpuRegister.R8] != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
        }

        if (capacity == 0)
        {
            return ReturnOk(ctx);
        }

        var dataAddress = ctx[CpuRegister.R9];
        if (dataAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var count = checked((int)capacity);
        Span<byte> data = stackalloc byte[count * TrophyDataSize];
        data.Clear();
        for (var trophyId = 0; trophyId < count; trophyId++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.Slice(trophyId * TrophyDataSize, sizeof(uint)),
                (uint)trophyId);
        }

        if (!ctx.Memory.TryWrite(dataAddress, data) ||
            !ctx.TryWriteUInt32(outCountAddress, (uint)count))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ReturnOk(ctx);
    }

    /// <summary>
    /// Gen5 ABI: context, handle, trophy id, then SceNpTrophy2Details and
    /// SceNpTrophy2Data output pointers.
    /// </summary>
    /// <remarks>
    /// Reports "no such trophy" rather than succeeding. Succeeding would require
    /// filling both output structures, and their exact layouts are not confirmed
    /// here — a title that trusted zeroed details would read an empty name and a
    /// grade of zero as real data. NOT_FOUND is a documented outcome that callers
    /// must already handle, so it degrades along a path the game tests.
    /// </remarks>
    [SysAbiExport(
        Nid = "EwNylPdWUTM",
        ExportName = "sceNpTrophy2GetTrophyInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetTrophyInfo(CpuContext ctx) =>
        SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);

    private static int WriteIdAndReturn(CpuContext ctx, ulong outAddress, ref int nextId)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> idBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(idBytes, nextId);
        if (!ctx.Memory.TryWrite(outAddress, idBytes))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        nextId++;
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int ReturnOk(CpuContext ctx) => SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
