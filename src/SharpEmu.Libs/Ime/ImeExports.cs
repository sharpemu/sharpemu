// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    // libIme error space.
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0031);
    private const int ImeErrorNotOpened = unchecked((int)0x80BC0002);
    private const int ImeErrorInvalidUserId = unchecked((int)0x80BC0010);
    private const int ImeErrorNoResourceId = unchecked((int)0x80BC0023);

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
    private static int _keyboardOpen;

    public static void ResetRuntimeState() =>
        Interlocked.Exchange(ref _keyboardOpen, 0);

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
        Interlocked.Exchange(ref _keyboardOpen, 1);
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

    [SysAbiExport(
        Nid = "PMVehSlfZ94",
        ExportName = "sceImeKeyboardClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardClose(CpuContext ctx)
    {
        if (Volatile.Read(ref _keyboardOpen) == 0)
        {
            return ctx.SetReturn(ImeErrorNotOpened);
        }

        if (unchecked((int)ctx[CpuRegister.Rdi]) == -1)
        {
            return ctx.SetReturn(ImeErrorInvalidUserId);
        }

        Interlocked.Exchange(ref _keyboardOpen, 0);
        return ctx.SetReturn(0);
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
        BinaryPrimitives.WriteInt32LittleEndian(info[0x00..], PrimaryUserId);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], KeyboardDeviceTypeOsk);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x14..], KeyboardStateDisconnected);
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn(ImeErrorInvalidAddress);
        }

        return Volatile.Read(ref _keyboardOpen) == 0
            ? ctx.SetReturn(ImeErrorNotOpened)
            : ctx.SetReturn(ImeErrorNoResourceId);
    }
}
