// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Voice;

public static class VoiceQoSExports
{
    [SysAbiExport(
        Nid = "U8IfNl6-Css",
        ExportName = "sceVoiceQoSInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVoiceQoS")]
    public static int VoiceQoSInit(CpuContext ctx)
    {
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Trpt2QBZHCI",
        ExportName = "sceVoiceQoSGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVoiceQoS")]
    public static int VoiceQoSGetStatus(CpuContext ctx)
    {
        // Returns 0 to indicate connected state (voice available)
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "FuXenJLkk-c",
        ExportName = "sceVoiceQoSTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVoiceQoS")]
    public static int VoiceQoSTerminate(CpuContext ctx)
    {
        // No-op: cleanup is handled by emulator shutdown
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "+0lOiPZjnBI",
        ExportName = "sceVoiceQoSSetMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceVoiceQoS")]
    public static int VoiceQoSSetMode(CpuContext ctx)
    {
        // No-op: mode configuration is not emulated
        return ctx.SetReturn(0);
    }
}
