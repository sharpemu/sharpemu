// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ContentSearch;

// No host media library exists to search, so initialization reports success
// and the PSN content-management flow proceeds to its normal Terminate.
public static class ContentSearchExports
{
    [SysAbiExport(
        Nid = "dPj4ZtRcIWk",
        ExportName = "sceContentSearchInit",
        Target = Generation.Gen5,
        LibraryName = "libSceContentSearch")]
    public static int ContentSearchInit(CpuContext ctx) => ctx.SetReturn(0);
}
