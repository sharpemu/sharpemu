// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Share;

public static class SharePlayExports
{
    private static int _controllerProhibited;

    [SysAbiExport(
        Nid = "FzQS6DREDfk",
        ExportName = "sceSharePlayProhibitController",
        Target = Generation.Gen5,
        LibraryName = "libSceSharePlay")]
    public static int SharePlayProhibitController(CpuContext ctx)
    {
        // The guest ABI passes a boolean-valued integer in RDI.
        // SharpEmu does not expose a SharePlay guest controller, but retaining
        // the requested state keeps repeated enable/disable calls coherent.
        var prohibited = ctx[CpuRegister.Rdi];
        if (prohibited > 1)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Volatile.Write(ref _controllerProhibited, unchecked((int)prohibited));
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    public static void ResetRuntimeState() => Volatile.Write(ref _controllerProhibited, 0);

    internal static bool IsControllerProhibited => Volatile.Read(ref _controllerProhibited) != 0;

    internal static void ResetForTests() => ResetRuntimeState();
}
