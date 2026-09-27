// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpSessionSignalingExports
{
    private static int _nextContextId;

    [SysAbiExport(
        Nid = "ysmw6J-P8Ak",
        ExportName = "sceNpSessionSignalingInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingInitialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "aBuX0PX-T7I",
        ExportName = "sceNpSessionSignalingCreateContext2",
        Target = Generation.Gen5,
        LibraryName = "libSceNpSessionSignaling")]
    public static int NpSessionSignalingCreateContext2(CpuContext ctx)
    {
        var contextId = Interlocked.Increment(ref _nextContextId);
        ctx[CpuRegister.Rax] = unchecked((ulong)contextId);
        return contextId;
    }
}
