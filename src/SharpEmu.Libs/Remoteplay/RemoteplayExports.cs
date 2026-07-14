// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Remoteplay;

public static class RemoteplayExports
{
    private const int RemoteplayConnectionStatusDisconnected = 0;

    [SysAbiExport(
        Nid = "k1SwgkMSOM8",
        ExportName = "sceRemoteplayInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRemoteplay")]
    public static int RemoteplayInitialize(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "g3PNjYKWqnQ",
        ExportName = "sceRemoteplayGetConnectionStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRemoteplay")]
    public static int RemoteplayGetConnectionStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // No remote play session ever exists under emulation.
        return ctx.TryWriteInt32(statusAddress, RemoteplayConnectionStatusDisconnected)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
