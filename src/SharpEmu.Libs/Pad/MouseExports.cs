// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Pad;

public static class MouseExports
{
    private const int PrimaryUserId = 0x10000000;
    private const int MouseDataSize = 0x28;
    private const int MouseErrorInvalidArgument = unchecked((int)0x80020002);
    private const int MouseErrorInvalidHandle = unchecked((int)0x80020003);
    private const int MouseErrorNotInitialized = unchecked((int)0x80020005);
    private const int MouseErrorAlreadyOpened = unchecked((int)0x80020008);

    private static readonly bool[] OpenHandles = new bool[2];
    private static bool _initialized;
    public static int MouseInit(CpuContext ctx)
    {
        _initialized = true;
        Array.Clear(OpenHandles);
        return SetReturn(ctx, 0);
    }
    public static int MouseOpen(CpuContext ctx)
    {
        if (!_initialized)
        {
            return SetReturn(ctx, MouseErrorNotInitialized);
        }

        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        if (userId != PrimaryUserId || type != 0 || index is < 0 or > 1)
        {
            return SetReturn(ctx, MouseErrorInvalidArgument);
        }

        if (OpenHandles[index])
        {
            return SetReturn(ctx, MouseErrorAlreadyOpened);
        }

        OpenHandles[index] = true;
        return SetReturn(ctx, index);
    }
    public static int MouseRead(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (dataAddress == 0 || count is < 1 or > 64)
        {
            return SetReturn(ctx, MouseErrorInvalidArgument);
        }

        if (handle is < 0 or > 1 || !OpenHandles[handle])
        {
            return SetReturn(ctx, MouseErrorInvalidHandle);
        }

        Span<byte> data = stackalloc byte[MouseDataSize];
        data.Clear();
        var ticks = Stopwatch.GetTimestamp();
        var timestamp =
            ((ulong)(ticks / Stopwatch.Frequency) * 1_000_000UL) +
            ((ulong)(ticks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency);
        BinaryPrimitives.WriteUInt64LittleEndian(data, timestamp);
        data[0x08] = 0; // No host mouse is exposed to the guest yet.
        return ctx.Memory.TryWrite(dataAddress, data)
            ? SetReturn(ctx, 1)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cAnT0Rw-IwU",
        ExportName = "sceMouseClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceMouse")]
    public static int MouseClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (handle is < 0 or > 1 || !OpenHandles[handle])
        {
            return SetReturn(ctx, MouseErrorInvalidHandle);
        }

        OpenHandles[handle] = false;
        return SetReturn(ctx, 0);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
