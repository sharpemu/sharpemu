// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    // libIme error space.
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0031);

    // SCE_IME_KEYBOARD_MAX_NUMBER.
    private const int KeyboardMaxNumber = 5;
    // SceImeKeyboardResourceIdArray { SceUserServiceUserId userId;
    // uint32_t resourceId[SCE_IME_KEYBOARD_MAX_NUMBER]; }.
    private const int KeyboardResourceIdArraySize = 4 + (KeyboardMaxNumber * 4);
    // SceImeKeyboardInfo { SceUserServiceUserId userId; SceImeKeyboardDeviceType device;
    // SceImeKeyboardType type; uint32_t repeatDelay; uint32_t repeatRate;
    // SceImeKeyboardStatus status; int8_t reserved[12]; } — all 4-byte members.
    private const int KeyboardInfoSize = 0x24;
    // SCE_IME_KEYBOARD_DEVICE_TYPE_OSK.
    private const uint KeyboardDeviceTypeOsk = 1;
    // SCE_IME_KEYBOARD_STATE_DISCONNECTED — no physical keyboard is ever attached.
    private const uint KeyboardStateDisconnected = 0;
    // The single local user this emulator exposes (see UserServiceExports).
    private const int PrimaryUserId = 0x10000000;

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

    // ABI: int sceImeKeyboardGetResourceId(SceUserServiceUserId userId,
    // SceImeKeyboardResourceIdArray *resourceIdArray). No keyboard is ever attached,
    // so publish the caller's user id with an all-zero id array instead of leaving
    // the caller's storage uninitialized — sceImeKeyboardGetInfo is called with
    // whatever this leaves behind.
    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var arrayAddress = ctx[CpuRegister.Rsi];
        if (arrayAddress == 0)
        {
            return ctx.SetReturn(ImeErrorInvalidAddress);
        }

        Span<byte> array = stackalloc byte[KeyboardResourceIdArraySize];
        array.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(array, userId);
        return ctx.Memory.TryWrite(arrayAddress, array)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(ImeErrorInvalidAddress);
    }

    // ABI: int sceImeKeyboardGetInfo(uint32_t resourceId,
    // SceImeKeyboardInfo *info) — rdi is a plain resource id, rsi the out struct,
    // written at exactly sizeof(SceImeKeyboardInfo) == 0x24.
    [SysAbiExport(
        Nid = "VkqLPArfFdc",
        ExportName = "sceImeKeyboardGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(ImeErrorInvalidAddress);
        }

        Span<byte> info = stackalloc byte[KeyboardInfoSize];
        info.Clear();
        //   +0x00 userId       = the single local user
        //   +0x04 device       = OSK (the host has no USB/BT keyboard bridge)
        //   +0x08 type         = 0 (no layout reported for a disconnected device)
        //   +0x0C repeatDelay  = 0, +0x10 repeatRate = 0
        //   +0x14 status       = DISCONNECTED
        BinaryPrimitives.WriteInt32LittleEndian(info[0x00..], PrimaryUserId);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], KeyboardDeviceTypeOsk);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x14..], KeyboardStateDisconnected);
        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(ImeErrorInvalidAddress);
    }
}
