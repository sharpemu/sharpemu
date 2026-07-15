// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Stubs;

/// <summary>
/// Success stubs for trophy, character-encoding and telemetry ABI calls that
/// Void Terrarium (and other titles) invoke during startup. They were
/// previously unresolved and returned NOT_FOUND, and a title that gates its
/// UI/text initialization on these succeeding then skips ahead and never draws
/// its content (a black screen with only a clear pass). These return success
/// (and a non-zero handle where an out pointer is expected) so init proceeds.
/// </summary>
public static class GameServiceStubs
{
    private static int Ok(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    // Writes a small non-zero handle to the pointer in the given register so
    // the caller treats the object as created; returns success.
    private static int OkWithHandle(CpuContext ctx, CpuRegister outPointerRegister)
    {
        var outAddress = ctx[outPointerRegister];
        if (outAddress != 0)
        {
            Span<byte> handle = stackalloc byte[sizeof(int)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(handle, 1);
            _ = ctx.Memory.TryWrite(outAddress, handle);
        }

        return Ok(ctx);
    }

    // ---- NpTrophy2: trophy context/handle registration at boot ----
    public static int NpTrophy2CreateContext(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpTrophy2CreateHandle(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpTrophy2RegisterContext(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "4IzqhhUQ3nk", ExportName = "sceNpTrophy2GetGameInfo",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNpTrophy2")]
    public static int NpTrophy2GetGameInfo(CpuContext ctx) => Ok(ctx);

    // ---- CES: Shift-JIS <-> Unicode conversion setup (Japanese text) ----

    [SysAbiExport(Nid = "ZiDCxUUGbec", ExportName = "sceCesUcsProfileInitSJis1997Cp932",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceLibcInternal")]
    public static int CesUcsProfileInitSJis1997Cp932(CpuContext ctx) => Ok(ctx);

    [SysAbiExport(Nid = "538bRGc6Zo8", ExportName = "sceCesMbcsUcsContextInit",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceLibcInternal")]
    public static int CesMbcsUcsContextInit(CpuContext ctx) => Ok(ctx);

    // ---- NpUniversalDataSystem: gameplay telemetry events ----
    public static int NpUniversalDataSystemCreateEvent(CpuContext ctx) => OkWithHandle(ctx, CpuRegister.Rdi);
    public static int NpUniversalDataSystemPostEvent(CpuContext ctx) => Ok(ctx);
    public static int NpUniversalDataSystemDestroyEvent(CpuContext ctx) => Ok(ctx);
}
