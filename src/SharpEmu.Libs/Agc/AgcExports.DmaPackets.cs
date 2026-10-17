// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial constructs AGC DMA transfer packets.

    [SysAbiExport(
        Nid = "1rZSWUv1IRc",
        ExportName = "sceAgcDcbCopyData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbCopyData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destinationSelector = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationPolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var sourceSelector = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var sourcePolicy = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var stackAddress = ctx[CpuRegister.Rsp];

        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceValue) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var elementSizeRaw) ||
            !TryReadUInt64(ctx, stackAddress + (3 * sizeof(ulong)), out var confirmWriteRaw) ||
            commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var packetAddress))
        {
            return ReturnPointer(ctx, 0);
        }

        var elementSize = (uint)(elementSizeRaw & 0xFF);
        var confirmWrite = (uint)(confirmWriteRaw & 0xFF);
        var control =
            ((sourceSelector >> 1) & 0xFu) |
            (((destinationSelector >> 1) & 0xFu) << 8) |
            ((sourcePolicy & 0x3u) << 13) |
            ((elementSize & 0x1u) << 16) |
            ((confirmWrite & 0x1u) << 20) |
            ((destinationPolicy & 0x3u) << 25) |
            ((sourceSelector & 0x1u) << 30);

        if (!TryWriteUInt32(ctx, packetAddress, Pm4(6, ItCopyData, 0)) ||
            !TryWriteUInt32(ctx, packetAddress + 4, control) ||
            !TryWriteUInt32(ctx, packetAddress + 8, (uint)sourceValue) ||
            !TryWriteUInt32(ctx, packetAddress + 12, (uint)(sourceValue >> 32)) ||
            !TryWriteUInt32(ctx, packetAddress + 16, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, packetAddress + 20, (uint)(destinationAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_copy_data buf=0x{commandBufferAddress:X16} cmd=0x{packetAddress:X16} " +
            $"dstSel=0x{destinationSelector:X2} srcSel=0x{sourceSelector:X2} " +
            $"dst=0x{destinationAddress:X16} src=0x{sourceValue:X16} control=0x{control:X8}");
        return ReturnPointer(ctx, packetAddress);
    }

    [SysAbiExport(
        Nid = "WmAc2MEj6Io",
        ExportName = "sceAgcDcbDmaData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDmaData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var engine = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destination = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var source = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R8];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceCachePolicyRaw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var sourceAddress) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var byteCount) ||
            !TryReadUInt64(ctx, stackAddress + (4 * sizeof(ulong)), out var waitForPreviousRaw) ||
            !TryReadUInt64(ctx, stackAddress + (5 * sizeof(ulong)), out var writeConfirmRaw) ||
            !TryReadUInt64(ctx, stackAddress + (6 * sizeof(ulong)), out var blockEngineRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || byteCount == 0 || (byteCount & 3) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var sourceCachePolicy = (uint)(sourceCachePolicyRaw & 0xFF);
        var waitForPrevious = (uint)(waitForPreviousRaw & 0xFF);
        var writeConfirm = (uint)(writeConfirmRaw & 0xFF);
        var blockEngine = (uint)(blockEngineRaw & 0xFF);
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
                engine | (waitForPrevious << 8) | (writeConfirm << 16) | (blockEngine << 24)) ||
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
        var destinationSelector = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var destinationCachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var sourceSelector = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var sourceCachePolicy = (uint)(ctx[CpuRegister.R9] & 0xFF);
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var sourceOrImmediate) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var byteCount) ||
            !TryReadUInt64(ctx, stackAddress + (3 * sizeof(ulong)), out var waitForPrevious) ||
            !TryReadUInt64(ctx, stackAddress + (4 * sizeof(ulong)), out var writeConfirm) ||
            commandBufferAddress == 0 ||
            byteCount == 0 ||
            byteCount > 256u * 1024u * 1024u ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RDmaData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destinationSelector | (destinationCachePolicy << 8) |
                (sourceSelector << 16) | (sourceCachePolicy << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 8,
                ((uint)waitForPrevious & 0xFFu) << 8 | ((uint)writeConfirm & 0xFFu) << 16) ||
            !TryWriteUInt32(ctx, commandAddress + 12, byteCount) ||
            !ctx.TryWriteUInt64(commandAddress + 16, destinationAddress) ||
            !ctx.TryWriteUInt64(commandAddress + 24, sourceOrImmediate))
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
