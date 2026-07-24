// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.NpGameIntent;

public static class NpGameIntentExports
{
    private static int _initialized;

    [SysAbiExport(
        Nid = "m87BHxt-H60",
        ExportName = "sceNpGameIntentInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // sceNpGameIntentTerminate tears down the game-intent transaction subsystem.
    // Titles call this during shutdown or when leaving multiplayer; the emulator
    // has no persistent intent state to release, so this is a no-op success.
    [SysAbiExport(
        Nid = "0HBYxYAjmf0",
        ExportName = "sceNpGameIntentTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentTerminate(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 0);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
