// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Text;

namespace SharpEmu.Libs.UserService;

public static class UserServiceExports
{
    private const int OrbisUserServiceErrorInvalidArgument = unchecked((int)0x80960005);
    private const int OrbisUserServiceErrorNoEvent = unchecked((int)0x80960007);
    private const int OrbisUserServiceErrorInvalidParameter = unchecked((int)0x80960009);
    private const int OrbisUserServiceErrorBufferTooShort = unchecked((int)0x8096000A);
    // Retail user-service IDs begin at 0x3E8.
    private const int PrimaryUserId = 1000;
    private const int InvalidUserId = -1;
    private const string PrimaryUserName = "SharpEmu";
    private static int _loginEventDelivered;

    private static readonly bool _traceUserService =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_USER_SERVICE"), "1", StringComparison.Ordinal);

    [SysAbiExport(
        Nid = "j3YMu1MVNNo",
        ExportName = "sceUserServiceInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceInitialize(CpuContext ctx)
    {
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
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var result = ctx.TryWriteInt32(userIdAddress, PrimaryUserId)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService($"get_initial_user out=0x{userIdAddress:X16} value={PrimaryUserId} result=0x{result:X8}");
        return ctx.SetReturn(result);
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
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        Span<byte> userIds = stackalloc byte[sizeof(int) * 4];
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x00..], PrimaryUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x04..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x08..], InvalidUserId);
        BinaryPrimitives.WriteInt32LittleEndian(userIds[0x0C..], InvalidUserId);
        return ctx.Memory.TryWrite(userIdListAddress, userIds)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
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
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        if (Interlocked.Exchange(ref _loginEventDelivered, 1) != 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorNoEvent);
        }

        Span<byte> payload = stackalloc byte[sizeof(int) * 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload[0..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload[sizeof(int)..], PrimaryUserId);
        return ctx.Memory.TryWrite(eventAddress, payload)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
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
        // Zero selects the current user.
        if (userId != 0 && userId != PrimaryUserId)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (nameAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        var nameBytes = Encoding.UTF8.GetBytes(PrimaryUserName);
        if (capacity <= (ulong)nameBytes.Length)
        {
            return ctx.SetReturn(OrbisUserServiceErrorBufferTooShort);
        }

        Span<byte> output = stackalloc byte[nameBytes.Length + 1];
        nameBytes.CopyTo(output);
        var result = ctx.Memory.TryWrite(nameAddress, output)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        TraceUserService(
            $"get_user_name user={userId} out=0x{nameAddress:X16} capacity=0x{capacity:X} value='{PrimaryUserName}' result=0x{result:X8}");
        return ctx.SetReturn(result);
    }

    [SysAbiExport(
        Nid = "-sD02mFDBh4",
        ExportName = "sceUserServiceGetGamePresets",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetGamePresets(CpuContext ctx)
    {
        // Return deterministic defaults for the offline profile.
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var resultAddress = ctx[CpuRegister.Rcx];
        Span<byte> defaults = stackalloc byte[0x18];
        if (resultAddress == 0 || !ctx.Memory.TryWrite(resultAddress, defaults))
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        TraceUserService(
            $"get_game_presets user={userId} title=0x{ctx[CpuRegister.Rsi]:X16} " +
            $"key=0x{ctx[CpuRegister.R9]:X} out=0x{resultAddress:X16} result=0x00000000");
        return ctx.SetReturn(0);
    }

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
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        return ctx.TryWriteInt32(valueAddress, 0)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "woNpu+45RLk",
        ExportName = "sceUserServiceGetAgeLevel",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int UserServiceGetAgeLevel(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var ageLevelAddress = ctx[CpuRegister.Rsi];
        if (userId != 1000)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidParameter);
        }

        if (ageLevelAddress == 0)
        {
            return ctx.SetReturn(OrbisUserServiceErrorInvalidArgument);
        }

        // Report an adult account so titles skip parental-restriction paths.
        return ctx.TryWriteInt32(ageLevelAddress, 21)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static void TraceUserService(string message)
    {
        if (_traceUserService)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] user_service.{message}");
        }
    }
}
