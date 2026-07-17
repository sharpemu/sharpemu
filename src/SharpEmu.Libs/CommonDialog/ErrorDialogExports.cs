// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Threading;

namespace SharpEmu.Libs.CommonDialog;

/// <summary>
/// libSceErrorDialog. There is no host error dialog, so an opened dialog
/// immediately reports finished — this keeps guests that block on the dialog
/// status (a common fatal-error path) from spinning forever.
/// </summary>
public static class ErrorDialogExports
{
    private const int AlreadyInitialized = unchecked((int)0x80ED0001);
    private const int NotInitialized = unchecked((int)0x80ED0002);
    private const int ArgNull = unchecked((int)0x80ED0005);

    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusFinished = 3;

    private static int _initialized;
    private static int _status;

    [SysAbiExport(
        Nid = "I88KChlynSs",
        ExportName = "sceErrorDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogInitialize(CpuContext ctx)
    {
        var result = Interlocked.Exchange(ref _initialized, 1) == 0 ? 0 : AlreadyInitialized;
        if (result == 0)
        {
            Volatile.Write(ref _status, StatusInitialized);
        }

        return SetReturn(ctx, result);
    }

    [SysAbiExport(
        Nid = "M2ZF-ClLhgY",
        ExportName = "sceErrorDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogOpen(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return SetReturn(ctx, ArgNull);
        }

        if (Volatile.Read(ref _initialized) == 0)
        {
            return SetReturn(ctx, NotInitialized);
        }

        Volatile.Write(ref _status, StatusFinished);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "t2FvHRXzgqk",
        ExportName = "sceErrorDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogGetStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "WWiGuh9XfgQ",
        ExportName = "sceErrorDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogUpdateStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "ekXHb1kDBl0",
        ExportName = "sceErrorDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogClose(CpuContext ctx)
    {
        Volatile.Write(ref _status, StatusFinished);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "9XAxK2PMwk8",
        ExportName = "sceErrorDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceErrorDialog")]
    public static int ErrorDialogTerminate(CpuContext ctx)
    {
        Volatile.Write(ref _status, StatusNone);
        Interlocked.Exchange(ref _initialized, 0);
        return SetReturn(ctx, 0);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
