// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Share;

public static class ShareExports
{
    private const int MaxContentParamBytes = 4096;

    private static int _initialized;
    private static string _contentParam = string.Empty;
    private static readonly object _callbackGate = new();
    private static ulong _contentEventCallback;
    private static ulong _contentEventCallbackArgument;

    [SysAbiExport(
        Nid = "nBDD66kiFW8",
        ExportName = "sceShareInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceShareUtility")]
    public static int ShareInitialize(CpuContext ctx)
    {
        var memorySize = ctx[CpuRegister.Rdi];
        var priority = unchecked((int)ctx[CpuRegister.Rsi]);
        var affinityMask = ctx[CpuRegister.Rdx];
        if (memorySize == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Interlocked.Exchange(ref _initialized, 1);

        TraceShare($"initialize memory=0x{memorySize:X} priority={priority} affinity=0x{affinityMask:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "7QZtURYnXG4",
        ExportName = "sceShareSetContentParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceShareUtility")]
    public static int ShareSetContentParam(CpuContext ctx)
    {
        var contentParamAddress = ctx[CpuRegister.Rdi];
        if (contentParamAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadNullTerminatedUtf8(ctx, contentParamAddress, MaxContentParamBytes, out var contentParam))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        _contentParam = contentParam;
        if (Volatile.Read(ref _initialized) == 0)
        {
            TraceShare("set_content_param before initialize");
        }

        TraceShare($"set_content_param len={contentParam.Length} preview='{FormatTraceString(contentParam)}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "Sygnk9dr5WQ",
        ExportName = "sceShareRegisterContentEventCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceShareUtility")]
    public static int ShareRegisterContentEventCallback(CpuContext ctx)
    {
        var callback = ctx[CpuRegister.Rdi];
        var argument = ctx[CpuRegister.Rsi];
        if (callback == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_callbackGate)
        {
            _contentEventCallback = callback;
            _contentEventCallbackArgument = argument;
        }

        TraceShare($"register_content_event_callback fn=0x{callback:X16} arg=0x{argument:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "KnsfHKmZqFA",
        ExportName = "sceShareUnregisterContentEventCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceShareUtility")]
    public static int ShareUnregisterContentEventCallback(CpuContext ctx)
    {
        var callback = ctx[CpuRegister.Rdi];
        if (callback == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_callbackGate)
        {
            if (_contentEventCallback == callback)
            {
                _contentEventCallback = 0;
                _contentEventCallbackArgument = 0;
            }
        }

        TraceShare($"unregister_content_event_callback fn=0x{callback:X16}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        Span<byte> bytes = stackalloc byte[maxLength];
        Span<byte> one = stackalloc byte[1];
        for (var index = 0; index < maxLength; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, one))
            {
                value = string.Empty;
                return false;
            }

            if (one[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes[..index]);
                return true;
            }

            bytes[index] = one[0];
        }

        value = string.Empty;
        return false;
    }

    private static string FormatTraceString(string value)
    {
        var normalized = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120 ? normalized : string.Concat(normalized.AsSpan(0, 120), "...");
    }

    private static void TraceShare(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SHARE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] share.{message}");
    }
}
