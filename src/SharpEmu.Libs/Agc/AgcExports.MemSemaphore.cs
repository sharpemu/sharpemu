// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    private const uint ItMemSemaphore = 0x39;

    [SysAbiExport(
        Nid = "G0jrLdvEqDw",
        ExportName = "sceAgcDcbMemSemaphore",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbMemSemaphore(CpuContext ctx) =>
        BuildMemSemaphorePacket(ctx);

    [SysAbiExport(
        Nid = "q4VuU-QsLOE",
        ExportName = "sceAgcAcbMemSemaphore",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbMemSemaphore(CpuContext ctx) =>
        BuildMemSemaphorePacket(ctx);

    private static int BuildMemSemaphorePacket(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var semaphoreAddress = ctx[CpuRegister.Rsi];
        var operation = unchecked((uint)ctx[CpuRegister.Rdx]);
        var signalType = unchecked((uint)ctx[CpuRegister.Rcx]);
        var mailbox = unchecked((uint)ctx[CpuRegister.R8]);
        if (commandBufferAddress == 0 ||
            semaphoreAddress == 0 ||
            (semaphoreAddress & (sizeof(ulong) - 1)) != 0 ||
            operation is not (6u or 7u) ||
            signalType > 1 ||
            mailbox > 1)
        {
            return ReturnPointer(ctx, 0);
        }

        var control = (operation << 29) |
            (signalType << 20) |
            (mailbox << 16);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var packetAddress) ||
            !TryWriteUInt32(ctx, packetAddress, Pm4(4, ItMemSemaphore, 0)) ||
            !TryWriteUInt32(ctx, packetAddress + sizeof(uint), unchecked((uint)semaphoreAddress)) ||
            !TryWriteUInt32(
                ctx,
                packetAddress + (2 * sizeof(uint)),
                unchecked((uint)(semaphoreAddress >> 32))) ||
            !TryWriteUInt32(ctx, packetAddress + (3 * sizeof(uint)), control))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, packetAddress);
    }

}
