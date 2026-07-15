// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Mouse;

public static class MouseExports
{
    [SysAbiExport(
        Nid = "Qs0wWulgl7U",
        ExportName = "sceMouseInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseInit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Returns 0 read entries: no mouse is connected. This NID was previously misbound
    // as an sceNgs2VoiceGetState alias.
    [SysAbiExport(
        Nid = "x8qnXqh-tiM",
        ExportName = "sceMouseRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseRead(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RaqxZIf6DvE",
        ExportName = "sceMouseOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseOpen(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
