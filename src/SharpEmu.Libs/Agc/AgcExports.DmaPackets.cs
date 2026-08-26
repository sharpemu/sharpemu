// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial constructs AGC DMA transfer packets.

    [SysAbiExport(
        Nid = "WmAc2MEj6Io",
        ExportName = "sceAgcDcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var source = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R8];
        var sourceCachePolicy = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var control4Raw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var sourceAddress) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var byteCount) ||
            !TryReadUInt64(ctx, stackAddress + (4 * sizeof(ulong)), out var control7Raw) ||
            !TryReadUInt64(ctx, stackAddress + (5 * sizeof(ulong)), out var control8Raw) ||
            !TryReadUInt64(ctx, stackAddress + (6 * sizeof(ulong)), out var control9Raw))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || byteCount == 0 || (byteCount & 3) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var control4 = (uint)(control4Raw & 0xFF);
        var control7 = (uint)(control7Raw & 0xFF);
        var control8 = (uint)(control8Raw & 0xFF);
        var control9 = (uint)(control9Raw & 0xFF);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RDmaData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination |
                (destinationCachePolicy << 8) |
                (source << 16) |
                (sourceCachePolicy << 24)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                control4 | (control7 << 8) | (control8 << 16) | (control9 << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, byteCount) ||
            !ctx.TryWriteUInt64(commandAddress + 16, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 24, sourceAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_dma_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} src=0x{sourceAddress:X16} bytes={byteCount} " +
            $"control0=0x{destination | (destinationCachePolicy << 8) | (source << 16) | (sourceCachePolicy << 24):X8}");
        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "2ccJz9LQI+w",
        ExportName = "sceAgcDcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "-RnpfpxIhec",
        ExportName = "sceAgcAcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var sourceSelector = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationSelector = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceOrImmediate) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var byteCount) ||
            commandBufferAddress == 0 ||
            byteCount == 0 ||
            byteCount > 256u * 1024u * 1024u ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDmaData)) ||
            !ctx.TryWriteUInt64(commandAddress + 4, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 12, sourceOrImmediate) ||
            !TryWriteUInt32(ctx, commandAddress + 20, byteCount) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 24,
                sourceSelector | (destinationSelector << 8)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "M0ttm8h7SKA",
        ExportName = "sceAgcAcbDmaDataGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDmaDataGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }
}
