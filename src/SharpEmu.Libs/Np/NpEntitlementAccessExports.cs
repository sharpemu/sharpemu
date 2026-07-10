// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static class NpEntitlementAccessExports
{
    private const int BootParamClearSize = 0x20;
    private const int EmptyAddcontInfoListSize = 0x10;

    [SysAbiExport(
        Nid = "jO8DM8oyego",
        ExportName = "sceNpEntitlementAccessInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessInitialize(CpuContext ctx)
    {
        var initParam = ctx[CpuRegister.Rdi];
        var bootParam = ctx[CpuRegister.Rsi];

        if (bootParam != 0)
        {
            Span<byte> clear = stackalloc byte[BootParamClearSize];
            clear.Clear();
            if (!ctx.Memory.TryWrite(bootParam, clear))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceNpEntitlementAccess($"initialize init=0x{initParam:X16} boot=0x{bootParam:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "TFyU+KFBv54",
        ExportName = "sceNpEntitlementAccessGetAddcontEntitlementInfoList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpEntitlementAccess")]
    public static int NpEntitlementAccessGetAddcontEntitlementInfoList(CpuContext ctx)
    {
        var listAddress = ctx[CpuRegister.Rsi];
        if (listAddress != 0)
        {
            Span<byte> emptyList = stackalloc byte[EmptyAddcontInfoListSize];
            emptyList.Clear();
            if (!ctx.Memory.TryWrite(listAddress, emptyList))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceNpEntitlementAccess(
            $"get_addcont_info_list service=0x{ctx[CpuRegister.Rdi]:X16} list=0x{listAddress:X16} " +
            $"max={ctx[CpuRegister.Rdx]} flags=0x{ctx[CpuRegister.Rcx]:X16} -> empty");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static void TraceNpEntitlementAccess(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] np.entitlement.{message}");
    }
}
