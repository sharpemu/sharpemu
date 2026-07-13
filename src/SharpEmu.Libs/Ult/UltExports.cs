// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ult;

public static class UltExports
{
    // Games initialize the ULT (user-level thread) runtime before spinning up
    // their job systems; an unresolved import here (0x80020002) makes engines
    // treat the whole task system as unavailable. The emulator schedules guest
    // pthreads natively, so accepting the initialization is sufficient.
    [SysAbiExport(
        Nid = "hZIg1EWGsHM",
        ExportName = "sceUltInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int UltInitialize(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }
}
