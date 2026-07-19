// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Np;

public static class NpTrophy2Exports
{
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
