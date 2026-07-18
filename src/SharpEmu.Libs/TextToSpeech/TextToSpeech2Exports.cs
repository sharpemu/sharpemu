// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.TextToSpeech;

public static class TextToSpeech2Exports
{
    private const int SpeechStatusIdle = 0;

    private static readonly object _stateGate = new();
    private static bool _initialized;
    private static bool _opened;

    [SysAbiExport(
        Nid = "UOjiprYwVNw",
        ExportName = "sceTextToSpeech2Initialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceTextToSpeech2")]
    public static int TextToSpeech2Initialize(CpuContext ctx)
    {
        lock (_stateGate)
        {
            if (!_initialized)
            {
                _initialized = true;
                _opened = false;
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "SoWHuVW0gpU",
        ExportName = "sceTextToSpeech2Terminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceTextToSpeech2")]
    public static int TextToSpeech2Terminate(CpuContext ctx)
    {
        lock (_stateGate)
        {
            _opened = false;
            _initialized = false;
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "X0HZNbSiqyg",
        ExportName = "sceTextToSpeech2Open",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceTextToSpeech2")]
    public static int TextToSpeech2Open(CpuContext ctx)
    {
        var configurationAddress = ctx[CpuRegister.Rdi];
        if (configurationAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt64(configurationAddress, out _))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (_stateGate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            if (_opened)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            _opened = true;
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "t4e879M-cSw",
        ExportName = "sceTextToSpeech2Close",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceTextToSpeech2")]
    public static int TextToSpeech2Close(CpuContext ctx)
    {
        lock (_stateGate)
        {
            if (!_initialized || !_opened)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            _opened = false;
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "08JSg9p6bgQ",
        ExportName = "sceTextToSpeech2GetSpeechStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceTextToSpeech2")]
    public static int TextToSpeech2GetSpeechStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (_stateGate)
        {
            if (!_initialized || !_opened)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        if (!ctx.TryWriteInt32(statusAddress, SpeechStatusIdle))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "8ntsRd07EQA",
        ExportName = "sceTextToSpeech2Speak",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceTextToSpeech2")]
    public static int TextToSpeech2Speak(CpuContext ctx)
    {
        var speechParametersAddress = ctx[CpuRegister.Rdi];
        if (speechParametersAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt64(speechParametersAddress, out var textAddress))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (textAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> firstCodeUnit = stackalloc byte[sizeof(ushort)];
        if (!ctx.Memory.TryRead(textAddress, firstCodeUnit))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (_stateGate)
        {
            if (!_initialized || !_opened)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        // Until a host speech backend is available, accepted requests complete
        // synchronously and the observable service status remains idle.
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "2jiIxUmcsGo",
        ExportName = "sceTextToSpeech2Cancel",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceTextToSpeech2")]
    public static int TextToSpeech2Cancel(CpuContext ctx)
    {
        lock (_stateGate)
        {
            if (!_initialized || !_opened)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    internal static void ResetForTests()
    {
        lock (_stateGate)
        {
            _initialized = false;
            _opened = false;
        }
    }
}
