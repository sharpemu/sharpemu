// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.GameUpdate;

public static class GameUpdateExports
{
    private static int _initialized;

    [SysAbiExport(
        Nid = "YJtKLttI9fM",
        ExportName = "sceGameUpdateInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // sceGameUpdateTerminate tears down the update-check subsystem. Titles
    // call this at shutdown; the emulator has no pending update state to
    // release, so this is a no-op success.
    [SysAbiExport(
        Nid = "NSH-C-OmoNI",
        ExportName = "sceGameUpdateTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceGameUpdate")]
    public static int GameUpdateTerminate(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 0);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
