// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial builds general AGC command-buffer packets.

    private static long _dcbWriteDataTraceCount;
    private static long _dcbWaitRegMemTraceCount;
    private static long _packetPayloadTraceCount;

    private static uint EncodeWaitRegMemPoll(uint pollCycles) =>
        Math.Min(pollCycles >> 4, 0xFFFFu);

    private static uint EncodeWaitRegMem32Control(uint compareFunction, uint operation, uint cachePolicy) =>
        0x10u |
        (compareFunction & 0x7u) |
        ((operation & 0x3u) << 8) |
        ((operation & 0xCu) << 4) |
        ((cachePolicy & 0x3u) << 25);

    private static uint EncodeWaitRegMem64Control(uint compareFunction, uint operation, uint cachePolicy) =>
        0x10u |
        (compareFunction & 0x7u) |
        ((operation & 0x1u) << 8) |
        ((operation & 0x6u) << 5) |
        ((cachePolicy & 0x3u) << 25);

    // NID captured from shipped titles; the friendly name collides with a real catalog symbol of a different NID. Rename pending AGC API confirmation.
    #pragma warning disable SHEM004
    [SysAbiExport(
        Nid = "V++UgBtQhn0",
        ExportName = "sceAgcGetDataPacketPayloadAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int GetDataPacketPayloadAddress(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var commandAddress = ctx[CpuRegister.Rsi];
        var type = (int)ctx[CpuRegister.Rdx];
        if (outputAddress == 0 || commandAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var payloadAddress = commandAddress + 8;
        if (type == 0)
        {
            if (!TryReadUInt32(ctx, commandAddress, out var header))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            payloadAddress = (header & 0x3FFF_0000u) == 0x3FFF_0000u
                ? 0
                : commandAddress + 4;
        }

        if (!ctx.TryWriteUInt64(outputAddress, payloadAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (ShouldTraceHotPath(ref _packetPayloadTraceCount))
        {
            TraceAgc(
                $"agc.get_packet_payload out=0x{outputAddress:X16} cmd=0x{commandAddress:X16} " +
                $"type={type} payload=0x{payloadAddress:X16}");
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    #pragma warning restore SHEM004

    [SysAbiExport(
        Nid = "LtTouSCZjHM",
        ExportName = "sceAgcCbNop",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbNop(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dwordCount = (uint)ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 || dwordCount < 2 || dwordCount > 0x4001)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, dwordCount, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(dwordCount, ItNop, RZero)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 1; index < dwordCount; index++)
        {
            if (!TryWriteUInt32(ctx, commandAddress + ((ulong)index * sizeof(uint)), 0))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    // RenderThread/Subrender probe this before writing a NOP. Unresolved
    // GetSize returns NOT_FOUND and leaves command-buffer sizing broken.
    // CbNop rejects dwordCount < 2, so report that floor.
    [SysAbiExport(
        Nid = "t7PlZ9nt5Lc",
        ExportName = "sceAgcCbNopGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbNopGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "k3GhuSNmBLU",
        ExportName = "sceAgcCbDispatch",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbDispatch(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var groupCountX = (uint)ctx[CpuRegister.Rsi];
        var groupCountY = (uint)ctx[CpuRegister.Rdx];
        var groupCountZ = (uint)ctx[CpuRegister.Rcx];
        var modifier = (uint)ctx[CpuRegister.R8];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItDispatchDirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, groupCountX) ||
            !TryWriteUInt32(ctx, commandAddress + 8, groupCountY) ||
            !TryWriteUInt32(ctx, commandAddress + 12, groupCountZ) ||
            !TryWriteUInt32(ctx, commandAddress + 16, DirectDispatchInitiator(modifier)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    private static uint DirectDispatchInitiator(uint modifier) =>
        // AGC's direct API takes workgroup counts by default. Preserve the
        // caller's USE_THREAD_DIMENSIONS bit when explicitly requested; do not
        // force it. Demon's Souls' 0xF00100 dispatch is paired with a
        // 0x3C004000 element bound (exactly 64 lanes per group), proving the
        // default packet is group-dimensional.
        (modifier & 0xA038u) | 0x41u;

    [SysAbiExport(
        Nid = "JrtiDtKeS38",
        ExportName = "sceAgcAcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RAcbReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "KT-hTp-Ch14",
        ExportName = "sceAgcAcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var gcrControl = (uint)ctx[CpuRegister.Rsi];
        var baseAddress = ctx[CpuRegister.Rdx];
        var sizeBytes = ctx[CpuRegister.Rcx];
        var pollCycles = (uint)ctx[CpuRegister.R8];
        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0x8000_0000u) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    // Matches the fixed 8-dword ACQUIRE_MEM packet AcbAcquireMem writes above.
    [SysAbiExport(
        Nid = "ewobAQeMo5k",
        ExportName = "sceAgcAcbAcquireMemGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbAcquireMemGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "htn36gPnBk4",
        ExportName = "sceAgcAcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var address = ctx[CpuRegister.R8];
        var reference = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var pollCycles) ||
            commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 7u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)address & (size == 0 ? ~0x3u : ~0x7u)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32) & 0x3FFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }

        if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, EncodeWaitRegMem32Control(compareFunction, 0, cachePolicy)) ||
                !TryWriteUInt32(ctx, commandAddress + 24, EncodeWaitRegMemPoll(pollCycles)))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, EncodeWaitRegMem64Control(compareFunction, 0, cachePolicy)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, EncodeWaitRegMemPoll(pollCycles)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "eZ4+17OQz4Q",
        ExportName = "sceAgcAcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbWriteData(CpuContext ctx) =>
        DcbWriteData(ctx);

    [SysAbiExport(
        Nid = "j3EtxFkSIhQ",
        ExportName = "sceAgcAcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var argumentsAddress = ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)argumentsAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(argumentsAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "wr23dPKyWc0",
        ExportName = "sceAgcCbReleaseMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbReleaseMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var action = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var gcrControl = (uint)(ctx[CpuRegister.Rdx] & 0xFFFF);
        var destination = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var destinationAddress = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + 8, out var dataSelectionRaw) ||
            !TryReadUInt64(ctx, stackAddress + 16, out var data) ||
            !TryReadUInt64(ctx, stackAddress + 24, out var gdsOffsetRaw) ||
            !TryReadUInt64(ctx, stackAddress + 32, out var gdsSizeRaw) ||
            !TryReadUInt64(ctx, stackAddress + 40, out var interruptRaw) ||
            !TryReadUInt64(ctx, stackAddress + 48, out var interruptContextIdRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var dataSelection = (uint)(dataSelectionRaw & 0xFF);
        var gdsOffset = (uint)(gdsOffsetRaw & 0xFFFF);
        var gdsSize = (uint)(gdsSizeRaw & 0xFFFF);
        var interrupt = (uint)(interruptRaw & 0xFF);
        var interruptContextId = (uint)interruptContextIdRaw;
        if (commandBufferAddress == 0 ||
            destination > 1 ||
            dataSelection > 3 ||
            gdsOffset != 0 ||
            gdsSize > 2 ||
            interrupt > 6 ||
            (interrupt >= 4 && gcrControl != 0) ||
            (interrupt == 5 &&
             (destinationAddress == 0 || destinationAddress % sizeof(uint) != 0)) ||
            (interrupt == 6 &&
             (destinationAddress == 0 || destinationAddress % sizeof(ulong) != 0)))
        {
            return ReturnPointer(ctx, 0);
        }

        var packetAddressValue = interrupt == 4 ? 0UL : destinationAddress;
        var packetData = interrupt == 4 ? 0UL : data;

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RReleaseMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, action | (cachePolicy << 8)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 8,
                gcrControl | (dataSelection << 16) | (interrupt << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)packetAddressValue) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(packetAddressValue >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, (uint)packetData) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)(packetData >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 28, interruptContextId & 0x07FF_FFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_release_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"action=0x{action:X2} gcr=0x{gcrControl:X4} dst=0x{destinationAddress:X16} data_sel={dataSelection} data=0x{data:X16}");
        if (interrupt is 0 or 2 or 3 && dataSelection != 0)
        {
            TrackCbReleaseMemTarget(ctx, commandBufferAddress, destinationAddress);
        }
        RecordRingChunkWriter(commandAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "TRO721eVt4g",
        ExportName = "sceAgcDcbResetQueue",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbResetQueue(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var op = (uint)ctx[CpuRegister.Rsi];
        var state = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 || op != 0x3FF || state != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RDrawReset)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_reset_queue buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "rUuVjyR+Rd4",
        ExportName = "sceAgcDcbGetLodStatsGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStatsGetSize(CpuContext ctx)
    {
        var counterCount = (uint)ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = 0x10u + (counterCount * sizeof(uint));
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "vuSXe69VILM",
        ExportName = "sceAgcDcbGetLodStats",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbGetLodStats(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var cachePolicy = (uint)ctx[CpuRegister.Rsi] & 0x3u;
        var destinationAddress = ctx[CpuRegister.Rdx];
        var control = (uint)ctx[CpuRegister.Rcx];
        var counterMask = (uint)ctx[CpuRegister.R8] & 0xFFu;
        var resetCounters = (uint)ctx[CpuRegister.R9] & 0x1u;
        if (!TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var enableRaw) ||
            !TryReadUInt64(ctx, ctx[CpuRegister.Rsp] + (2 * sizeof(ulong)), out var counterSelectRaw) ||
            commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var enable = (uint)enableRaw & 0x1u;
        var counterSelect = (uint)counterSelectRaw & 0xFFu;
        var packetControl =
            (cachePolicy << 28) |
            (enable << 19) |
            (resetCounters << 18) |
            (counterMask << 10) |
            (counterSelect << 2);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, ItGetLodStats, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress & ~0x3Fu) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 16, packetControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_get_lod_stats buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"dst=0x{destinationAddress:X16} control=0x{control:X8} counters=0x{counterMask:X2}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "57labkp+rSQ",
        ExportName = "sceAgcDcbAcquireMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbAcquireMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var engine = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cbDbOp = (uint)ctx[CpuRegister.Rdx];
        var gcrControl = (uint)ctx[CpuRegister.Rcx];
        var baseAddress = ctx[CpuRegister.R8];
        var sizeBytes = ctx[CpuRegister.R9];
        if (!TryReadUInt32(ctx, ctx[CpuRegister.Rsp] + sizeof(ulong), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        var noSize = sizeBytes == ulong.MaxValue;
        if (commandBufferAddress == 0 ||
            engine > 1 ||
            (!noSize && (sizeBytes & 0xFF) != 0) ||
            (!noSize && (sizeBytes >> 40) != 0) ||
            (baseAddress & 0xFF) != 0 ||
            (baseAddress >> 40) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 8, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(8, ItNop, RAcquireMem)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (engine << 31) | cbDbOp) ||
            !TryWriteUInt32(ctx, commandAddress + 8, noSize ? 0 : (uint)(sizeBytes >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 16, (uint)(baseAddress >> 8)) ||
            !TryWriteUInt32(ctx, commandAddress + 20, 0) ||
            !TryWriteUInt32(ctx, commandAddress + 24, pollCycles / 40) ||
            !TryWriteUInt32(ctx, commandAddress + 28, gcrControl))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_acquire_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"engine={engine} cbdb=0x{cbDbOp:X8} gcr=0x{gcrControl:X8} base=0x{baseAddress:X16} size=0x{sizeBytes:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    // Matches the fixed 8-dword ACQUIRE_MEM packet DcbAcquireMem writes above.
    [SysAbiExport(
        Nid = "-vnlTPPXPrw",
        ExportName = "sceAgcDcbAcquireMemGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbAcquireMemGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 8u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "i1jyy49AjXU",
        ExportName = "sceAgcDcbWriteData",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWriteData(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var destination = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var destinationAddress = ctx[CpuRegister.Rcx];
        var dataAddress = ctx[CpuRegister.R8];
        var dwordCount = (uint)ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var incrementRaw) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var writeConfirmRaw))
        {
            return ReturnPointer(ctx, 0);
        }

        var increment = (uint)(incrementRaw & 0xFF);
        var writeConfirm = (uint)(writeConfirmRaw & 0xFF);
        if (commandBufferAddress == 0 ||
            destinationAddress == 0 ||
            dataAddress == 0 ||
            dwordCount > 0x3FFD)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = dwordCount + 4;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RWriteData)) ||
            !TryWriteUInt32(
                ctx,
                commandAddress + 4,
                destination | (cachePolicy << 8) | (increment << 16) | (writeConfirm << 24)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)destinationAddress) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(destinationAddress >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < dwordCount; index++)
        {
            if (!TryReadUInt32(ctx, dataAddress + ((ulong)index * sizeof(uint)), out var value) ||
                !TryWriteUInt32(ctx, commandAddress + 16 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        if (ShouldTraceHotPath(ref _dcbWriteDataTraceCount))
        {
            TraceAgc(
                $"agc.dcb_write_data buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"dst={destination} cache={cachePolicy} addr=0x{destinationAddress:X16} count={dwordCount} " +
                $"increment={increment} confirm={writeConfirm}");
        }

        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "FuVbkyKlf+s",
        ExportName = "sceAgcCbCondWriteGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbCondWriteGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 9u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "7toV+elXqNM",
        ExportName = "sceAgcCbCondWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbCondWrite(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var compareFunction = (uint)ctx[CpuRegister.Rsi];
        var writeSpace = (uint)ctx[CpuRegister.Rdx];
        var writeAddress = ctx[CpuRegister.Rcx];
        var writeValue = (uint)ctx[CpuRegister.R8];
        var readAddress = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt32(ctx, stackAddress + sizeof(ulong), out var reference) ||
            !TryReadUInt32(ctx, stackAddress + (2 * sizeof(ulong)), out var mask) ||
            commandBufferAddress == 0 ||
            compareFunction > 6 ||
            writeSpace is not 1 and not 2 ||
            readAddress == 0 ||
            (writeSpace == 1 &&
             (writeAddress == 0 || writeAddress % sizeof(uint) != 0)) ||
            readAddress % sizeof(uint) != 0)
        {
            return ReturnPointer(ctx, 0);
        }

        var control = 0x10u |
                      compareFunction |
                      (writeSpace == 1 ? 0x100u : 0u);
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 9, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(9, ItCondWrite, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, control) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)readAddress & ~0x3u) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(readAddress >> 32) & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 16, reference) ||
            !TryWriteUInt32(ctx, commandAddress + 20, mask) ||
            !TryWriteUInt32(ctx, commandAddress + 24, (uint)writeAddress & ~0x3u) ||
            !TryWriteUInt32(ctx, commandAddress + 28, (uint)(writeAddress >> 32) & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 32, writeValue))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.cb_cond_write buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} compare={compareFunction} " +
            $"space={writeSpace} read=0x{readAddress:X16} " +
            $"ref=0x{reference:X8} mask=0x{mask:X8} " +
            $"write=0x{writeAddress:X16} data=0x{writeValue:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "VmW0Tdpy420",
        ExportName = "sceAgcDcbWaitRegMem",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitRegMem(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var compareFunction = (uint)(ctx[CpuRegister.Rdx] & 0xFF);
        var operation = (uint)(ctx[CpuRegister.Rcx] & 0xFF);
        var cachePolicy = (uint)(ctx[CpuRegister.R8] & 0xFF);
        var address = ctx[CpuRegister.R9];
        var stackAddress = ctx[CpuRegister.Rsp];
        if (!TryReadUInt64(ctx, stackAddress + sizeof(ulong), out var reference) ||
            !TryReadUInt64(ctx, stackAddress + (2 * sizeof(ulong)), out var mask) ||
            !TryReadUInt32(ctx, stackAddress + (3 * sizeof(ulong)), out var pollCycles))
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 ||
            size > 1 ||
            compareFunction > 7 ||
            !IsValidWaitOperation(operation) ||
            cachePolicy > 3)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = size == 0 ? 7u : 9u;
        var packetRegister = size == 0 ? RWaitMem32 : RWaitMem64;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, packetRegister)) ||
                 !TryWriteUInt32(ctx, commandAddress + 4, (uint)address & (size == 0 ? ~0x3u : ~0x7u)) ||
                 !TryWriteUInt32(ctx, commandAddress + 8, (uint)(address >> 32) & 0x3FFFFu) ||
                 !TryWriteUInt32(ctx, commandAddress + 12, (uint)mask))
        {
            return ReturnPointer(ctx, 0);
        }
        else if (size == 0)
        {
            if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)reference) ||
                !TryWriteUInt32(ctx, commandAddress + 20, EncodeWaitRegMem32Control(compareFunction, operation, cachePolicy)) ||
                !TryWriteUInt32(ctx, commandAddress + 24, EncodeWaitRegMemPoll(pollCycles)))
            {
                return ReturnPointer(ctx, 0);
            }
        }
        else if (!TryWriteUInt32(ctx, commandAddress + 16, (uint)(mask >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 20, (uint)reference) ||
                 !TryWriteUInt32(ctx, commandAddress + 24, (uint)(reference >> 32)) ||
                 !TryWriteUInt32(ctx, commandAddress + 28, EncodeWaitRegMem64Control(compareFunction, operation, cachePolicy)) ||
                 !TryWriteUInt32(ctx, commandAddress + 32, EncodeWaitRegMemPoll(pollCycles)))
        {
            return ReturnPointer(ctx, 0);
        }

        if (ShouldTraceHotPath(ref _dcbWaitRegMemTraceCount))
        {
            TraceAgc(
                $"agc.dcb_wait_reg_mem buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
                $"size={size} compare={compareFunction} op={operation} cache={cachePolicy} " +
                $"addr=0x{address:X16} ref=0x{reference:X16} mask=0x{mask:X16} poll={pollCycles}");
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "43WJ08sSugE",
        ExportName = "sceAgcDcbWaitOnAddressGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbWaitOnAddressGetSize(CpuContext ctx)
    {
        var size = (uint)ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = size switch
        {
            0 => 14u * sizeof(uint),
            1 => 16u * sizeof(uint),
            _ => 0,
        };
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "u2T2DiA5hRI",
        ExportName = "sceAgcDcbStallCommandBufferParser",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParser(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var size = (uint)(ctx[CpuRegister.Rsi] & 0xFF);
        var address = ctx[CpuRegister.Rdx];
        var reference = ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || size > 1 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        // Direct execution submits work synchronously, so there is no independent
        // hardware command processor to stall. Keep a well-formed no-op in the DCB
        // so packet addresses and the command-buffer cursor remain coherent.
        TraceAgc(
            $"agc.dcb_stall_parser buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} " +
            $"size={size} addr=0x{address:X16} reference=0x{reference:X16}");
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+u6dKSLWM2o",
        ExportName = "sceAgcDcbStallCommandBufferParserGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbStallCommandBufferParserGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 2u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "RmaJwLtc8rY",
        ExportName = "sceAgcDcbSetBaseIndirectArgs",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetBaseIndirectArgs(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var baseIndex = (uint)ctx[CpuRegister.Rsi];
        var address = ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 4, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(4, ItSetBase, 0) | (baseIndex << 1)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 1) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)address & ~7u) ||
            !TryWriteUInt32(ctx, commandAddress + 12, (uint)(address >> 32)))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "CtB+A9-VxO0",
        ExportName = "sceAgcDcbDispatchIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbDispatchIndirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var dataOffset = (uint)ctx[CpuRegister.Rsi];
        var modifier = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 3, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(3, ItDispatchIndirect, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, dataOffset) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (modifier & 0xA038u) | 0x41u))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "+kSrjIVxKFE",
        ExportName = "sceAgcDcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPushMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var markerAddress = ctx[CpuRegister.Rsi];
        if (commandBufferAddress == 0 ||
            !TryReadGuestCString(ctx, markerAddress, 4095, out var marker))
        {
            return ReturnPointer(ctx, 0);
        }

        var payloadDwords = Math.Max(((uint)marker.Length + 4) / 4, 1);
        var packetDwords = payloadDwords + 1;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItNop, RPushMarker)))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint index = 0; index < payloadDwords; index++)
        {
            uint value = 0;
            for (uint byteIndex = 0; byteIndex < sizeof(uint); byteIndex++)
            {
                var markerIndex = (index * sizeof(uint)) + byteIndex;
                if (markerIndex < (uint)marker.Length)
                {
                    value |= (uint)marker[(int)markerIndex] << ((int)byteIndex * 8);
                }
            }

            if (!TryWriteUInt32(ctx, commandAddress + 4 + ((ulong)index * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "cpCILPya5Zk",
        ExportName = "sceAgcAcbPushMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPushMarker(CpuContext ctx) => DcbPushMarker(ctx);

    [SysAbiExport(
        Nid = "H7uZqCoNuWk",
        ExportName = "sceAgcDcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbPopMarker(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        if (commandBufferAddress == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(2, ItNop, RPopMarker)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, 0))
        {
            return ReturnPointer(ctx, 0);
        }

        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "6mFxkVqdmbQ",
        ExportName = "sceAgcAcbPopMarker",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int AcbPopMarker(CpuContext ctx) => DcbPopMarker(ctx);
}
