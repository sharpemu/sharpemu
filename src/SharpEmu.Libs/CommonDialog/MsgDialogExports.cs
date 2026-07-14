// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.CommonDialog;

public static class MsgDialogExports
{
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;

    private const int ErrorOk = 0;
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorBusy = unchecked((int)0x80B80007);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);

    // Result buffer layout follows the common dialog convention: mode at +0x00,
    // result at +0x04, buttonId at +0x08. The affirmative button (OK/YES) is 1.
    private const int ResultSize = 0x20;
    private const int ButtonIdAffirmative = 1;

    private static int _status;

    [SysAbiExport(
        Nid = "lDqxaY1UbEo",
        ExportName = "sceMsgDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogInitialize(CpuContext ctx)
    {
        // Treat repeated initialization as success. The dialog service is process-global in
        // this HLE implementation and has no per-call resources to recreate. Only promote
        // from NONE so re-initializing mid-flow cannot clobber a running/finished dialog.
        Interlocked.CompareExchange(ref _status, StatusInitialized, StatusNone);
        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "ePw-kqZmelo",
        ExportName = "sceMsgDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _status, StatusNone) == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "b06Hh0DPEaE",
        ExportName = "sceMsgDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        var status = Volatile.Read(ref _status);
        if (status == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        if (status == StatusRunning)
        {
            return ctx.SetReturn(ErrorBusy);
        }

        LogDialogMessage(ctx, paramAddress);

        // There is no host popup to actually show. Enter RUNNING so close/cancel paths see
        // a live dialog; the guest's next status poll auto-dismisses it (see PollStatus).
        Interlocked.Exchange(ref _status, StatusRunning);
        return ctx.SetReturn(ErrorOk);
    }

    // Best-effort extraction of the dialog text so fatal-error popups are visible in the
    // log even though no host dialog is shown. The PS5 SceMsgDialogParam layout is not
    // fully known, so chase every qword in the struct that points at readable text - one
    // level deep, then a second level for nested sub-param structs.
    private static void LogDialogMessage(CpuContext ctx, ulong paramAddress)
    {
        Console.Error.WriteLine($"[LOADER][INFO] sceMsgDialogOpen: param=0x{paramAddress:X12}");

        Span<byte> head = stackalloc byte[0xA0];
        if (ctx.Memory.TryRead(paramAddress, head))
        {
            DumpPointerStrings(ctx, paramAddress, head, "param", chaseNested: true);
        }
    }

    private static void DumpPointerStrings(CpuContext ctx, ulong baseAddress, ReadOnlySpan<byte> bytes, string label, bool chaseNested)
    {
        Span<byte> nested = stackalloc byte[0x40];
        for (var offset = 0; offset + 8 <= bytes.Length; offset += 8)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
            if (value < 0x10000 || value == baseAddress)
            {
                continue;
            }

            var text = TryReadPrintableText(ctx, value);
            if (text is not null)
            {
                Console.Error.WriteLine($"[LOADER][INFO]   {label}+0x{offset:X2} -> 0x{value:X12} text=\"{text}\"");
            }
            else if (chaseNested && ctx.Memory.TryRead(value, nested))
            {
                DumpPointerStrings(ctx, value, nested, $"{label}+0x{offset:X2}", chaseNested: false);
            }
        }
    }

    private static string? TryReadPrintableText(CpuContext ctx, ulong address)
    {
        Span<byte> bytes = stackalloc byte[256];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return null;
        }

        var nullIndex = bytes.IndexOf((byte)0);
        if (nullIndex < 2)
        {
            return null;
        }

        var candidate = bytes[..nullIndex];
        foreach (var b in candidate)
        {
            if (b is < 0x20 or > 0x7E && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'\t')
            {
                return null;
            }
        }

        return System.Text.Encoding.ASCII.GetString(candidate).Replace("\n", "\\n").Replace("\r", "\\r");
    }

    [SysAbiExport(
        Nid = "CWVW78Qc3fI",
        ExportName = "sceMsgDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    [SysAbiExport(
        Nid = "6fIC3XKt2k0",
        ExportName = "sceMsgDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogUpdateStatus(CpuContext ctx) => ctx.SetReturn(PollStatus());

    // With no host UI the dialog cannot wait for user input: the first status poll after
    // Open observes the dialog as already dismissed. Advancing on both UpdateStatus and
    // GetStatus keeps every guest polling pattern free of infinite RUNNING loops, while
    // an Open -> Close sequence with no poll in between still exercises the close path.
    private static int PollStatus()
    {
        Interlocked.CompareExchange(ref _status, StatusFinished, StatusRunning);
        return Volatile.Read(ref _status);
    }

    [SysAbiExport(
        Nid = "Lr8ovHH9l6A",
        ExportName = "sceMsgDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ErrorArgNull);
        }

        if (Volatile.Read(ref _status) != StatusFinished)
        {
            return ctx.SetReturn(ErrorNotFinished);
        }

        // Report the affirmative button so yes/no prompts take the confirming branch;
        // buttonId 0 is the "invalid" sentinel and games may treat it as an error.
        Span<byte> result = stackalloc byte[ResultSize];
        result.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result[0x04..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(result[0x08..], ButtonIdAffirmative);

        if (!ctx.Memory.TryWrite(resultAddress, result))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "HTrcDKlFKuM",
        ExportName = "sceMsgDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogClose(CpuContext ctx)
    {
        if (Interlocked.CompareExchange(ref _status, StatusFinished, StatusRunning) != StatusRunning)
        {
            return ctx.SetReturn(ErrorNotRunning);
        }

        return ctx.SetReturn(ErrorOk);
    }

    [SysAbiExport(
        Nid = "wTpfglkmv34",
        ExportName = "sceMsgDialogProgressBarSetValue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarSetValue(CpuContext ctx) => ProgressBarNoOp(ctx);

    [SysAbiExport(
        Nid = "Gc5k1qcK4fs",
        ExportName = "sceMsgDialogProgressBarInc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarInc(CpuContext ctx) => ProgressBarNoOp(ctx);

    [SysAbiExport(
        Nid = "6H-71OdrpXM",
        ExportName = "sceMsgDialogProgressBarSetMsg",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogProgressBarSetMsg(CpuContext ctx) => ProgressBarNoOp(ctx);

    // There is no visible bar to update. Accept the call whenever the service is alive so
    // save/install loops that report progress do not abort on an unexpected error.
    private static int ProgressBarNoOp(CpuContext ctx)
    {
        if (Volatile.Read(ref _status) == StatusNone)
        {
            return ctx.SetReturn(ErrorNotInitialized);
        }

        return ctx.SetReturn(ErrorOk);
    }
}
