// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SaveData;

namespace SharpEmu.GnmMode.Libraries.SaveData;

public static class GnmSaveDataDialogExports
{
    private static readonly object Gate = new();
    private static bool _progressRunning;

    internal static void Reset(ICpuMemory memory) => Terminate(new CpuContext(memory, Generation.Gen4));

    public static int Open(CpuContext context)
    {
        var address = context[CpuRegister.Rdi];
        uint mode = 0;
        if (address != 0 && !context.TryReadUInt32(address + 0x34, out mode))
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        lock (Gate)
        {
            var result = SaveDataDialogExports.SaveDataDialogOpen(context);
            if (result == 0)
                _progressRunning = mode == 5;
            return result;
        }
    }

    public static int GetStatus(CpuContext context)
    {
        lock (Gate)
            return _progressRunning ? context.SetReturn(2) : SaveDataDialogExports.SaveDataDialogGetStatus(context);
    }

    public static int Close(CpuContext context)
    {
        lock (Gate)
        {
            var result = SaveDataDialogExports.SaveDataDialogClose(context);
            if (result == 0)
                _progressRunning = false;
            return result;
        }
    }

    public static int Terminate(CpuContext context)
    {
        lock (Gate)
        {
            _progressRunning = false;
            return SaveDataDialogExports.SaveDataDialogTerminate(context);
        }
    }
}
