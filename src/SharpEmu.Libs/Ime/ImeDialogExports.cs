// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

public static class ImeDialogExports
{
    private const int StatusNone = 0;
    private const int StatusFinished = 2;

    private const int EndStatusOk = 0;

    private const ulong ParamMaxTextLengthOffset = 0x24;
    private const ulong ParamInputTextBufferOffset = 0x28;

    private const int ImeDialogErrorInvalidAddress = unchecked((int)0x80BC0001);

    private const string DefaultInputText = "Sharp";

    private static readonly object _gate = new();
    private static int _status = StatusNone;

    [SysAbiExport(
        Nid = "NUeBrN7hzf0",
        ExportName = "sceImeDialogInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogInit(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        if (parameterAddress == 0)
        {
            return SetReturn(ctx, ImeDialogErrorInvalidAddress);
        }

        TryWriteInputText(ctx, parameterAddress);

        lock (_gate)
        {
            _status = StatusFinished;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "IADmD4tScBY",
        ExportName = "sceImeDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetStatus(CpuContext ctx)
    {
        int status;
        lock (_gate)
        {
            status = _status;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)(long)status);
        return status;
    }

    [SysAbiExport(
        Nid = "x01jxu+vxlc",
        ExportName = "sceImeDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return SetReturn(ctx, ImeDialogErrorInvalidAddress);
        }

        Span<byte> result = stackalloc byte[8];
        result.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result, EndStatusOk);
        ctx.Memory.TryWrite(resultAddress, result);

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "oBmw4xrmfKs",
        ExportName = "sceImeDialogAbort",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogAbort(CpuContext ctx)
    {
        lock (_gate)
        {
            _status = StatusFinished;
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "gyTyVn+bXMw",
        ExportName = "sceImeDialogTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogTerm(CpuContext ctx)
    {
        lock (_gate)
        {
            _status = StatusNone;
        }

        return SetReturn(ctx, 0);
    }

    private static void TryWriteInputText(CpuContext ctx, ulong parameterAddress)
    {
        Span<byte> field = stackalloc byte[8];
        if (!ctx.Memory.TryRead(parameterAddress + ParamInputTextBufferOffset, field))
        {
            return;
        }

        var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(field);
        if (bufferAddress == 0)
        {
            return;
        }

        var maxTextLength = 16u;
        Span<byte> lengthField = stackalloc byte[4];
        if (ctx.Memory.TryRead(parameterAddress + ParamMaxTextLengthOffset, lengthField))
        {
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(lengthField);
            if (declared is > 0 and <= 256)
            {
                maxTextLength = declared;
            }
        }

        var text = Environment.GetEnvironmentVariable("SHARPEMU_DEFAULT_PROFILE");
        if (string.IsNullOrEmpty(text))
        {
            text = DefaultInputText;
        }

        if ((uint)text.Length > maxTextLength)
        {
            text = text[..(int)maxTextLength];
        }

        var encoded = Encoding.Unicode.GetBytes(text);
        Span<byte> payload = stackalloc byte[encoded.Length + 2];
        encoded.CopyTo(payload);
        payload[^2] = 0;
        payload[^1] = 0;

        if (ctx.Memory.TryWrite(bufferAddress, payload))
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] ime.dialog_autofill buf=0x{bufferAddress:X16} " +
                $"max={maxTextLength} text=\"{text}\"");
        }
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }
}
