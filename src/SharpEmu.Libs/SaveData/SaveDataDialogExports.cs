// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.SaveData;

public static class SaveDataDialogExports
{
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusRunning = 2;
    private const int StatusFinished = 3;

    private const int ErrorOk = 0;
    private const int ErrorNotInitialized = unchecked((int)0x80B80003);
    private const int ErrorAlreadyInitialized = unchecked((int)0x80B80004);
    private const int ErrorNotFinished = unchecked((int)0x80B80005);
    private const int ErrorNotRunning = unchecked((int)0x80B8000B);
    private const int ErrorArgNull = unchecked((int)0x80B8000D);

    private const int ResultSize = 0x48;
    private static int _status;
    private static int _lastMode;
    private static ulong _lastUserData;

    [SysAbiExport(
        Nid = "s9e3+YpRnzw",
        ExportName = "sceSaveDataDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogInitialize(CpuContext ctx)
    {
        if (Interlocked.CompareExchange(ref _status, StatusInitialized, StatusNone) != StatusNone)
        {
            return SetReturn(ctx, ErrorAlreadyInitialized);
        }

        return SetReturn(ctx, ErrorOk);
    }

    [SysAbiExport(
        Nid = "4tPhsP6FpDI",
        ExportName = "sceSaveDataDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, ErrorArgNull);
        }

        if (_status is not (StatusInitialized or StatusFinished))
        {
            return SetReturn(ctx, ErrorNotInitialized);
        }

        _lastMode = TryReadInt32(ctx, paramAddress, out var mode) ? mode : 0;
        _lastUserData = ctx.TryReadUInt64(paramAddress + 0xC8, out var userData) ? userData : 0;

        // There is no host save dialog yet. Complete immediately with OK so
        // guest polling sees a finished dialog instead of spinning forever.
        Interlocked.Exchange(ref _status, StatusFinished);
        TraceSaveDataDialog($"open mode={_lastMode} userData=0x{_lastUserData:X16} -> finished");
        return SetReturn(ctx, ErrorOk);
    }

    [SysAbiExport(
        Nid = "ERKzksauAJA",
        ExportName = "sceSaveDataDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogGetStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "KK3Bdg1RWK0",
        ExportName = "sceSaveDataDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogUpdateStatus(CpuContext ctx) => SetReturn(ctx, Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "en7gNVnh878",
        ExportName = "sceSaveDataDialogIsReadyToDisplay",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogIsReadyToDisplay(CpuContext ctx) => SetReturn(ctx, 1);

    [SysAbiExport(
        Nid = "yEiJ-qqr6Cg",
        ExportName = "sceSaveDataDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return SetReturn(ctx, ErrorArgNull);
        }

        if (Volatile.Read(ref _status) != StatusFinished)
        {
            return SetReturn(ctx, ErrorNotFinished);
        }

        Span<byte> result = stackalloc byte[ResultSize];
        result.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result[0x00..], _lastMode);
        BinaryPrimitives.WriteInt32LittleEndian(result[0x04..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(result[0x08..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(result[0x20..], _lastUserData);

        if (!ctx.Memory.TryWrite(resultAddress, result))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, ErrorOk);
    }

    [SysAbiExport(
        Nid = "fH46Lag88XY",
        ExportName = "sceSaveDataDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogClose(CpuContext ctx)
    {
        if (Interlocked.CompareExchange(ref _status, StatusFinished, StatusRunning) != StatusRunning)
        {
            return SetReturn(ctx, ErrorNotRunning);
        }

        return SetReturn(ctx, ErrorOk);
    }

    [SysAbiExport(
        Nid = "YuH2FA7azqQ",
        ExportName = "sceSaveDataDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _status, StatusNone) == StatusNone)
        {
            return SetReturn(ctx, ErrorNotInitialized);
        }

        _lastMode = 0;
        _lastUserData = 0;
        return SetReturn(ctx, ErrorOk);
    }

    [SysAbiExport(
        Nid = "V-uEeFKARJU",
        ExportName = "sceSaveDataDialogProgressBarInc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogProgressBarInc(CpuContext ctx) => SetReturn(ctx, ErrorOk);

    [SysAbiExport(
        Nid = "hay1CfTmLyA",
        ExportName = "sceSaveDataDialogProgressBarSetValue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSaveDataDialog")]
    public static int SaveDataDialogProgressBarSetValue(CpuContext ctx) => SetReturn(ctx, ErrorOk);

    private static bool TryReadInt32(CpuContext ctx, ulong address, out int value)
    {
        value = 0;
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceSaveDataDialog(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SAVEDATA"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] save_data_dialog.{message}");
    }
}
