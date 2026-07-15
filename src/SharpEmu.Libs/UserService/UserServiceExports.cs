// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Text;
using System.Threading;

namespace SharpEmu.Libs.UserService;

public static class UserServiceExports
{
    private const int OrbisUserServiceErrorInvalidArgument = unchecked((int)0x80960005);
    private const int OrbisUserServiceErrorNoEvent = unchecked((int)0x80960007);
    private const int OrbisUserServiceErrorInvalidParameter = unchecked((int)0x80960009);
    private const int OrbisUserServiceErrorBufferTooShort = unchecked((int)0x8096000A);
    // Retail user ids encode their local user slot in the 0x10000000 range;
    // the first signed-in user maps to slot 0.  Small emulator-local ids do
    // not pass Unity's user-id-to-slot conversion and become slot -1.
    private const int PrimaryUserId = 0x10000000;
    private const int InvalidUserId = -1;
    private const string PrimaryUserName = "SharpEmu";
    private static int _loginEventDelivered;

    [SysAbiExport(
        Nid = "j3YMu1MVNNo",
        ExportName = "sceUserServiceInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceInitialize(CpuContext ctx)
    {
        Trace("initialize");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CdWp0oHWGr0",
        ExportName = "sceUserServiceGetInitialUser",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetInitialUser(CpuContext ctx)
    {
        var userIdAddress = ctx[CpuRegister.Rdi];
        if (userIdAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        return TryWriteInt32(ctx, userIdAddress, PrimaryUserId)
            ? SetReturnWithTrace(ctx, 0, $"get_initial_user user={PrimaryUserId} out=0x{userIdAddress:X16}")
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "fPhymKNvK-A",
        ExportName = "sceUserServiceGetLoginUserIdList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetLoginUserIdList(CpuContext ctx)
    {
        var userIdListAddress = ctx[CpuRegister.Rdi];
        if (userIdListAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> userIds = stackalloc byte[sizeof(int) * 4];
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x00..], PrimaryUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x04..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x08..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x0C..], InvalidUserId);
        return ctx.Memory.TryWrite(userIdListAddress, userIds)
            ? SetReturnWithTrace(
                ctx,
                0,
                $"get_login_user_id_list users=[{PrimaryUserId},{InvalidUserId},{InvalidUserId},{InvalidUserId}] " +
                $"out=0x{userIdListAddress:X16}")
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "yH17Q6NWtVg",
        ExportName = "sceUserServiceGetEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetEvent(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (eventAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        if (Interlocked.Exchange(ref _loginEventDelivered, 1) != 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorNoEvent);
        }

        Span<byte> payload = stackalloc byte[sizeof(int) * 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload[0..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload[sizeof(int)..], PrimaryUserId);
        return ctx.Memory.TryWrite(eventAddress, payload)
            ? SetReturnWithTrace(
                ctx,
                0,
                $"get_event type=login user={PrimaryUserId} out=0x{eventAddress:X16}")
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "1xxcMiGu2fo",
        ExportName = "sceUserServiceGetUserName",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetUserName(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var nameAddress = ctx[CpuRegister.Rsi];
        var capacity = ctx[CpuRegister.Rdx];
        if (userId != PrimaryUserId)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidParameter);
        }

        if (nameAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        var nameBytes = Encoding.UTF8.GetBytes(PrimaryUserName);
        if (capacity <= (ulong)nameBytes.Length)
        {
            return SetReturn(ctx, OrbisUserServiceErrorBufferTooShort);
        }

        Span<byte> output = stackalloc byte[nameBytes.Length + 1];
        nameBytes.CopyTo(output);
        return ctx.Memory.TryWrite(nameAddress, output)
            ? SetReturnWithTrace(
                ctx,
                0,
                $"get_user_name user={userId} name='{PrimaryUserName}' out=0x{nameAddress:X16}")
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // Name not yet in ps5_names.txt and the NID was captured from titles; revisit when the symbol is catalogued.
    #pragma warning disable SHEM006
    [SysAbiExport(
        Nid = "D-CzAxQL0XI",
        ExportName = "sceUserServiceGetPlatformPrivacySetting",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetPlatformPrivacySetting(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (parameterId != 1000)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidParameter);
        }

        if (valueAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        return TryWriteInt32(ctx, valueAddress, 0)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
    #pragma warning restore SHEM006

    [SysAbiExport(
        Nid = "woNpu+45RLk",
        ExportName = "sceUserServiceGetAgeLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAgeLevel(CpuContext ctx) =>
        WriteUserSettingInt32(ctx, 18, "get_age_level");

    [SysAbiExport(
        Nid = "-sD02mFDBh4",
        ExportName = "sceUserServiceGetGamePresets",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetGamePresets(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var presetsAddress = ctx[CpuRegister.Rsi];
        if (userId != PrimaryUserId)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidParameter);
        }

        if (presetsAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> presets = stackalloc byte[0x28];
        presets.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(presets, (ulong)presets.Length);
        if (!ctx.Memory.TryWrite(presetsAddress, presets))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturnWithTrace(
            ctx,
            0,
            $"get_game_presets user={userId} out=0x{presetsAddress:X16}");
    }

    [SysAbiExport(
        Nid = "rnEhHqG-4xo",
        ExportName = "sceUserServiceGetAccessibilityChatTranscription",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityChatTranscription(CpuContext ctx) =>
        WriteUserSettingInt32(ctx, 0, "get_accessibility_chat_transcription");

    [SysAbiExport(
        Nid = "ZKJtxdgvzwg",
        ExportName = "sceUserServiceGetAccessibilityPressAndHoldDelay",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityPressAndHoldDelay(CpuContext ctx) =>
        WriteUserSettingInt32(ctx, 0, "get_accessibility_press_and_hold_delay");

    [SysAbiExport(
        Nid = "-3Y5GO+-i78",
        ExportName = "sceUserServiceGetAccessibilityTriggerEffect",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityTriggerEffect(CpuContext ctx) =>
        WriteUserSettingInt32(ctx, 0, "get_accessibility_trigger_effect");

    [SysAbiExport(
        Nid = "qWYHOFwqCxY",
        ExportName = "sceUserServiceGetAccessibilityVibration",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityVibration(CpuContext ctx) =>
        WriteUserSettingInt32(ctx, 1, "get_accessibility_vibration");

    [SysAbiExport(
        Nid = "hD-H81EN9Vg",
        ExportName = "sceUserServiceGetAccessibilityZoomEnabled",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityZoomEnabled(CpuContext ctx) =>
        WriteUserSettingInt32(ctx, 0, "get_accessibility_zoom_enabled");

    [SysAbiExport(
        Nid = "O6IW1-Dwm-w",
        ExportName = "sceUserServiceGetAccessibilityZoomFollowFocus",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAccessibilityZoomFollowFocus(CpuContext ctx) =>
        WriteUserSettingInt32(ctx, 0, "get_accessibility_zoom_follow_focus");

    private static bool TryWriteInt32(CpuContext ctx, ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int WriteUserSettingInt32(CpuContext ctx, int value, string operation)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (userId != PrimaryUserId)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidParameter);
        }

        if (valueAddress == 0)
        {
            return SetReturn(ctx, OrbisUserServiceErrorInvalidArgument);
        }

        if (!TryWriteInt32(ctx, valueAddress, value))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturnWithTrace(
            ctx,
            0,
            $"{operation} user={userId} value={value} out=0x{valueAddress:X16}");
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static int SetReturnWithTrace(CpuContext ctx, int result, string message)
    {
        Trace(message);
        return SetReturn(ctx, result);
    }

    private static void Trace(string message)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_USER_SERVICE"),
                "1",
                StringComparison.Ordinal))
        {
            var returnRip = GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame)
                ? frame.ReturnRip
                : 0;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] user_service.{message} ret=0x{returnRip:X16}");
        }
    }
}
