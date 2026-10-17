// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Remoteplay;

// SharpEmu does not implement PS5 Remote Play. Titles still probe this API
// during startup (initialize + connection-status checks while bringing up
// pad/network subsystems). Without a handler they get ORBIS_GEN2_ERROR_NOT_FOUND
// instead of a real status code. Reporting a clean "initialized, not connected"
// state lets callers take their normal no-remote-play path.
public static class RemoteplayExports
{
    private const int StatusDisconnected = 0;

    [SysAbiExport(
        Nid = "k1SwgkMSOM8",
        ExportName = "sceRemoteplayInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay")]
    public static int RemoteplayInitialize(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "g3PNjYKWqnQ",
        ExportName = "sceRemoteplayGetConnectionStatus",
        Target = Generation.Gen5,
        LibraryName = "libSceRemoteplay")]
    public static int RemoteplayGetConnectionStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress != 0)
        {
            Span<byte> status = stackalloc byte[0x10];
            status.Clear();
            status[0] = StatusDisconnected;
            if (!ctx.Memory.TryWrite(statusAddress, status))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetReturn(ctx, 0);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
