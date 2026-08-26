// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial constructs AGC draw-command packets.

    [SysAbiExport(
        Nid = "GIIW2J37e70",
        ExportName = "sceAgcDcbSetIndexSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexSize(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexSize = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        return DcbSetIndexSizePacket(
            ctx,
            commandBufferAddress,
            indexSize,
            cachePolicy,
            perInstanceObjectIdSupport: 0);
    }

    [SysAbiExport(
        ExportName = "sceAgcDcbSetIndexSizeGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexSizeGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 3u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    private static int DcbSetIndexSizePacket(
        CpuContext ctx,
        ulong commandBufferAddress,
        uint indexSize,
        uint cachePolicy,
        uint perInstanceObjectIdSupport)
    {
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItSetUconfigRegIndex, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0x2000_0000u | VgtIndexType) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                0x400u |
                (indexSize & 0x3u) |
                ((cachePolicy & 0x3u) << 6) |
                ((perInstanceObjectIdSupport & 0x1u) << 14)))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_set_index_size buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} size={indexSize & 0x3u} " +
            $"cache={cachePolicy & 0x3u} instance_id={perInstanceObjectIdSupport & 0x1u}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "8N2tmT3jmC8",
        ExportName = "sceAgcDcbSetIndexCount",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCount(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RIndexCount)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mljzuGDZRQ4",
        ExportName = "sceAgcDcbSetIndexCountGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexCountGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 7u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "tSBxhAPyytQ",
        ExportName = "sceAgcDcbSetNumInstances",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetNumInstances(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var instanceCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNumInstances, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, instanceCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_num_instances buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={instanceCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "q88lQ+GP5Yk",
        ExportName = "sceAgcDcbDrawIndex",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndex(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var indexAddress = ctx[CpuRegister.Rdx];
        var modifier = (uint)ctx[CpuRegister.Rcx];

        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var baseCommand) ||
            !TryWriteUInt32(ctx, baseCommand, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 4, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, baseCommand + 8, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, baseCommand + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, baseCommand + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        // DRAW_INDEX_2 is six dwords: header, maximum index count, the
        // 64-bit index-buffer base, the draw count and the initiator.  The
        // former five-dword packet omitted both the real base and the count
        // field, so every call made by Unity looked like a zero-count draw to
        // the submitted-command parser and the complete scene was discarded.
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 6, out var drawCommand) ||
            !TryWriteUInt32(ctx, drawCommand, Pm4(6, ItDrawIndex2, 0)) ||
            !TryWriteUInt32(ctx, drawCommand + 4, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 8, (uint)indexAddress) ||
            !TryWriteUInt32(ctx, drawCommand + 12, (uint)(indexAddress >> 32)) ||
            !TryWriteUInt32(ctx, drawCommand + 16, indexCount) ||
            !TryWriteUInt32(ctx, drawCommand + 20, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index buf=0x{commandBufferAddress:X16} " +
            $"base=0x{baseCommand:X16} draw=0x{drawCommand:X16} " +
            $"count={indexCount} index=0x{indexAddress:X16}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "1q1titRBL6o",
        ExportName = "sceAgcDcbDrawIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var emit = Interlocked.Increment(ref _indirectDrawEmitCount);

        if (emit <= 12 || emit % 250 == 0)
        {
            var rcx = ctx[CpuRegister.Rcx];
            var dump = string.Empty;
            for (var word = 0; word < 8; word++)
            {
                dump += TryReadUInt32(ctx, rcx + dataOffset + ((ulong)word * 4), out var raw)
                    ? $" {raw}"
                    : " ?";
            }

            Console.Error.WriteLine(
                $"[LOADER][WARN] agc.emit_indirect#{emit} buf=0x{commandBufferAddress:X16} " +
                $"off=0x{dataOffset:X} rdx=0x{ctx[CpuRegister.Rdx]:X} rcx=0x{rcx:X} " +
                $"r8=0x{ctx[CpuRegister.R8]:X} rcx_words:{dump}");
        }

        if (commandBufferAddress == 0)
        {
            Interlocked.Increment(ref _indirectDrawEmitRejectCount);
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var drawCommand) ||
            !TryWriteUInt32(ctx, drawCommand, Pm4(5, ItDrawIndirect, 0)) ||
            !TryWriteUInt32(ctx, drawCommand + 4, dataOffset) ||
            !TryWriteUInt32(ctx, drawCommand + 8, 0) ||
            !TryWriteUInt32(ctx, drawCommand + 12, 0) ||
            !TryWriteUInt32(ctx, drawCommand + 16, 0))
        {
            var rejects = Interlocked.Increment(ref _indirectDrawEmitRejectCount);
            if (rejects <= 8 || rejects % 250 == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.emit_indirect_reject#{rejects} " +
                    $"buf=0x{commandBufferAddress:X16} reason=alloc_or_write");
            }

            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_indirect buf=0x{commandBufferAddress:X16} " +
            $"draw=0x{drawCommand:X16} offset=0x{dataOffset:X}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "Yw0jKSqop+E",
        ExportName = "sceAgcDcbDrawIndexAuto",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexAuto(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexCount = (uint)ctx[CpuRegister.Rsi];
        var modifier = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || modifier != 0x4000_0000)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 7, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(7, ItNop, RDrawIndexAuto)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_auto buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "t1vNu082-jM",
        ExportName = "sceAgcDcbDrawIndexIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, modifier))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index_indirect buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{dataOffset:X8} modifier=0x{modifier:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "ypVBz4uPKcQ",
        ExportName = "sceAgcDcbDrawIndexIndirectMulti",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectMulti(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var drawCount = (uint)ctx[CpuRegister.Rdx];
        var stride = DrawIndexedIndirectArgsSize;
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItDrawIndexIndirectMulti, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 20, drawCount) ||
            !TryWriteUInt32(ctx, commandAddress + 24, stride) ||
            !TryWriteUInt32(ctx, commandAddress + 28, modifier))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index_indirect_multi buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{dataOffset:X8} draws={drawCount} " +
            $"stride={stride} modifier=0x{modifier:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "mStuvI0zOtc",
        ExportName = "sceAgcDcbDrawIndexIndirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 5u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "r98I08t+LOg",
        ExportName = "sceAgcDcbDrawIndexIndirectMultiGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexIndirectMultiGetSize(CpuContext ctx)
    {
        // Eight, matching the packet DcbDrawIndexIndirectMulti emits.
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "l4fM9K-Lyks",
        ExportName = "sceAgcDcbSetIndexBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetIndexBuffer(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexBufferAddress = ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItIndexBase, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(indexBufferAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(indexBufferAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, Pm4(2, ItIndexBufferSize, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_index_buffer buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} addr=0x{indexBufferAddress:X16} count={indexCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "B+aG9DUnTKA",
        ExportName = "sceAgcDcbDrawIndexOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndexOffset(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var indexOffset = (uint)ctx[CpuRegister.Rsi];
        var indexCount = (uint)ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDrawIndexOffset2, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 8, indexOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 12, indexCount) ||
            !TryWriteUInt32(ctx, commandAddress + 16, flags & 0xE000_0001u))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_offset buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset={indexOffset} count={indexCount} flags=0x{flags:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    // Synthetic label for an uncatalogued NID (the Unknown* convention); the NID is authoritative.
    #pragma warning disable SHEM006
    [SysAbiExport(
    Nid = "-KRzWekV120",
    ExportName = "sceAgcDriverUnknown_KRzWekV120",
    Target = Generation.Gen5,
    LibraryName = "libSceAgc")]
    public static int DriverUnknownKRzWekV120(CpuContext ctx)
    {
        return DcbSetIndexSizePacket(
            ctx,
            ctx[CpuRegister.Rdi],
            (uint)ctx[CpuRegister.Rsi],
            (uint)ctx[CpuRegister.Rdx],
            (uint)ctx[CpuRegister.Rcx]);
    }
    #pragma warning restore SHEM006

    private static long _indirectDrawEmitCount;
    private static long _indirectDrawEmitRejectCount;
}
