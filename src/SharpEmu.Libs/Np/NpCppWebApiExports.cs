// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

// Stub for sce::Np::CppWebApi: titles abort PS5-component startup if
// Common::initialize returns a negative SCE error, so no-op success is required to boot.
public static class NpCppWebApiExports
{
    [SysAbiExport(
        Nid = "UYPxv8MIzGo",
        ExportName = "_ZN3sce2Np9CppWebApi6Common10initializeERKNS2_10InitParamsERNS2_10LibContextE",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpCppWebApi")]
    public static int CppWebApiCommonInitialize(CpuContext ctx)
    {
        // int Common::initialize(const InitParams&, LibContext&) — 0 on success.
        TraceCppWebApi("common_initialize", ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);
        return ctx.SetReturn(0);
    }

    private static void TraceCppWebApi(string operation, ulong arg0, ulong arg1)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NP"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] np_cppwebapi.{operation} arg0=0x{arg0:X16} arg1=0x{arg1:X16}");
    }
}
