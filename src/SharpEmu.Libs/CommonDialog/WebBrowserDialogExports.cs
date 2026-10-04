// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CommonDialog;

/// <summary>
/// Headless lifecycle for the system web-browser dialog. SharpEmu does not yet
/// provide a host browser, so an opened dialog completes on its first status
/// poll instead of leaving games in an infinite RUNNING loop.
/// </summary>
public static class WebBrowserDialogExports
{
    internal const int StatusNone = 0;
    internal const int StatusInitialized = 1;
    internal const int StatusRunning = 2;
    internal const int StatusFinished = 3;

    private const int ErrorOk = 0;
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);

    private static int _status;

    [SysAbiExport(
        Nid = "jqb7HntFQFc",
        ExportName = "sceWebBrowserDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogInitialize(CpuContext ctx)
    {
        // Preserve the previous success-stub's idempotence. Several games initialize
        // optional dialogs defensively and do not benefit from ALREADY_INITIALIZED.
        Interlocked.CompareExchange(ref _status, StatusInitialized, StatusNone);
        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "FraP7debcdg",
        ExportName = "sceWebBrowserDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogOpen(CpuContext ctx) => Open(ctx);

    [SysAbiExport(
        Nid = "O7dIZQrwVFY",
        ExportName = "sceWebBrowserDialogOpenForPredeterminedContent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogOpenForPredeterminedContent(CpuContext ctx) => Open(ctx);

    private static int Open(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        if (Volatile.Read(ref _status) == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        Interlocked.Exchange(ref _status, StatusRunning);
        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "CFTG6a8TjOU",
        ExportName = "sceWebBrowserDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogGetStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    [SysAbiExport(
        Nid = "h1dR-t5ISgg",
        ExportName = "sceWebBrowserDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    private static int PollStatus()
    {
        Interlocked.CompareExchange(ref _status, StatusFinished, StatusRunning);
        return Volatile.Read(ref _status);
    }

    [SysAbiExport(
        Nid = "PSK+Eik919Q",
        ExportName = "sceWebBrowserDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogClose(CpuContext ctx)
    {
        if (Interlocked.CompareExchange(ref _status, StatusFinished, StatusRunning) != StatusRunning)
        {
            return ctx.SetReturn(ErrorNotRunning);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "vCaW0fgVQmc",
        ExportName = "sceWebBrowserDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogGetResult(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        if (Volatile.Read(ref _status) != StatusFinished)
        {
            return ctx.SetReturn(ErrorNotFinished);
        }

        // The result begins with the common-dialog result value. Report success and
        // initialize only that documented scalar rather than guessing a structure size.
        Span<byte> result = stackalloc byte[sizeof(int)];
        result.Clear();
        if (!ctx.Memory.TryWrite(ctx[CpuRegister.Rdi], result))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "Wit4LjeoeX4",
        ExportName = "sceWebBrowserDialogGetEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogGetEvent(CpuContext ctx) => ctx.SetReturn(ErrorOk);

    [SysAbiExport(
        Nid = "uYELOMVnmNQ",
        ExportName = "sceWebBrowserDialogNavigate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogNavigate(CpuContext ctx) => ctx.SetReturn(ErrorOk);

    [SysAbiExport(
        Nid = "RLhKBOoNyXY",
        ExportName = "sceWebBrowserDialogSetZoom",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogSetZoom(CpuContext ctx) => ctx.SetReturn(ErrorOk);

    [SysAbiExport(
        Nid = "ocHtyBwHfys",
        ExportName = "sceWebBrowserDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceWebBrowserDialog")]
    public static int WebBrowserDialogTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _status, StatusNone) == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        return ctx.SetReturn(ErrorOk);
    }

    internal static int StatusForTests => Volatile.Read(ref _status);

    internal static void ResetForTests() => Interlocked.Exchange(ref _status, StatusNone);
}
