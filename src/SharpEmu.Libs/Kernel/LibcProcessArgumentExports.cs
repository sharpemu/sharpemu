// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Process-argument accessors exported by libKernel for the platform libc.
/// </summary>
public static class LibcProcessArgumentExports
{
    [SysAbiExport(
        Nid = "iKJMWrAumPE",
        ExportName = "getargc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int GetArgc(CpuContext ctx) =>
        ctx.SetReturn(HleDataSymbols.ProcessArgumentCount);

    [SysAbiExport(
        Nid = "FJmglmTMdr4",
        ExportName = "getargv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int GetArgv(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = HleDataSymbols.ProcessArgumentVectorAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
