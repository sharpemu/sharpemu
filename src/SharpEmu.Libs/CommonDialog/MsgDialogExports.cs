// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Threading;

namespace SharpEmu.Libs.CommonDialog;

public static class MsgDialogExports
{
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusFinished = 3;
    private const int ResultSize = 0x20;

    private static int _initialized;
    private static int _status;

    [SysAbiExport(
        Nid = "lDqxaY1UbEo",
        ExportName = "sceMsgDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogInitialize(CpuContext ctx)
    {
        // Treat repeated initialization as success. The dialog service is process-global in
        // this HLE implementation and has no per-call resources to recreate.
        Interlocked.Exchange(ref _initialized, 1);
        Interlocked.Exchange(ref _status, StatusInitialized);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ePw-kqZmelo",
        ExportName = "sceMsgDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogTerminate(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 0);
        Interlocked.Exchange(ref _status, StatusNone);
        return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "b06Hh0DPEaE",
        ExportName = "sceMsgDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogOpen(CpuContext ctx)
    {
        LogDialogMessage(ctx, ctx[CpuRegister.Rdi]);

        // There is no host popup to actually show. Complete immediately with "finished" so a
        // guest polling loop (GetStatus/UpdateStatus -> GetResult -> Close) sees a dismissed
        // dialog on its very first poll instead of spinning forever waiting for user input.
        Interlocked.Exchange(ref _status, StatusFinished);
        return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Best-effort extraction of the dialog text so fatal-error popups are visible in the
    // log even though no host dialog is shown. The PS5 SceMsgDialogParam layout is not
    // fully known, so chase every qword in the struct that points at readable text - one
    // level deep, then a second level for nested sub-param structs.
    private static void LogDialogMessage(CpuContext ctx, ulong paramAddress)
    {
        if (paramAddress == 0)
        {
            Console.Error.WriteLine("[LOADER][INFO] sceMsgDialogOpen: param=NULL");
            return;
        }

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
    public static int MsgDialogGetStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "6fIC3XKt2k0",
        ExportName = "sceMsgDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogUpdateStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

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
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> result = stackalloc byte[ResultSize];
        result.Clear();
        if (!ctx.Memory.TryWrite(resultAddress, result))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "HTrcDKlFKuM",
        ExportName = "sceMsgDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMsgDialog")]
    public static int MsgDialogClose(CpuContext ctx)
    {
        Interlocked.Exchange(ref _status, StatusFinished);
        return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
