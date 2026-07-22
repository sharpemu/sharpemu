// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    // Quake (KEX) calls this from its main loop and from the audio bring-up path with
    // an event-handler pointer. No IME session ever exists here, so report success
    // without invoking the handler ("no pending IME events"). This NID was previously
    // misbound as an sceNgs2VoiceControl alias, which fed the game NGS2 errors.
    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JvYQEIOGiVQ",
        ExportName = "sceImeInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeInit(CpuContext ctx)
    {
        // No-op: IME subsystem is initialized on-demand per session
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "nzZEbFlahtE",
        ExportName = "sceImeTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeTerm(CpuContext ctx)
    {
        // No-op: cleanup is handled by emulator shutdown
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DaYuSjna840",
        ExportName = "sceImeCreateContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeCreateContext(CpuContext ctx)
    {
        // Returns a valid handle (1). Real PS5 returns sequential context IDs.
        // Games use this to create text input sessions (chat, menu selection).
        ctx[CpuRegister.Rax] = 1;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "dZ+dmuC7VJs",
        ExportName = "sceImeOpenDialog",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeOpenDialog(CpuContext ctx)
    {
        // No-op: UI dialog not rendered. Titles that don't show IME dialogs
        // just call this to initialize their text input state machine.
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4KxQJsaxWPo",
        ExportName = "sceImeCloseDialog",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeCloseDialog(CpuContext ctx)
    {
        // No-op: no native dialog to close
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "5Ie6WfJDe6U",
        ExportName = "sceImeSetDialogParam910",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeSetDialogParam910(CpuContext ctx)
    {
        // No-op: dialog parameters (max length, initial text, filter rules)
        // are ignored since we don't render the native IME UI.
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "XpOgancJFa4",
        ExportName = "sceImeGetResultString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeGetResultString(CpuContext ctx)
    {
        // Returns success. The output buffer receives nothing (game's fallback
        // behavior: no user input provided via emulated IME).
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "rEMLgDR2cU4",
        ExportName = "sceImeGetInputString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeGetInputString(CpuContext ctx)
    {
        // Returns success with empty string. Mirrors real hardware when no text
        // has been typed yet or the dialog was dismissed without input.
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
