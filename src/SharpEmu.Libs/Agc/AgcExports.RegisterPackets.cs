// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // This partial constructs AGC register-programming command packets.

    private const uint CbSetShRegisterRangeMarker = 0x6875000D;
    private static readonly ConcurrentDictionary<(ulong Table, uint Pair), byte>
        _tracedSuspiciousIndirectDepthEntries = new();
    private static readonly ulong? _traceIndirectCxHash = ParseIndirectCxTraceHash();
    private static int _indirectCxPairDumpCount;

    [SysAbiExport(
        Nid = "UZbQjYAwwXM",
        ExportName = "sceAgcCbSetShRegistersDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegistersDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (registerCount == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        if (commandBufferAddress == 0 || registersAddress == 0 || registerCount > 4096)
        {
            return ReturnPointer(ctx, 0);
        }

        var registers = new RegisterDefaultValue[registerCount];
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var offset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return ReturnPointer(ctx, 0);
            }

            registers[index] = new RegisterDefaultValue(offset, value);
        }

        Array.Sort(registers, static (left, right) => left.Offset.CompareTo(right.Offset));
        ulong firstCommandAddress = 0;
        var startIndex = 0;
        while (startIndex < registers.Length)
        {
            var endIndex = startIndex + 1;
            while (endIndex < registers.Length &&
                   registers[endIndex].Offset == registers[endIndex - 1].Offset + 1)
            {
                endIndex++;
            }

            var valueCount = (uint)(endIndex - startIndex);
            var packetDwords = valueCount + 2;
            if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
                !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
                !TryWriteUInt32(ctx, commandAddress + 4, registers[startIndex].Offset & 0xFFFFu))
            {
                return ReturnPointer(ctx, 0);
            }

            firstCommandAddress = firstCommandAddress == 0 ? commandAddress : firstCommandAddress;
            for (var index = startIndex; index < endIndex; index++)
            {
                if (!TryWriteUInt32(
                        ctx,
                        commandAddress + 8 + ((ulong)(index - startIndex) * sizeof(uint)),
                        registers[index].Value))
                {
                    return ReturnPointer(ctx, 0);
                }
            }

            startIndex = endIndex;
        }

        return ReturnPointer(ctx, firstCommandAddress);
    }

    [SysAbiExport(
        Nid = "n2fD4A+pb+g",
        ExportName = "sceAgcCbSetShRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirect(CpuContext ctx)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 ||
            offset == 0 ||
            offset > 0x3FF ||
            !TryGetCbSetShRegisterRangeDirectLayout(valueCount, out var packetDwords, out _))
        {
            return ReturnPointer(ctx, 0);
        }

        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, 2, out var markerAddress) ||
            !TryWriteUInt32(ctx, markerAddress, Pm4(2, ItNop, RZero)) ||
            !TryWriteUInt32(ctx, markerAddress + 4, CbSetShRegisterRangeMarker) ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, ItSetShReg, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc($"agc.cb_set_sh_range buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} offset=0x{offset:X8} count={valueCount}");
        RefreshBuilderArenaCursorPassive(ctx, commandBufferAddress);
        return ReturnPointer(ctx, commandAddress);
    }

    [SysAbiExport(
        Nid = "bxGoVxpdSPQ",
        ExportName = "sceAgcCbSetShRegisterRangeDirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int CbSetShRegisterRangeDirectGetSize(CpuContext ctx)
    {
        var valueCount = (uint)ctx[CpuRegister.Rdi];
        if (!TryGetCbSetShRegisterRangeDirectLayout(valueCount, out _, out var sizeBytes))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        ctx[CpuRegister.Rax] = sizeBytes;
        return (int)sizeBytes;
    }

    private static bool TryGetCbSetShRegisterRangeDirectLayout(
        uint valueCount,
        out uint packetDwords,
        out uint sizeBytes)
    {
        // The PM4 count field can encode at most 0x4001 dwords. This packet
        // uses two header dwords. The SharpEmu marker uses two more dwords.
        if (valueCount == 0 || valueCount > 0x3FFF)
        {
            packetDwords = 0;
            sizeBytes = 0;
            return false;
        }

        packetDwords = valueCount + 2;
        sizeBytes = (valueCount + 4) * sizeof(uint);
        return true;
    }

    [SysAbiExport(
        Nid = "ZvwO9euwYzc",
        ExportName = "sceAgcDcbSetCxRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RCxRegsIndirect, "cx");

    [SysAbiExport(
        Nid = "-HOOCn0JY48",
        ExportName = "sceAgcDcbSetShRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RShRegsIndirect, "sh");

    [SysAbiExport(
        Nid = "pFLArOT53+w",
        ExportName = "sceAgcDcbSetShRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegisterDirect(CpuContext ctx) =>
        DcbSetRegisterDirect(ctx, ItSetShReg, "sh");

    [SysAbiExport(
        Nid = "QhPDD513V0w",
        ExportName = "sceAgcDcbSetShRegisterDirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetShRegisterDirectGetSize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 3u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    [SysAbiExport(
        Nid = "hvUfkUIQcOE",
        ExportName = "sceAgcDcbSetUcRegistersIndirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegistersIndirect(CpuContext ctx) =>
        DcbSetRegistersIndirect(ctx, RUcRegsIndirect, "uc");

    [SysAbiExport(
        Nid = "w4-d0n60hdo",
        ExportName = "sceAgcDcbSetUcRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegisterDirect(CpuContext ctx) =>
        DcbSetRegisterDirect(ctx, ItSetUconfigReg, "uc");

    [SysAbiExport(
        Nid = "aP1Ki9G3++4",
        ExportName = "sceAgcDcbSetUcRegisterDirectGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetUcRegisterDirectGetSize(CpuContext ctx)
    {
        // SET_UCONFIG_REG header + offset + value.
        ctx[CpuRegister.Rax] = 3u * sizeof(uint);
        return (int)ctx[CpuRegister.Rax];
    }

    private static int DcbSetRegistersIndirect(CpuContext ctx, uint packetRegister, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var registersAddress = ctx[CpuRegister.Rsi];
        var registerCount = (uint)ctx[CpuRegister.Rdx];
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        TraceIndirectDepthTable(
            ctx,
            "emit",
            packetRegister,
            commandBufferAddress,
            registersAddress,
            registerCount);

        var op = packetRegister switch
        {
            RCxRegsIndirect => ItSetContextRegIndirect,
            RShRegsIndirect => ItSetShRegIndirect,
            RUcRegsIndirect => ItSetUconfigRegIndirect,
            _ => 0u,
        };
        if (op == 0 ||
            !TryAllocateCommandDwords(ctx, commandBufferAddress, 5, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(5, op, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, (uint)(registersAddress & 0xFFFF_FFFCUL)) ||
            !TryWriteUInt32(ctx, commandAddress + 8, (uint)(registersAddress >> 32)) ||
            !TryWriteUInt32(ctx, commandAddress + 12, 0x8000_0000u) ||
            !TryWriteUInt32(ctx, commandAddress + 16, registerCount & 0x3FFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc($"agc.dcb_set_{registerSpace}_indirect buf=0x{commandBufferAddress:X16} cmd=0x{commandAddress:X16} regs=0x{registersAddress:X16} count={registerCount}");
        return ReturnPointer(ctx, commandAddress);
    }

    private static void TraceIndirectDepthTable(
        CpuContext ctx,
        string source,
        uint packetRegister,
        ulong commandBufferAddress,
        ulong registersAddress,
        uint registerCount)
    {
        if (!_traceDepthMetadata ||
            packetRegister != RCxRegsIndirect ||
            registersAddress == 0)
        {
            return;
        }

        var boundedCount = Math.Min(registerCount & 0x3FFFu, 4096u);
        var offsetHash = 14695981039346656037UL;
        var valueHash = 14695981039346656037UL;
        for (uint index = 0; index < boundedCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var rawOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return;
            }

            offsetHash = (offsetHash ^ rawOffset) * 1099511628211UL;
            valueHash = (valueHash ^ value) * 1099511628211UL;
        }

        if (_traceIndirectCxHash == offsetHash &&
            Interlocked.Increment(ref _indirectCxPairDumpCount) <= 16)
        {
            for (uint index = 0; index < boundedCount; index++)
            {
                var entryAddress = registersAddress + ((ulong)index * 8);
                if (!TryReadUInt32(ctx, entryAddress, out var rawOffset) ||
                    !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
                {
                    break;
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] agc.indirect_cx_pair source={source} " +
                    $"rip=0x{ctx.Rip:X16} table=0x{registersAddress:X16} " +
                    $"count={registerCount} offset_hash=0x{offsetHash:X16} " +
                    $"value_hash=0x{valueHash:X16} pair={index} " +
                    $"raw=0x{rawOffset:X8} reg=0x{(rawOffset & ~0x7000_0000u):X8} " +
                    $"value=0x{value:X8}");
            }
        }

        for (uint index = 0; index < boundedCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var rawOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                return;
            }

            var registerOffset = rawOffset & ~0x7000_0000u;
            if (registerOffset != DbDepthSizeXy)
            {
                continue;
            }

            var width = (value & 0x3FFFu) + 1;
            var height = ((value >> 16) & 0x3FFFu) + 1;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.indirect_depth_source source={source} " +
                $"rip=0x{ctx.Rip:X16} buf=0x{commandBufferAddress:X16} " +
                $"table=0x{registersAddress:X16} count={registerCount} pair={index} " +
                $"entry=0x{entryAddress:X16} raw=0x{rawOffset:X8} " +
                $"value=0x{value:X8} size={width}x{height} " +
                $"offset_hash=0x{offsetHash:X16} value_hash=0x{valueHash:X16}");

            if (value == 0x00000007u &&
                _tracedSuspiciousIndirectDepthEntries.TryAdd((registersAddress, index), 0))
            {
                TraceSuspiciousIndirectDepthEntry(
                    ctx,
                    source,
                    commandBufferAddress,
                    registersAddress,
                    registerCount,
                    index);
            }
        }
    }

    private static ulong? ParseIndirectCxTraceHash()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_INDIRECT_CX_HASH");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(
            span,
            System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture,
            out var hash)
            ? hash
            : null;
    }

    private static void TraceSuspiciousIndirectDepthEntry(
        CpuContext ctx,
        string source,
        ulong commandBufferAddress,
        ulong registersAddress,
        uint registerCount,
        uint pairIndex)
    {
        const uint radius = 4;
        var boundedCount = Math.Min(registerCount & 0x3FFFu, 4096u);
        var first = pairIndex > radius ? pairIndex - radius : 0;
        var last = Math.Min(boundedCount, pairIndex + radius + 1);
        var entries = new List<string>((int)(last - first));
        for (var index = first; index < last; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var rawOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out var value))
            {
                entries.Add($"{index}:unreadable");
                continue;
            }

            entries.Add($"{index}:0x{rawOffset:X8}=0x{value:X8}");
        }

        var returnAddress = TryReadUInt64(ctx, ctx[CpuRegister.Rsp], out var valueAtStack)
            ? $"0x{valueAtStack:X16}"
            : "unreadable";
        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.indirect_depth_provenance source={source} " +
            $"rip=0x{ctx.Rip:X16} ret={returnAddress} " +
            $"buf=0x{commandBufferAddress:X16} table=0x{registersAddress:X16} " +
            $"count={registerCount} pair={pairIndex} " +
            $"window=[{string.Join(',', entries)}]");
    }

    private static int DcbSetRegisterDirect(CpuContext ctx, uint op, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        // Uc/Cx/Sh register is passed by value as {u32 offset, u32 value} in RSI.
        var packedRegister = ctx[CpuRegister.Rsi];
        var registerOffset = (uint)(packedRegister & 0xFFFF_FFFFUL);
        var registerValue = (uint)(packedRegister >> 32);
        if (commandBufferAddress == 0)
        {
            return ReturnPointer(ctx, 0);
        }

        const uint packetDwords = 3;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, op, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, registerOffset & 0xFFFFu) ||
            !TryWriteUInt32(ctx, commandAddress + 8, registerValue))
        {
            return ReturnPointer(ctx, 0);
        }

        TraceAgc(
            $"agc.dcb_set_{registerSpace}_direct buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{registerOffset:X4} value=0x{registerValue:X8}");
        return ReturnPointer(ctx, commandAddress);
    }

    // Sony SetCf* range writer — SET_CONTEXT_REG packet (same shape as SH range).
    [SysAbiExport(
        Nid = "BVFg3CWU6Eo",
        ExportName = "sceAgcDcbSetCfRegisterRangeDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCfRegisterRangeDirect(CpuContext ctx) =>
        DcbSetRegisterRangeDirect(ctx, ItSetContextReg, "cf");

    // Logged unresolved as LHFXRrlTPD8 during North Yankton load.
    [SysAbiExport(
        Nid = "LHFXRrlTPD8",
        ExportName = "sceAgcDcbSetCxRegisterDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAgc")]
    public static int DcbSetCxRegisterDirect(CpuContext ctx) =>
        DcbSetRegisterDirect(ctx, ItSetContextReg, "cx");

    private static int DcbSetRegisterRangeDirect(CpuContext ctx, uint op, string registerSpace)
    {
        var commandBufferAddress = ctx[CpuRegister.Rdi];
        var offset = (uint)ctx[CpuRegister.Rsi];
        var valuesAddress = ctx[CpuRegister.Rdx];
        var valueCount = (uint)ctx[CpuRegister.Rcx];
        if (commandBufferAddress == 0 || valueCount == 0 || valueCount > 0x3FFE)
        {
            return ReturnPointer(ctx, 0);
        }

        var packetDwords = valueCount + 2;
        if (!TryAllocateCommandDwords(ctx, commandBufferAddress, packetDwords, out var commandAddress) ||
            !TryWriteUInt32(ctx, commandAddress, Pm4(packetDwords, op, 0)) ||
            !TryWriteUInt32(ctx, commandAddress + 4, offset & 0xFFFFu))
        {
            return ReturnPointer(ctx, 0);
        }

        for (uint i = 0; i < valueCount; i++)
        {
            var value = 0u;
            if (valuesAddress != 0 &&
                !TryReadUInt32(ctx, valuesAddress + (i * sizeof(uint)), out value))
            {
                return ReturnPointer(ctx, 0);
            }

            if (!TryWriteUInt32(ctx, commandAddress + 8 + (i * sizeof(uint)), value))
            {
                return ReturnPointer(ctx, 0);
            }
        }

        TraceAgc(
            $"agc.dcb_set_{registerSpace}_range buf=0x{commandBufferAddress:X16} " +
            $"cmd=0x{commandAddress:X16} offset=0x{offset:X4} count={valueCount}");
        return ReturnPointer(ctx, commandAddress);
    }
}
