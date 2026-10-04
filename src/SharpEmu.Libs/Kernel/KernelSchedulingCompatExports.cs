// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Small libKernel scheduling and synchronization compatibility exports whose
/// guest-visible behavior does not require host scheduler state.
/// </summary>
public static class KernelSchedulingCompatExports
{
    [SysAbiExport(
        Nid = "CBNtXOoef-E",
        ExportName = "sched_get_priority_max",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SchedGetPriorityMax(CpuContext ctx)
    {
        _ = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(256);
    }

    [SysAbiExport(
        Nid = "m0iS6jNsXds",
        ExportName = "sched_get_priority_min",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SchedGetPriorityMin(CpuContext ctx)
    {
        _ = unchecked((int)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(767);
    }

    [SysAbiExport(
        Nid = "uvT2iYBBnkY",
        ExportName = "sceKernelSync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSync(CpuContext ctx) => ctx.SetReturn(0);
}
