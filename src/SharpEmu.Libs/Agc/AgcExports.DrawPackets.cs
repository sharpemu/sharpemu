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
            !TryEmitCommandDwords(ctx, commandBufferAddress, out var commandAddress, Pm4(3, ItSetUconfigRegIndex, 0), 0x2000_0000u | VgtIndexType, 0x400u | (indexSize & 0x3u) | ((cachePolicy & 0x3u) << 6) | ((perInstanceObjectIdSupport & 0x1u) << 14)))
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
            !TryEmitCommandDwords(ctx, commandBufferAddress, out var commandAddress, Pm4(2, ItNop, RIndexCount), indexCount))
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

        if (!TryEmitCommandDwords(ctx, commandBufferAddress, out var commandAddress, Pm4(2, ItNumInstances, 0), instanceCount))
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
        var modifier = ctx[CpuRegister.Rcx];

        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var drawInitiator = (modifier & (1UL << 32)) != 0
            ? 0u
            : ((uint)modifier >> 3) & 0x20u;

        if (!TryEmitCommandDwords(ctx, commandBufferAddress, out var baseCommand, Pm4(3, ItIndexBase, 0), (uint)indexAddress, (uint)(indexAddress >> 32), Pm4(2, ItIndexBufferSize, 0), indexCount))
        {
            return ReturnPointer(ctx, 0);
        }

        // DRAW_INDEX_2 is six dwords: header, maximum index count, the
        // 64-bit index-buffer base, the draw count and the initiator.  The
        // former five-dword packet omitted both the real base and the count
        // field, so every call made by Unity looked like a zero-count draw to
        // the submitted-command parser and the complete scene was discarded.
        if (!TryEmitCommandDwords(ctx, commandBufferAddress, out var drawCommand, Pm4(6, ItDrawIndex2, 0), indexCount, (uint)indexAddress, (uint)(indexAddress >> 32), indexCount, drawInitiator))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_draw_index buf=0x{commandBufferAddress:X16} " +
            $"base=0x{baseCommand:X16} draw=0x{drawCommand:X16} " +
            $"count={indexCount} index=0x{indexAddress:X16} modifier=0x{modifier:X}");

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
        var drawModifier = ctx[CpuRegister.Rdx];

        if (commandBufferAddress == 0)
        {
            Interlocked.Increment(ref _indirectDrawEmitRejectCount);
            return ReturnPointer(ctx, 0);
        }

        var modifierBits = (uint)drawModifier;
        var firstVertexRegister = DrawModifierRegister(modifierBits, DrawModifierStartVertex, 9);
        var firstInstanceRegister = DrawModifierRegister(modifierBits, DrawModifierStartInstance, 19);
        var drawInitiator = (drawModifier & (1UL << 32)) != 0
            ? 2u
            : ((modifierBits >> 3) & 0x20u) | 2u;

        if (!TryEmitCommandDwords(ctx, commandBufferAddress, out var drawCommand, Pm4(5, ItDrawIndirect, 0), dataOffset, firstVertexRegister, firstInstanceRegister, drawInitiator))
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
            $"draw=0x{drawCommand:X16} offset=0x{dataOffset:X} modifier=0x{drawModifier:X}");

        return ReturnPointer(ctx, drawCommand);
    }

    [SysAbiExport(
        Nid = "cxPZ4Wgvdj8",
        ExportName = "sceAgcDcbDrawIndirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndirectGetSize(CpuContext ctx) =>
        ctx.SetReturn(5 * sizeof(uint));

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
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryEmitCommandDwords(ctx, commandBufferAddress, out var commandAddress, Pm4(7, ItNop, RDrawIndexAuto), indexCount, 0, 0, 0, 0, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_auto buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} count={indexCount} modifier=0x{modifier:X8}");
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
        var modifier = ctx[CpuRegister.Rdx];
        var modifierBits = (uint)modifier;
        // Only the multi draws have the CP write the draw index; a single draw's comes from the bound user data.
        var registerLocations = DrawModifierRegister(modifierBits, DrawModifierStartVertex, 9) |
            ((ulong)DrawModifierRegister(modifierBits, DrawModifierStartInstance, 19) << 32);

        // The API modifier selects register locations; it is not a packet initiator.
        var drawInitiator = (modifier & (1UL << 32)) != 0
            ? 2u : ((modifierBits >> 3) & 0x20u) | 2u;
        if (commandBufferAddress == 0 ||
            !TryEmitCommandDwords(ctx, commandBufferAddress, out var commandAddress, Pm4(5, ItDrawIndexIndirect, 0), dataOffset,
                (uint)registerLocations, (uint)(registerLocations >> 32), drawInitiator))
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
    public static int DcbDrawIndexIndirectMulti(CpuContext ctx) =>
        EmitDrawIndirectMulti(ctx, ItDrawIndexIndirectMulti, "agc.dcb_draw_index_indirect_multi");

    [SysAbiExport(
        Nid = "kUlvghKs-mA",
        ExportName = "sceAgcDcbDrawIndirectMulti",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDrawIndirectMulti(CpuContext ctx) =>
        EmitDrawIndirectMulti(ctx, ItDrawIndirectMulti, "agc.dcb_draw_indirect_multi");

    private const uint DrawModifierStartVertex = 1u << 0;
    private const uint DrawModifierStartInstance = 1u << 2;
    private const uint DrawModifierDrawIndex = 1u << 3;
    private const uint DrawModifierUserVgprs = 1u << 4;

    // The shader register of a user-data slot the modifier enables, as sce::Agc::get{VertexOffset,InstanceOffset,
    // DrawIndex}UserDataSlot read it: five bits at slotShift, no slot when the modifier passes the values in VGPRs.
    // Bits 29-31 name the frontmost stage; the hull stages' user data starts at 0x10C, the others' at 0x8C.
    private static uint DrawModifierRegister(uint modifierBits, uint enable, int slotShift)
    {
        if ((modifierBits & (enable | DrawModifierUserVgprs)) != enable)
            return 0x280u;

        var userData0 = (modifierBits >> 29) is 3 or 5 ? 0x10Cu : 0x8Cu;
        return userData0 + ((modifierBits >> slotShift) & 0x1Fu);
    }

    // Both multi draws take the same arguments and write the same ten-dword packet; the register the modifier names
    // holds the base vertex of an indexed draw and the start vertex of the other.
    private static int EmitDrawIndirectMulti(CpuContext ctx, uint opcode, string trace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var countFromMemory = (uint)ctx[CpuRegister.Rdx];
        var drawCount = (uint)ctx[CpuRegister.Rcx];
        var countAddress = ctx[CpuRegister.R8];
        var stride = (uint)ctx[CpuRegister.R9];
        if (countFromMemory > 1 ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var modifier))
            return ReturnPointer(ctx, 0);

        var modifierBits = (uint)modifier;
        var registerLocations = DrawModifierRegister(modifierBits, DrawModifierStartVertex, 9) |
            ((ulong)DrawModifierRegister(modifierBits, DrawModifierStartInstance, 19) << 32);
        var drawIndexRegister = DrawModifierRegister(modifierBits, DrawModifierDrawIndex, 24);
        if (drawIndexRegister != 0x280u)
            registerLocations |= ((ulong)drawIndexRegister << 16) | (1UL << 59);
        var drawInitiator = (modifier & (1UL << 32)) != 0
            ? 2u : ((modifierBits >> 3) & 0x20u) | 2u;

        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 10, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(10, opcode, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !ctx.TryWriteUInt64(commandAddress + 8, registerLocations) ||
            !TryWriteUInt32(ctx, commandAddress + 16, countFromMemory << 30) ||
            !TryWriteUInt32(ctx, commandAddress + 20, drawCount) ||
            !ctx.TryWriteUInt64(commandAddress + 24, countAddress & ~3UL) ||
            !TryWriteUInt32(ctx, commandAddress + 32, stride) ||
            !TryWriteUInt32(ctx, commandAddress + 36, drawInitiator))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"{trace} buf=0x{commandBufferAddress:X16} " +
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
        ctx[CpuRegister.Rax] = 10u * sizeof(uint);
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

        if (!TryEmitCommandDwords(ctx, commandBufferAddress, out var commandAddress, Pm4(3, ItIndexBase, 0), (uint)(indexBufferAddress & 0xFFFF_FFFFUL), (uint)(indexBufferAddress >> 32), Pm4(2, ItIndexBufferSize, 0), indexCount))
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
        var drawModifier = ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        // The API modifier is not a packet initiator. Bit 32 disables its bit-8 selection.
        var drawInitiator = (drawModifier & (1UL << 32)) != 0
            ? 0u
            : ((uint)drawModifier >> 3) & 0x20u;
        if (!TryEmitCommandDwords(ctx, commandBufferAddress, out var commandAddress, Pm4(5, ItDrawIndexOffset2, 0), Math.Max(indexCount, 1u), indexOffset, indexCount, drawInitiator))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_draw_index_offset buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset={indexOffset} count={indexCount} flags=0x{drawModifier:X8}");
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

    private static long _indirectDrawEmitRejectCount;
}
