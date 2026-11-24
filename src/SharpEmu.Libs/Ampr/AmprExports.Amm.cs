// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ampr;

// AMM, the AMPR memory manager. A title gives AMM direct memory and maps it into the AMM area with
// command buffers. The commands run when their buffer is submitted, in buffer order with the buffer's
// other commands. Auto memory is handed out by page, so one map can take several physical runs.
public static partial class AmprExports
{
    // The AMM area lies inside the guest user range that the GPU trackers cover. A title sizes its own
    // allocator from the range it is given.
    private const ulong AmmAreaStart = 0x80_0000_0000UL;
    private const ulong AmmAreaEnd = 0xFC_0000_0000UL;
    private const ulong AmmPageSize = 0x4000;
    private const ulong AmmGiveGranularity = 0x20_0000;
    private const ulong AmmRecordSize = 0x20;
    private const uint AmmMapRecordType = 1;
    private const uint AmmUnmapRecordType = 2;
    private const int AmmUsageDirect = 0;
    private const int AmmUsageAuto = 1;
    private const ulong AmmMapFixed = 0x10;
    private const int KernelEperm = unchecked((int)0x80020001);
    private const int KernelEfault = unchecked((int)0x8002000E);
    private const int KernelEbusy = unchecked((int)0x80020010);
    private const int KernelEinval = unchecked((int)0x80020016);
    private const int KernelEagain = unchecked((int)0x80020023);
    private static readonly object _ammGate = new();
    // Free auto pages and the mapped runs, both sorted by start.
    private static readonly List<(ulong Offset, ulong Length)> _ammFreePages = [];
    private static readonly List<(ulong Address, ulong Length, ulong Offset, bool Auto)> _ammMappings = [];
    private static bool _ammAreaReserved;
    private static ulong _ammAddressSlot;

    private sealed class AmmCommand
    {
        public ulong RecordOffset;
        public bool Unmap;
        public ulong Address;
        public ulong Size;
        public int Protection;
        // Set for MapDirect: the title names the direct memory offset instead of taking auto pages.
        public ulong? DirectOffset;
    }

    [SysAbiExport(
        Nid = "wkQR9+xTFKY",
        ExportName = "sceAmprAmmGetVirtualAddressRanges",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmGetVirtualAddressRanges(CpuContext ctx)
    {
        // The multimap range sits at the end of the area and is empty, as it is by default.
        var ranges = ctx[CpuRegister.Rdi];
        if (ranges != 0)
        {
            _ = ctx.TryWriteUInt64(ranges, AmmAreaStart) &&
                ctx.TryWriteUInt64(ranges + 0x08, AmmAreaEnd) &&
                ctx.TryWriteUInt64(ranges + 0x10, AmmAreaEnd) &&
                ctx.TryWriteUInt64(ranges + 0x18, AmmAreaEnd);
        }

        _ = TryReserveAmmArea(ctx);
        return SetAmmResult(ctx, 0);
    }

    [SysAbiExport(
        Nid = "Q07J7XpvhrU",
        ExportName = "sceAmprAmmGiveDirectMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmGiveDirectMemory(CpuContext ctx)
    {
        var searchStart = ctx[CpuRegister.Rdi];
        var searchEnd = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];
        var alignment = ctx[CpuRegister.Rcx];
        var usage = unchecked((int)ctx[CpuRegister.R8]);
        var offsetOut = ctx[CpuRegister.R9];
        if (size == 0 || size % AmmGiveGranularity != 0 || alignment % AmmGiveGranularity != 0 ||
            usage is not (AmmUsageDirect or AmmUsageAuto))
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        if (offsetOut == 0)
        {
            return SetAmmResult(ctx, KernelEfault);
        }

        if (!TryReserveAmmArea(ctx))
        {
            return SetAmmResult(ctx, KernelEbusy);
        }

        // The pages come out of the title's direct memory.
        var result = KernelMemoryCompatExports.InvokeKernelMemoryOperation(
            ctx,
            KernelMemoryCompatExports.KernelAllocateDirectMemory,
            searchStart,
            searchEnd,
            size,
            alignment == 0 ? AmmGiveGranularity : alignment,
            0,
            offsetOut);
        if (result != 0)
        {
            return SetAmmResult(ctx, result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN ? KernelEagain : result);
        }

        if (!ctx.TryReadUInt64(offsetOut, out var offset))
        {
            return SetAmmResult(ctx, KernelEfault);
        }

        if (usage == AmmUsageAuto)
        {
            lock (_ammGate)
            {
                ReleaseAmmPagesLocked(offset, size);
            }
        }

        TraceAmpr(ctx, "amm_give", offset, size, (ulong)usage);
        return SetAmmResult(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JEVYGhDc97M",
        ExportName = "sceAmprAmmCommandBufferMap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferMap(CpuContext ctx) =>
        AppendAmmCommand(ctx, unmap: false, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], unchecked((int)ctx[CpuRegister.R8]));

    [SysAbiExport(
        Nid = "8TBE+9XCZbI",
        ExportName = "sceAmprAmmCommandBufferMapDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferMapDirect(CpuContext ctx) =>
        AppendAmmMapDirect(ctx);

    [SysAbiExport(
        Nid = "kOfZlhbVAkc",
        ExportName = "sceAmprAmmCommandBufferMapDirectWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferMapDirectWithGpuMaskId(CpuContext ctx) =>
        AppendAmmMapDirect(ctx);

    // The AMM buffer object is plain memory the title sets up with SetBuffer; only stale host state goes.
    [SysAbiExport(
        Nid = "EDq5bqCqYpA",
        ExportName = "sceAmprAmmCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferConstructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        RemoveCommandBufferState(commandBuffer);
        TraceAmpr(ctx, "amm_ctor", commandBuffer, 0, 0);
        return SetAmmResult(ctx, 0);
    }

    [SysAbiExport(
        Nid = "pvUFDOHilnE",
        ExportName = "sceAmprAmmCommandBufferDestructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferDestructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer != 0)
        {
            RemoveCommandBufferState(commandBuffer);
        }

        TraceAmpr(ctx, "amm_dtor", commandBuffer, 0, 0);
        return SetAmmResult(ctx, 0);
    }

    [SysAbiExport(
        Nid = "M-VFI2DJWQA",
        ExportName = "sceAmprAmmCommandBufferUnmap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferUnmap(CpuContext ctx) =>
        AppendAmmCommand(ctx, unmap: true, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx], 0);

    [SysAbiExport(
        Nid = "lwS-7y3jcBI",
        ExportName = "sceAmprAmmSubmitCommandBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmSubmitCommandBuffer(CpuContext ctx)
    {
        // The entry takes the buffer the commands were written to, the bytes written and the priority.
        var commandBuffer = ctx[CpuRegister.Rdi];
        var priority = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (commandBuffer == 0 || priority > 2)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        var result = SubmitCommandBuffer(
            ctx,
            commandBuffer,
            AmprEngine.Amm,
            priority,
            (_, executionResult, errorOffset) => ReportAmmFailure(commandBuffer, executionResult, errorOffset),
            out var submission);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return SetAmmResult(ctx, result);
        }

        if (submission!.CompletedInline)
        {
            ReportAmmFailure(commandBuffer, submission.ExecutionResult, submission.ErrorOffset);
        }

        return SetAmmResult(ctx, 0);
    }

    // On hardware a failed command ends the application; here it is reported and the buffer stops.
    private static void ReportAmmFailure(ulong commandBuffer, int executionResult, uint errorOffset)
    {
        if (executionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] ampr.amm command failed: buffer=0x{commandBuffer:X16} offset=0x{errorOffset:X} result=0x{executionResult:X8}");
        }
    }

    // MapDirect takes (buffer, va, direct memory offset, size, memory type, protection).
    private static int AppendAmmMapDirect(CpuContext ctx)
    {
        var offset = ctx[CpuRegister.Rdx];
        if (offset % AmmPageSize != 0)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        return AppendAmmCommand(ctx, unmap: false, ctx[CpuRegister.Rsi], ctx[CpuRegister.Rcx], unchecked((int)ctx[CpuRegister.R9]), offset);
    }

    private static int AppendAmmCommand(CpuContext ctx, bool unmap, ulong address, ulong size, int protection, ulong? directOffset = null)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0 || size == 0 || address % AmmPageSize != 0 || size % AmmPageSize != 0 ||
            address < AmmAreaStart || address >= AmmAreaEnd || size > AmmAreaEnd - address)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out var buffer, out _, out var state) || state is null)
        {
            return SetAmmResult(ctx, KernelEfault);
        }

        if (buffer == 0)
        {
            return SetAmmResult(ctx, KernelEperm);
        }

        Span<byte> record = stackalloc byte[(int)AmmRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record, unmap ? AmmUnmapRecordType : AmmMapRecordType);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x04..], unchecked((uint)protection));
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], size);
        ulong recordOffset;
        lock (state)
        {
            recordOffset = state.WriteOffset;
        }

        if (!AppendCommandBufferRecord(ctx, commandBuffer, record))
        {
            return SetAmmResult(ctx, KernelEbusy);
        }

        lock (state)
        {
            state.AmmCommands.Add(new AmmCommand
            {
                RecordOffset = recordOffset,
                Unmap = unmap,
                Address = address,
                Size = size,
                Protection = protection,
                DirectOffset = directOffset,
            });
        }

        TraceAmpr(ctx, unmap ? "amm_unmap" : "amm_map", commandBuffer, address, size);
        return SetAmmResult(ctx, 0);
    }

    private static int ExecuteAmmCommand(CpuContext ctx, AmmCommand command)
    {
        lock (_ammGate)
        {
            if (!TryReserveAmmArea(ctx))
            {
                return KernelEbusy;
            }

            // A map over mapped pages replaces them, so both commands first return the range to the area.
            var result = ReleaseAmmRangeLocked(ctx, command.Address, command.Size);
            if (result != 0 || command.Unmap)
            {
                return result;
            }

            var auto = command.DirectOffset is null;
            List<(ulong Offset, ulong Length)> runs;
            if (auto)
            {
                if (!TryTakeAmmPagesLocked(command.Size, out runs))
                {
                    return KernelEagain;
                }
            }
            else
            {
                runs = [(command.DirectOffset!.Value, command.Size)];
            }

            var address = command.Address;
            for (var i = 0; i < runs.Count; i++)
            {
                var (offset, length) = runs[i];
                result = ctx.TryWriteUInt64(_ammAddressSlot, address)
                    ? KernelMemoryCompatExports.InvokeKernelMemoryOperation(
                        ctx,
                        KernelMemoryCompatExports.KernelMapDirectMemory,
                        _ammAddressSlot,
                        length,
                        unchecked((ulong)(uint)command.Protection),
                        AmmMapFixed,
                        offset,
                        0)
                    : KernelEfault;
                if (result != 0)
                {
                    for (var j = i; auto && j < runs.Count; j++)
                    {
                        ReleaseAmmPagesLocked(runs[j].Offset, runs[j].Length);
                    }

                    _ = ReleaseAmmRangeLocked(ctx, command.Address, address - command.Address);
                    return result;
                }

                InsertAmmMappingLocked(address, length, offset, auto);
                address += length;
            }

            return 0;
        }
    }

    // The range becomes reserved AMM area again and its pages go back to the pool.
    private static int ReleaseAmmRangeLocked(CpuContext ctx, ulong address, ulong size)
    {
        if (size == 0)
        {
            return 0;
        }

        var end = address + size;
        var first = _ammMappings.FindIndex(mapping => mapping.Address + mapping.Length > address);
        if (first < 0 || _ammMappings[first].Address >= end)
        {
            return 0;
        }

        var result = ctx.TryWriteUInt64(_ammAddressSlot, address)
            ? KernelMemoryCompatExports.ReserveBackingRange(ctx, _ammAddressSlot, size, AmmMapFixed, 0)
            : KernelEfault;
        if (result != 0)
        {
            return result;
        }

        while (first < _ammMappings.Count && _ammMappings[first].Address < end)
        {
            var (mappedAddress, length, offset, auto) = _ammMappings[first];
            _ammMappings.RemoveAt(first);
            var mappedEnd = mappedAddress + length;
            var cutStart = Math.Max(mappedAddress, address);
            var cutEnd = Math.Min(mappedEnd, end);
            if (auto)
            {
                ReleaseAmmPagesLocked(offset + (cutStart - mappedAddress), cutEnd - cutStart);
            }

            if (mappedAddress < cutStart)
            {
                _ammMappings.Insert(first++, (mappedAddress, cutStart - mappedAddress, offset, auto));
            }

            if (cutEnd < mappedEnd)
            {
                _ammMappings.Insert(first++, (cutEnd, mappedEnd - cutEnd, offset + (cutEnd - mappedAddress), auto));
            }
        }

        return 0;
    }

    private static void InsertAmmMappingLocked(ulong address, ulong length, ulong offset, bool auto)
    {
        var index = _ammMappings.FindIndex(mapping => mapping.Address > address);
        _ammMappings.Insert(index < 0 ? _ammMappings.Count : index, (address, length, offset, auto));
    }

    private static bool TryTakeAmmPagesLocked(ulong size, out List<(ulong Offset, ulong Length)> runs)
    {
        runs = [];
        var remaining = size;
        for (var i = 0; i < _ammFreePages.Count && remaining != 0; i++)
        {
            runs.Add((_ammFreePages[i].Offset, Math.Min(_ammFreePages[i].Length, remaining)));
            remaining -= runs[^1].Length;
        }

        if (remaining != 0)
        {
            runs.Clear();
            return false;
        }

        foreach (var (_, length) in runs)
        {
            var (freeOffset, freeLength) = _ammFreePages[0];
            if (length == freeLength)
            {
                _ammFreePages.RemoveAt(0);
            }
            else
            {
                _ammFreePages[0] = (freeOffset + length, freeLength - length);
            }
        }

        return true;
    }

    private static void ReleaseAmmPagesLocked(ulong offset, ulong length)
    {
        if (length == 0)
        {
            return;
        }

        var index = _ammFreePages.FindIndex(run => run.Offset > offset);
        if (index < 0)
        {
            index = _ammFreePages.Count;
        }

        _ammFreePages.Insert(index, (offset, length));
        if (index + 1 < _ammFreePages.Count && offset + length == _ammFreePages[index + 1].Offset)
        {
            _ammFreePages[index] = (offset, length + _ammFreePages[index + 1].Length);
            _ammFreePages.RemoveAt(index + 1);
        }

        if (index > 0 && _ammFreePages[index - 1].Offset + _ammFreePages[index - 1].Length == offset)
        {
            _ammFreePages[index - 1] = (_ammFreePages[index - 1].Offset, _ammFreePages[index - 1].Length + _ammFreePages[index].Length);
            _ammFreePages.RemoveAt(index);
        }
    }

    // The area is reserved at the first AMM call, before the title maps anything into it.
    // The AMM area is GPU memory a title maps piece by piece: a streamed texture's descriptor covers its whole mip
    // chain while only the resident levels are mapped, and its minimum LOD keeps sampling on those. The pages of the
    // area that are not mapped read as zeros, as in a PRT aperture.
    internal static bool ContainsAmmRange(ulong address, ulong size) =>
        Volatile.Read(ref _ammAreaReserved) && size != 0 && address >= AmmAreaStart && address < AmmAreaEnd &&
        size <= AmmAreaEnd - address;

    private static bool TryReserveAmmArea(CpuContext ctx)
    {
        lock (_ammGate)
        {
            if (_ammAreaReserved)
            {
                return true;
            }

            if (_ammAddressSlot == 0 &&
                !KernelMemoryCompatExports.TryAllocateHleData(ctx, sizeof(ulong), sizeof(ulong), out _ammAddressSlot))
            {
                return false;
            }

            var result = ctx.TryWriteUInt64(_ammAddressSlot, AmmAreaStart)
                ? KernelMemoryCompatExports.ReserveBackingRange(ctx, _ammAddressSlot, AmmAreaEnd - AmmAreaStart, AmmMapFixed, 0)
                : KernelEfault;
            if (result != 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] ampr.amm area reservation failed: 0x{AmmAreaStart:X16}+0x{AmmAreaEnd - AmmAreaStart:X} result=0x{result:X8}");
                return false;
            }

            _ammAreaReserved = true;
            return true;
        }
    }

    private static int SetAmmResult(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }
}
