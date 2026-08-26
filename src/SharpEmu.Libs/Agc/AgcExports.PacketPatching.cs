// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial patches previously constructed AGC command packets.

    [SysAbiExport(
        Nid = "vcmNN+AAXnY",
        ExportName = "sceAgcSetCxRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "cx");

    [SysAbiExport(
        Nid = "whb1RL7K4Ss",
        ExportName = "sceAgcSetCxRegIndirectPatchSetNumRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchSetNumRegisters(CpuContext ctx) =>
        SetIndirectPatchRegisterCount(ctx, "cx");

    [SysAbiExport(
        Nid = "Qrj4c+61z4A",
        ExportName = "sceAgcSetShRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "sh");

    [SysAbiExport(
        Nid = "6lNcCp+fxi4",
        ExportName = "sceAgcSetUcRegIndirectPatchSetAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchSetAddress(CpuContext ctx) =>
        SetIndirectPatchAddress(ctx, "uc");

    [SysAbiExport(
        Nid = "d-6uF9sZDIU",
        ExportName = "sceAgcSetCxRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetCxRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "cx");

    [SysAbiExport(
        Nid = "z2duB-hHQSM",
        ExportName = "sceAgcSetShRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetShRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "sh");

    [SysAbiExport(
        Nid = "vRoArM9zaIk",
        ExportName = "sceAgcSetUcRegIndirectPatchAddRegisters",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetUcRegIndirectPatchAddRegisters(CpuContext ctx) =>
        AddIndirectPatchRegisters(ctx, "uc");

    [SysAbiExport(
        Nid = "IxYiarKlXxM",
        ExportName = "sceAgcDmaDataPatchSetDstAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetDstAddressOrOffset(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var destinationAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var destinationOffset = packetLength == 7 ? 4UL : 16UL;
        return ctx.TryWriteUInt64(commandAddress + destinationOffset, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // SRC counterpart of sceAgcDmaDataPatchSetDstAddressOrOffset. Without this,
    // the source stays 0 and the DMA copy — often a label write — never runs.
    [SysAbiExport(
        Nid = "cdDRpqcFGbU",
        ExportName = "sceAgcDmaDataPatchSetSrcAddressOrOffsetOrImmediate",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DmaDataPatchSetSrcAddressOrOffsetOrImmediate(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var sourceValue = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RDmaData ||
            !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var packetLength = ((header >> 16) & 0x3FFFu) + 2;
        var sourceOffset = packetLength == 7 ? 12UL : 24UL;
        return ctx.TryWriteUInt64(commandAddress + sourceOffset, sourceValue)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "eAy8eGNsCuU",
        ExportName = "sceAgcWriteDataPatchSetCachePolicy",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetCachePolicy(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 1);

    [SysAbiExport(
        Nid = "tmy-+rBpspY",
        ExportName = "sceAgcWriteDataPatchSetDst",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetDst(CpuContext ctx) =>
        PatchWriteDataControlByte(ctx, byteIndex: 0);

    [SysAbiExport(
        Nid = "fPSCdQxgpSw",
        ExportName = "sceAgcWriteDataPatchSetAddressOrOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WriteDataPatchSetAddressOrOffset(CpuContext ctx)
    {
        // SDK revisions disagree on whether the packet or destination is the
        // first argument. Astro passes (destination, packet), while older
        // captures use (packet, destination), so identify the packet by its
        // header instead of hard-coding one ordering.
        var first = ctx[CpuRegister.Rdi];
        var second = ctx[CpuRegister.Rsi];
        ulong commandAddress;
        ulong destinationAddress;
        if (TryGetPacketIdentity(ctx, first, out var firstOp, out var firstRegister) &&
            firstOp == ItNop && firstRegister == RWriteData)
        {
            commandAddress = first;
            destinationAddress = second;
        }
        else if (TryGetPacketIdentity(ctx, second, out var secondOp, out var secondRegister) &&
                 secondOp == ItNop && secondRegister == RWriteData)
        {
            commandAddress = second;
            destinationAddress = first;
        }
        else
        {
            // Astro's SDK 9 ABI passes (address-or-offset, pointer-to-field)
            // rather than the whole packet. The field is already the packet's
            // 64-bit address payload, so patch it directly.
            if (second == 0 || !ctx.TryWriteUInt64(second, first))
            {
                return SetReturn(ctx, second == 0
                    ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            TraceAgc(
                $"agc.patch_write_data_field field=0x{second:X16} value=0x{first:X16}");
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        TraceAgc(
            $"agc.patch_write_data_addr cmd=0x{commandAddress:X16} dst=0x{destinationAddress:X16}");
        return ctx.TryWriteUInt64(commandAddress + 8, destinationAddress)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "3KDcnM3lrcU",
        ExportName = "sceAgcWaitRegMemPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 8UL
            : op == ItNop && register is RWaitMem32 or RWaitMem64
                ? 4UL
                : 0;
        if (fieldOffset == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItNop && register is RWaitMem32 or RWaitMem64
            ? TryWriteUInt32(
                  ctx,
                  commandAddress + fieldOffset,
                  (uint)address & (register == RWaitMem32 ? ~0x3u : ~0x7u)) &&
              TryWriteUInt32(ctx, commandAddress + fieldOffset + 4, (uint)(address >> 32) & 0x3FFFFu)
            : ctx.TryWriteUInt64(commandAddress + fieldOffset, address);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "n485EBnIWmk",
        ExportName = "sceAgcWaitRegMemPatchCompareFunction",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchCompareFunction(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var compareFunction = (uint)ctx[CpuRegister.Rsi];
        if (compareFunction > 7 ||
            !TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var fieldOffset = op == ItWaitRegMem
            ? 4UL
            : op == ItNop && register == RWaitMem32
                ? 20UL
                : op == ItNop && register == RWaitMem64
                    ? 28UL
                    : 0;
        return fieldOffset != 0 &&
               TryPatchUInt32Bits(ctx, commandAddress + fieldOffset, 0x7u, compareFunction)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, fieldOffset == 0
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "7nOoijNPvEU",
        ExportName = "sceAgcWaitRegMemPatchReference",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchReference(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var reference = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 16, (uint)reference)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 16, (uint)reference)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 20, reference);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "hXAnLgDHCoI",
        ExportName = "sceAgcWaitRegMemPatchMask",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int WaitRegMemPatchMask(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var mask = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var wrote = op == ItWaitRegMem
            ? TryWriteUInt32(ctx, commandAddress + 20, (uint)mask)
            : op == ItNop && register == RWaitMem32
                ? TryWriteUInt32(ctx, commandAddress + 12, (uint)mask)
                : op == ItNop && register == RWaitMem64 &&
                  ctx.TryWriteUInt64(commandAddress + 12, mask);
        return wrote
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, op == ItWaitRegMem ||
                             (op == ItNop && register is RWaitMem32 or RWaitMem64)
                ? OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    // PatchAddress/PatchData touch UInt64 fields at +12/+20 of an RReleaseMem
    // packet, so the packet is at least 7 dwords; use the 8-dword RELEASE_MEM
    // family size already used elsewhere in this file.
    [SysAbiExport(
        Nid = "hL7C0IRpWZI",
        ExportName = "sceAgcCbQueueEndOfPipeActionGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbQueueEndOfPipeActionGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "0fWWK5uG9rQ",
        ExportName = "sceAgcQueueEndOfPipeActionPatchAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchAddress(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) ||
            op != ItNop ||
            register != RReleaseMem)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 12, address)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "J8YCgfKAMQs",
        ExportName = "sceAgcQueueEndOfPipeActionPatchGcrCntl",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchGcrCntl(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x0000_FFFFu,
                (uint)ctx[CpuRegister.Rsi] & 0xFFFFu)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "MlEw1feXcjg",
        ExportName = "sceAgcQueueEndOfPipeActionPatchData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchData(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        if (!IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(commandAddress + 20, ctx[CpuRegister.Rsi])
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "T9fjQIINoeE",
        ExportName = "sceAgcQueueEndOfPipeActionPatchType",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int QueueEndOfPipeActionPatchType(CpuContext ctx)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var dataSelection = (uint)ctx[CpuRegister.Rsi];
        TraceAgc(
            $"agc.eop_patch_type cmd=0x{commandAddress:X16} value=0x{dataSelection:X8}");
        if (dataSelection > 3 || !IsAgcReleaseMemPacket(ctx, commandAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return TryPatchUInt32Bits(
                ctx,
                commandAddress + 8,
                0x00FF_0000u,
                dataSelection << 16)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool IsAgcReleaseMemPacket(CpuContext ctx, ulong commandAddress) =>
        TryGetPacketIdentity(ctx, commandAddress, out var op, out var register) &&
        op == ItNop &&
        register == RReleaseMem;

    private static bool TryPatchUInt32Bits(
        CpuContext ctx,
        ulong address,
        uint mask,
        uint value)
    {
        return TryReadUInt32(ctx, address, out var current) &&
               TryWriteUInt32(ctx, address, PatchUInt32Bits(current, mask, value));
    }

    private static uint PatchUInt32Bits(uint current, uint mask, uint value) =>
        (current & ~mask) | (value & mask);

    private static int SetIndirectPatchAddress(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        if (commandAddress == 0 || registersAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(registersAddress >> 32)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_addr cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int SetIndirectPatchRegisterCount(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, commandAddress + 4, registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_count cmd=0x{commandAddress:X16} count={registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PatchWriteDataControlByte(CpuContext ctx, int byteIndex)
    {
        if (!TryResolveWriteDataPatchArguments(
                ctx,
                ctx[CpuRegister.Rdi],
                ctx[CpuRegister.Rsi],
                out var commandAddress,
                out var value) ||
            !TryReadUInt32(ctx, commandAddress + 4, out var control))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var shift = byteIndex * 8;
        var patchedControl = (control & ~(0xFFu << shift)) | (((uint)value & 0xFFu) << shift);
        return TryWriteUInt32(ctx, commandAddress + 4, patchedControl)
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryResolveWriteDataPatchArguments(
        CpuContext ctx,
        ulong first,
        ulong second,
        out ulong commandAddress,
        out ulong value)
    {
        if (IsWriteDataPacket(ctx, first))
        {
            commandAddress = first;
            value = second;
            return true;
        }

        if (IsWriteDataPacket(ctx, second))
        {
            commandAddress = second;
            value = first;
            return true;
        }

        commandAddress = 0;
        value = 0;
        return false;
    }

    private static bool IsWriteDataPacket(CpuContext ctx, ulong commandAddress)
    {
        if (!TryGetPacketIdentity(ctx, commandAddress, out var op, out var register))
        {
            return false;
        }

        return op == ItWriteData || (op == ItNop && register == RWriteData);
    }

    private static int AddIndirectPatchRegisters(CpuContext ctx, string registerSpace)
    {
        var commandAddress = ctx[CpuRegister.Rdi];
        var registerCount = (uint)ctx[CpuRegister.Rsi];
        if (commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadUInt32(ctx, commandAddress + 4, out var currentCount) ||
            !TryWriteUInt32(ctx, commandAddress + 4, currentCount + registerCount))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAgc($"agc.patch_{registerSpace}_add cmd=0x{commandAddress:X16} add={registerCount} total={currentCount + registerCount}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryGetPacketIdentity(
        CpuContext ctx,
        ulong commandAddress,
        out uint op,
        out uint register)
    {
        op = 0;
        register = 0;
        if (commandAddress == 0 || !TryReadUInt32(ctx, commandAddress, out var header))
        {
            return false;
        }

        op = (header >> 8) & 0xFFu;
        register = (header >> 2) & 0x3Fu;
        return true;
    }
}
