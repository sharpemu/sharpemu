// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Network;

public static class NetCtlExports
{
    private const int MaxCallbacks = 8;
    private const int NatInfoSize = 16;
    private const int NetCtlErrorNoSpace = unchecked((int)0x80412103);
    private const int NetCtlErrorInvalidAddress = unchecked((int)0x80412107);
    private const int NetCtlErrorNotConnected = unchecked((int)0x80412108);
    private const int NetCtlInfoDevice = 1;
    private const int NetCtlInfoEtherAddress = 2;
    private const int NetCtlInfoMtu = 3;
    private const int NetCtlInfoLink = 4;
    private const int NetCtlInfoIpConfig = 11;
    private const int NetCtlInfoDhcpHostname = 12;
    private const int NetCtlInfoPppoeAuthName = 13;
    private const int NetCtlInfoIpAddress = 14;
    private const int NetCtlInfoNetmask = 15;
    private const int NetCtlInfoDefaultRoute = 16;
    private const int NetCtlInfoPrimaryDns = 17;
    private const int NetCtlInfoSecondaryDns = 18;
    private const int NetCtlInfoHttpProxyConfig = 19;
    private const int NetCtlInfoHttpProxyServer = 20;
    private const int NetCtlInfoHttpProxyPort = 21;
    private const int NetCtlDeviceWired = 0;
    private const int NetCtlLinkDisconnected = 0;
    private const int NetCtlIpConfigStatic = 0;
    private static readonly object CallbackGate = new();
    private static readonly CallbackRegistration[] Callbacks = new CallbackRegistration[MaxCallbacks];

    private readonly record struct CallbackRegistration(ulong Function, ulong Argument);

    [SysAbiExport(
        Nid = "gky0+oaNM4k",
        ExportName = "sceNetCtlInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlInit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "JO4yuTuMoKI",
        ExportName = "sceNetCtlGetNatInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetNatInfo(CpuContext ctx)
    {
        var natInfoAddress = ctx[CpuRegister.Rdi];
        if (natInfoAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        Span<byte> natInfo = stackalloc byte[NatInfoSize];
        if (!ctx.Memory.TryRead(natInfoAddress, natInfo))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(natInfo[..sizeof(uint)]);
        if (size != NatInfoSize)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, typeof(long));
        }

        BinaryPrimitives.WriteInt32LittleEndian(natInfo[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(natInfo[8..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(natInfo[12..], 0x7F000001);
        return ctx.Memory.TryWrite(natInfoAddress, natInfo)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "iQw3iQPhvUQ",
        ExportName = "sceNetCtlCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlCheckCallback(CpuContext ctx)
    {
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "uBPlr0lbuiI",
        ExportName = "sceNetCtlGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        Span<byte> stateBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(stateBytes, 0);
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    [SysAbiExport(
        Nid = "UJ+Z7Q+4ck0",
        ExportName = "sceNetCtlRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallback(CpuContext ctx)
    {
        var function = ctx[CpuRegister.Rdi];
        var argument = ctx[CpuRegister.Rsi];
        var callbackIdAddress = ctx[CpuRegister.Rdx];
        if (function == 0 || callbackIdAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        lock (CallbackGate)
        {
            var callbackId = Array.FindIndex(Callbacks, static callback => callback.Function == 0);
            if (callbackId < 0)
            {
                return ctx.SetReturn(NetCtlErrorNoSpace, typeof(long));
            }

            Span<byte> callbackIdBytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(callbackIdBytes, unchecked((uint)callbackId));
            if (!ctx.Memory.TryWrite(callbackIdAddress, callbackIdBytes))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }

            Callbacks[callbackId] = new CallbackRegistration(function, argument);
        }

        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK, typeof(long));
    }

    [SysAbiExport(
        Nid = "1NE9OWdBIww",
        ExportName = "sceNetCtlRegisterCallbackV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallbackV6(CpuContext ctx) => NetCtlRegisterCallback(ctx);

    [SysAbiExport(
        Nid = "obuxdTiwkF8",
        ExportName = "sceNetCtlGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetInfo(CpuContext ctx)
    {
        var code = unchecked((int)ctx[CpuRegister.Rdi]);
        var infoAddress = ctx[CpuRegister.Rsi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        return code switch
        {
            NetCtlInfoDevice => WriteUInt32(ctx, infoAddress, NetCtlDeviceWired),
            NetCtlInfoEtherAddress => WriteZeroBytes(ctx, infoAddress, 6),
            NetCtlInfoMtu => WriteUInt32(ctx, infoAddress, 1500),
            NetCtlInfoLink => WriteUInt32(ctx, infoAddress, NetCtlLinkDisconnected),
            NetCtlInfoIpConfig => WriteUInt32(ctx, infoAddress, NetCtlIpConfigStatic),
            NetCtlInfoDhcpHostname => WriteAsciiZ(ctx, infoAddress, string.Empty, 256),
            NetCtlInfoPppoeAuthName => WriteAsciiZ(ctx, infoAddress, string.Empty, 128),
            NetCtlInfoIpAddress => WriteAsciiZ(ctx, infoAddress, "127.0.0.1", 16),
            NetCtlInfoNetmask => WriteAsciiZ(ctx, infoAddress, "255.0.0.0", 16),
            NetCtlInfoDefaultRoute => WriteAsciiZ(ctx, infoAddress, "127.0.0.1", 16),
            NetCtlInfoPrimaryDns => WriteAsciiZ(ctx, infoAddress, "1.1.1.1", 16),
            NetCtlInfoSecondaryDns => WriteAsciiZ(ctx, infoAddress, "1.1.1.1", 16),
            NetCtlInfoHttpProxyConfig => WriteUInt32(ctx, infoAddress, 0),
            NetCtlInfoHttpProxyServer => WriteAsciiZ(ctx, infoAddress, string.Empty, 256),
            NetCtlInfoHttpProxyPort => WriteUInt16(ctx, infoAddress, 0),
            _ => ctx.SetReturn(NetCtlErrorNotConnected, typeof(long)),
        };
    }

    private static int WriteZeroBytes(CpuContext ctx, ulong address, int count)
    {
        Span<byte> bytes = stackalloc byte[count];
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int WriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int WriteUInt16(CpuContext ctx, ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int WriteAsciiZ(CpuContext ctx, ulong address, string value, int byteCount)
    {
        Span<byte> bytes = stackalloc byte[byteCount];
        var copyCount = Math.Min(value.Length, byteCount - 1);
        for (var i = 0; i < copyCount; i++)
        {
            bytes[i] = (byte)value[i];
        }

        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }
}
