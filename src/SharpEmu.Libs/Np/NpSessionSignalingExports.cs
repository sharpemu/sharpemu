// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpSessionSignalingExports
{
    [SysAbiExport(
        Nid = "ysmw6J-P8Ak",
        ExportName = "sceNpSessionSignalingInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingInitialize(CpuContext ctx)
    {
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // sceNpSessionSignalingTerminate shuts down the PSN session-signaling
    // subsystem. The emulator does not maintain a real signaling session, so
    // this is a no-op success that lets the caller's teardown sequence complete.
    [SysAbiExport(
        Nid = "CqJuNXo5yiM",
        ExportName = "sceNpSessionSignalingTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingTerminate(CpuContext ctx)
    {
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
