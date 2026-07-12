// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.SystemGesture;

public static class SystemGestureExports
{
    [SysAbiExport(
        Nid = "3pcAvmwKCvM",
        ExportName = "sceSystemGestureInitializePrimitiveTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureInitializePrimitiveTouchRecognizer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "FWF8zkhr854",
        ExportName = "sceSystemGestureCreateTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureCreateTouchRecognizer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qpo-mEOwje0",
        ExportName = "sceSystemGestureOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureOpen(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GgFMb22sbbI",
        ExportName = "sceSystemGestureUpdatePrimitiveTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureUpdatePrimitiveTouchRecognizer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "j4h82CQWENo",
        ExportName = "sceSystemGestureUpdateTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureUpdateTouchRecognizer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "h8uongcBNVs",
        ExportName = "sceSystemGestureGetTouchEventsCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureGetTouchEventsCount(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "1MMK0W-kMgA",
        ExportName = "sceSystemGestureAppendTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureAppendTouchRecognizer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "wPJGwI2RM2I",
        ExportName = "sceSystemGestureUpdateAllTouchRecognizer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemGesture")]
    public static int SystemGestureUpdateAllTouchRecognizer(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
