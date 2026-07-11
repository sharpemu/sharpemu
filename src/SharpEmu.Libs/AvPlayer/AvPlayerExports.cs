// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.AvPlayer;

public static class AvPlayerExports
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private static readonly object StateGate = new();
    private static readonly HashSet<ulong> Players = new();

    [SysAbiExport(
        Nid = "aS66RI0gGgo",
        ExportName = "sceAvPlayerInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInit(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        lock (StateGate)
        {
            Players.Add(handle);
        }

        ctx[CpuRegister.Rax] = handle;
        return unchecked((int)handle);
    }

    [SysAbiExport(
        Nid = "JdksQu8pNdQ",
        ExportName = "sceAvPlayerGetVideoDataEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoDataEx(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // "UbQoYawOsfY" is sceAvPlayerIsActive, not sceAvPlayerGetVideoDataEx (the previous NID here
    // was wrong - verified by hashing both names against scripts/ps5_names.txt). No player is
    // ever actually active in this HLE implementation, so report false/inactive.
    [SysAbiExport(
        Nid = "UbQoYawOsfY",
        ExportName = "sceAvPlayerIsActive",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerIsActive(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Wnp1OVcrZgk",
        ExportName = "sceAvPlayerGetAudioData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetAudioData(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "HD1YKVU26-M",
        ExportName = "sceAvPlayerPostInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPostInit(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            return ctx.SetReturn(
                handle != 0 && dataAddress != 0 && Players.Contains(handle)
                    ? 0
                    : InvalidParameters);
        }
    }

    [SysAbiExport(
        Nid = "NkJwDzKmIlw",
        ExportName = "sceAvPlayerClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerClose(CpuContext ctx)
    {
        lock (StateGate)
        {
            return ctx.SetReturn(Players.Remove(ctx[CpuRegister.Rdi]) ? 0 : InvalidParameters);
        }
    }
}
