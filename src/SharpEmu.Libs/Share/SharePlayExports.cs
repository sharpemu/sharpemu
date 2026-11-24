// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Share;

public static class SharePlayExports
{
    private static int _controllerProhibited;

    [SysAbiExport(
        Nid = "FzQS6DREDfk",
        ExportName = "sceSharePlayProhibitController",
        Target = Generation.Gen5,
        LibraryName = "libSceSharePlay")]
    public static int SharePlayProhibitController(CpuContext ctx)
    {
        var prohibited = ctx[CpuRegister.Rdi];
        if (prohibited > 1)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Volatile.Write(ref _controllerProhibited, unchecked((int)prohibited));
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "+MCXJlWdi+s",
        ExportName = "sceSharePlayGetCurrentConnectionInfoA",
        Target = Generation.Gen5,
        LibraryName = "libSceSharePlay")]
    public static int SharePlayGetCurrentConnectionInfoA(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // No SharePlay session is running: the 72-byte connection info (SceSharePlayConnectionInfoA)
        // reads as zeroed, i.e. status ORBIS_SHARE_PLAY_CONNECTION_STATUS_DORMANT (not connected).
        Span<byte> info = stackalloc byte[0x48];
        info.Clear();
        return ctx.Memory.TryWrite(address, info)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    public static void ResetRuntimeState() => Volatile.Write(ref _controllerProhibited, 0);

    internal static bool IsControllerProhibited => Volatile.Read(ref _controllerProhibited) != 0;

    internal static void ResetForTests() => ResetRuntimeState();
}
