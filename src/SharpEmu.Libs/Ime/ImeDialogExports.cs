// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Overlay;
using SharpEmu.Libs.Pad;

namespace SharpEmu.Libs.Ime;

public static class ImeDialogExports
{
    private const int StatusNone = 0;
    private const int StatusRunning = 1;
    private const int StatusFinished = 2;

    private const int EndStatusOk = 0;
    private const int EndStatusUserCanceled = 1;
    private const int EndStatusAborted = 2;

    private const ulong ParamTypeOffset = 0x04;
    private const ulong ParamEnterLabelOffset = 0x10;
    private const ulong ParamOptionOffset = 0x20;
    private const ulong ParamMaxTextLengthOffset = 0x24;
    private const ulong ParamInputTextBufferOffset = 0x28;
    private const ulong ParamPlaceholderOffset = 0x40;
    private const ulong ParamTitleOffset = 0x48;

    private const uint OptionNoAutoCapitalization = 0x2;
    private const uint OptionPassword = 0x4;
    private const int MaxTextLength = 2048;
    private const int MaxLabelLength = 256;

    private const int ImeErrorBusy = unchecked((int)0x80BC0001);
    private const int ImeErrorInvalidMaxTextLength = unchecked((int)0x80BC0016);
    private const int ImeErrorInvalidInputTextBuffer = unchecked((int)0x80BC0017);
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0031);
    private const int ImeErrorDialogNotRunning = unchecked((int)0x80BC0105);
    private const int ImeErrorDialogNotFinished = unchecked((int)0x80BC0106);
    private const int ImeErrorDialogNotInUse = unchecked((int)0x80BC0107);

    private const string DefaultInputText = "Sharp";
    private const string AutofillVariable = "SHARPEMU_IME_AUTOFILL";

    private static readonly object _gate = new();
    private static int _status = StatusNone;
    private static int _endStatus = EndStatusOk;
    private static long _nextSession;
    private static ImeKeyboardSession? _session;
    private static ulong _bufferAddress;
    private static int _maxTextLength;

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
            return SetReturn(ctx, ImeErrorInvalidAddress);
        }

        lock (_gate)
        {
            if (_status != StatusNone)
            {
                return SetReturn(ctx, ImeErrorBusy);
            }

            if (!ctx.TryReadUInt32(parameterAddress + ParamMaxTextLengthOffset, out var maxTextLength))
            {
                return SetReturn(ctx, ImeErrorInvalidAddress);
            }

            if (maxTextLength is 0 or > MaxTextLength)
            {
                return SetReturn(ctx, ImeErrorInvalidMaxTextLength);
            }

            if (!ctx.TryReadUInt64(parameterAddress + ParamInputTextBufferOffset, out var bufferAddress) ||
                bufferAddress == 0)
            {
                return SetReturn(ctx, ImeErrorInvalidInputTextBuffer);
            }

            var kind = ctx.TryReadUInt32(parameterAddress + ParamTypeOffset, out var type) && type <= 4
                ? (ImeTextKind)type
                : ImeTextKind.Default;
            var enterLabel = ctx.TryReadUInt32(parameterAddress + ParamEnterLabelOffset, out var label) && label <= 3
                ? (ImeEnterLabel)label
                : ImeEnterLabel.Default;
            var option = ctx.TryReadUInt32(parameterAddress + ParamOptionOffset, out var options) ? options : 0;
            var title = ReadGuestString(ctx, parameterAddress + ParamTitleOffset, MaxLabelLength);
            var placeholder = ReadGuestString(ctx, parameterAddress + ParamPlaceholderOffset, MaxLabelLength);
            var initial = ReadUtf16(ctx, bufferAddress, (int)maxTextLength);
            if (string.IsNullOrEmpty(initial) && kind != ImeTextKind.Number)
            {
                initial = FallbackText();
            }

            _bufferAddress = bufferAddress;
            _maxTextLength = (int)maxTextLength;
            _endStatus = EndStatusOk;
            if (string.Equals(Environment.GetEnvironmentVariable(AutofillVariable), "1", StringComparison.Ordinal))
            {
                WriteText(ctx, initial);
                _status = StatusFinished;
                Console.Error.WriteLine($"[LOADER][INFO] ime_dialog.autofill text=\"{initial}\"");
                return SetReturn(ctx, 0);
            }

            _session = new ImeKeyboardSession(
                Interlocked.Increment(ref _nextSession),
                title,
                placeholder,
                initial,
                (int)maxTextLength,
                kind,
                (option & OptionPassword) != 0,
                (option & OptionNoAutoCapitalization) == 0,
                enterLabel,
                Environment.GetEnvironmentVariable(OverlayText.LanguageVariable));
            _session.Begin();
            SystemDialogInput.Capture(wantsText: true);
            ImeKeyboardLayer.Instance.Show(_session.View());
            _status = StatusRunning;
            Console.Error.WriteLine(
                $"[LOADER][INFO] ime_dialog.open type={kind} max={maxTextLength} option=0x{option:X} title=\"{title}\"");
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "IADmD4tScBY",
        ExportName = "sceImeDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceImeDialog")]
    public static int ImeDialogGetStatus(CpuContext ctx)
    {
        lock (_gate)
        {
            if (_status == StatusRunning && _session is { } session)
            {
                switch (session.Poll())
                {
                    case ImeKeyboardInput.Changed:
                        ImeKeyboardLayer.Instance.Show(session.View());
                        break;
                    case ImeKeyboardInput.Done:
                        Finish(EndStatusOk, ctx);
                        break;
                    case ImeKeyboardInput.Cancel:
                        Finish(EndStatusUserCanceled, ctx);
                        break;
                }
            }

            ctx[CpuRegister.Rax] = unchecked((ulong)(long)_status);
            return _status;
        }
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
            return SetReturn(ctx, ImeErrorInvalidAddress);
        }

        lock (_gate)
        {
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ImeErrorDialogNotInUse);
            }

            if (_status == StatusRunning)
            {
                return SetReturn(ctx, ImeErrorDialogNotFinished);
            }

            Span<byte> result = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(result, _endStatus);
            return ctx.Memory.TryWrite(resultAddress, result)
                ? SetReturn(ctx, 0)
                : SetReturn(ctx, ImeErrorInvalidAddress);
        }
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
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ImeErrorDialogNotInUse);
            }

            if (_status != StatusRunning)
            {
                return SetReturn(ctx, ImeErrorDialogNotRunning);
            }

            Finish(EndStatusAborted, ctx);
            return SetReturn(ctx, 0);
        }
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
            if (_status == StatusNone)
            {
                return SetReturn(ctx, ImeErrorDialogNotInUse);
            }

            if (_session is not null)
            {
                Close();
            }

            _status = StatusNone;
            return SetReturn(ctx, 0);
        }
    }

    private static void Finish(int endStatus, CpuContext ctx)
    {
        _endStatus = endStatus;
        if (endStatus == EndStatusOk && _session is { } session)
        {
            WriteText(ctx, session.Text);
        }

        Console.Error.WriteLine($"[LOADER][INFO] ime_dialog.finish end={endStatus}");
        Close();
        _status = StatusFinished;
    }

    private static void Close()
    {
        _session = null;
        ImeKeyboardLayer.Instance.Hide();
        SystemDialogInput.Release();
    }

    private static void WriteText(CpuContext ctx, string text)
    {
        if (text.Length > _maxTextLength)
        {
            text = text[.._maxTextLength];
        }

        var payload = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, payload);
        ctx.Memory.TryWrite(_bufferAddress, payload);
    }

    private static string FallbackText()
    {
        var text = Environment.GetEnvironmentVariable("SHARPEMU_DEFAULT_PROFILE");
        return string.IsNullOrWhiteSpace(text) ? DefaultInputText : text.Trim();
    }

    private static string ReadGuestString(CpuContext ctx, ulong pointerAddress, int maxLength) =>
        ctx.TryReadUInt64(pointerAddress, out var address) && address != 0
            ? ReadUtf16(ctx, address, maxLength)
            : string.Empty;

    private static string ReadUtf16(CpuContext ctx, ulong address, int maxLength)
    {
        var builder = new StringBuilder();
        Span<byte> unit = stackalloc byte[2];
        for (var index = 0; index < maxLength; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)(index * 2), unit))
            {
                break;
            }

            var value = BinaryPrimitives.ReadUInt16LittleEndian(unit);
            if (value == 0)
            {
                break;
            }

            builder.Append((char)value);
        }

        return builder.ToString();
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }
}
