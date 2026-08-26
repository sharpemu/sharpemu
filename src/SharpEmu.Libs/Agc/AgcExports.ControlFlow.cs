// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial constructs AGC command-buffer control flow.

    // Hardware REWIND is a fixed 2-dword header + body (valid bit 31).
    [SysAbiExport(
        Nid = "QIXCsbipds0",
        ExportName = "sceAgcDcbRewindGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbRewindGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    // Writes IT_REWIND. When valid=0 the submit parser suspends until
    // sceAgcRewindPatchSetRewindState sets bit 31 on the body dword.
    [SysAbiExport(
        Nid = "zfcxg-ewMK8",
        ExportName = "sceAgcDcbRewind",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbRewind(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        // rsi bit0 = valid; bit1 = offload_enable (PM4 body bits 31 / 24).
        var flags = ctx[CpuRegister.Rsi];
        var valid = (flags & 1UL) != 0;
        var offloadEnable = (flags & 2UL) != 0;
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var body = (valid ? RewindValidBit : 0u) |
                   (offloadEnable ? RewindOffloadEnableBit : 0u);
        if (!TryAllocateCommandDwords(ctx, dcb, 2, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(2, ItRewind, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, body))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_rewind buf=0x{dcb:X16} cmd=0x{cmd:X16} valid={valid} offload={offloadEnable}");
        return ReturnPointer(ctx, cmd);
    }

    // Patches the REWIND body dword's valid bit and wakes any DCB suspended on it.
    // rdi is the packet pointer returned by sceAgcDcbRewind (header address).
    [SysAbiExport(
        Nid = "ziVA3whp3p4",
        ExportName = "sceAgcRewindPatchSetRewindState",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int RewindPatchSetRewindState(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        var valid = (ctx[CpuRegister.Rsi] & 1UL) != 0;
        if (packetAddress == 0 ||
            (long)packetAddress < 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var bodyAddress = packetAddress;
        if (TryReadUInt32(ctx, packetAddress, out var header) &&
            ((header >> 8) & 0xFFu) == ItRewind)
        {
            bodyAddress = packetAddress + sizeof(uint);
        }

        if (!TryReadUInt32(ctx, bodyAddress, out var body) ||
            !TryWriteUInt32(
                ctx,
                bodyAddress,
                valid ? body | RewindValidBit : body & ~RewindValidBit))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryReadUInt32(ctx, bodyAddress, out var patched))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (valid)
        {
            GpuWaitRegistry.RecordProduced(ctx.Memory, bodyAddress, patched);
        }

        TraceAgc($"agc.rewind_patch addr=0x{bodyAddress:X16} valid={valid} body=0x{patched:X8}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Matches the 4-dword INDIRECT_BUFFER packet DcbJump writes below.
    // Returning NOT_FOUND here left callers with a null packet pointer and an
    // immediate write AV on RenderThread.
    [SysAbiExport(
        Nid = "VEGu4dixjUg",
        ExportName = "sceAgcDcbJumpGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbJumpGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "xSAR0LTcRKM",
        ExportName = "sceAgcDcbJump",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbJump(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var mode = (uint)ctx[CpuRegister.Rsi];
        var cachePolicy = (uint)ctx[CpuRegister.Rdx];
        var target = ctx[CpuRegister.Rcx];
        var sizeDwords = (uint)ctx[CpuRegister.R8];
        if (dcb == 0 || mode > 1 || cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var control = 0x0F20_0000u |
                      ((cachePolicy & 0x3u) << 28) |
                      ((mode & 0x1u) << 20) |
                      (sizeDwords & 0xFFFFFu);

        if (!TryAllocateCommandDwords(ctx, dcb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItIndirectBuffer, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 12, control))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    // Matches the 4-dword INDIRECT_BUFFER packet CbBranch writes below.
    [SysAbiExport(
        Nid = "uZW-mqsxkrM",
        ExportName = "sceAgcCbBranchGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbBranchGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    // COND_EXEC gates the following execCount dwords on a 32-bit predicate in
    // memory. Our submitted-packet walker skips unknown PM4 ops, so the recorded
    // packet degrades to "predicate always true" — the gated commands always run.
    [SysAbiExport(
        Nid = "BIPexNBSGog",
        ExportName = "sceAgcDcbCondExec",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbCondExec(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var predicateAddress = ctx[CpuRegister.Rsi];
        var execCountDwords = (uint)ctx[CpuRegister.Rdx];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, dcb, 5, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(5, ItCondExec, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(predicateAddress & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)(predicateAddress >> 32)) ||
            !ctx.TryWriteUInt32(cmd + 12, 0) ||
            !ctx.TryWriteUInt32(cmd + 16, execCountDwords & 0x3FFF))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    // How a title continues a frame whose command arena filled: it branches from
    // the tail of the exhausted buffer into a fresh one and submits only the first
    // buffer, leaving the driver to follow the link. Dropping this packet strands
    // everything written after the switch -- for UE 4.27 that is the rest of the
    // frame, including its flip and the end-of-frame labels the guest's AGC
    // interrupt thread needs before it will trigger the backbuffer event.
    //
    // The branch target and its length arrive on the stack, past six register
    // arguments (verified against a live call: the values matched the continuation
    // buffer the title had already written into).
    [SysAbiExport(
        Nid = "w1KFAHVqpaU",
        ExportName = "sceAgcCbBranch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbBranch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var target) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (3 * sizeof(ulong)), out var targetDwords))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItIndirectBuffer, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)targetDwords & 0xFFFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_branch buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"target=0x{target:X16} dwords={targetDwords}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "b-oySn+G2tE",
        ExportName = "sceAgcAcbJumpGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbJumpGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 4u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "e1DFTg+Sd8U",
        ExportName = "sceAgcAcbJump",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbJump(CpuContext ctx)
    {
        var acb = ctx[CpuRegister.Rdi];
        var target = ctx[CpuRegister.Rsi];
        var sizeDwords = (uint)ctx[CpuRegister.Rdx];
        if (acb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        const uint chainMode = 1;
        var control = 0x0F20_0000u |
                      (chainMode << 20) |
                      (sizeDwords & 0xFFFFFu);
        if (!TryAllocateCommandDwords(ctx, acb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItIndirectBuffer, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, (uint)(target & 0xFFFF_FFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)((target >> 32) & 0xFFFFUL)) ||
            !ctx.TryWriteUInt32(cmd + 12, control))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    [SysAbiExport(
        Nid = "bbFueFP+J4k",
        ExportName = "sceAgcDcbSetPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetPredication(CpuContext ctx)
    {
        var dcb = ctx[CpuRegister.Rdi];
        var condition = (uint)(ctx[CpuRegister.Rsi] & 1u);
        var operation = (uint)(ctx[CpuRegister.Rdx] & 0x7u);
        var waitOperation = (uint)(ctx[CpuRegister.Rcx] & 1u);
        var address = ctx[CpuRegister.R8];
        if (dcb == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var flags = (condition << 8) | (waitOperation << 12) | (operation << 16);
        if (!TryAllocateCommandDwords(ctx, dcb, 4, out var cmd) ||
            !ctx.TryWriteUInt32(cmd, Pm4(4, ItSetPredication, RZero)) ||
            !ctx.TryWriteUInt32(cmd + 4, flags) ||
            !ctx.TryWriteUInt32(cmd + 8, (uint)address & 0xFFFF_FFF0u) ||
            !ctx.TryWriteUInt32(cmd + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, cmd);
    }

    [SysAbiExport(
        Nid = "w6Dj1VJt5qY",
        ExportName = "sceAgcSetPacketPredication",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int SetPacketPredication(CpuContext ctx)
    {
        var packetAddress = ctx[CpuRegister.Rdi];
        var predication = ctx[CpuRegister.Rsi];
        if (packetAddress == 0 || !TryReadUInt32(ctx, packetAddress, out var header))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        header = (header & ~1u) | (predication == 1 ? 1u : 0u);
        return !ctx.TryWriteUInt32(packetAddress, header)
            ? ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT)
            : ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
