// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Diagnostics;

public static class RazorCpuExports
{
    [SysAbiExport(Nid = "zw+celG7zSI", ExportName = "sceRazorCpuPushMarker",
        Target = Generation.Gen5, LibraryName = "libSceRazorCpu")]
    public static int PushMarker(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(Nid = "YpkGsMXP3ew", ExportName = "sceRazorCpuPopMarker",
        Target = Generation.Gen5, LibraryName = "libSceRazorCpu")]
    public static int PopMarker(CpuContext ctx) => ctx.SetReturn(0);
}
