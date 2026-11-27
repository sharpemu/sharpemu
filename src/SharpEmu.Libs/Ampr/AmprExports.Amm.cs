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
    private const ulong AmmAreaEnd = 0xC0_0000_0000UL;
    // The multimap range follows the AMM range, 256 GiB then 32 GiB as on AnyPS5. The top of the user range
    // stays free for the title, which reserves fixed ranges there (0xF000000000 and up).
    private const ulong AmmMultimapAreaEnd = 0xC8_0000_0000UL;
    private const ulong AmmPageSize = 0x4000;
    private const ulong AmmGiveGranularity = 0x20_0000;
    private const ulong AmmRecordSize = 0x20;
    private const int AmmUsageDirect = 0;
    private const int AmmUsageAuto = 1;
    private const ulong AmmMapFixed = 0x10;
    private const int KernelEperm = unchecked((int)0x80020001);
    private const int KernelEsrch = unchecked((int)0x80020003);
    private const int KernelEfault = unchecked((int)0x8002000E);
    private const int KernelEbusy = unchecked((int)0x80020010);
    private const int KernelEinval = unchecked((int)0x80020016);
    private const int KernelEagain = unchecked((int)0x80020023);
    private static readonly object _ammGate = new();
    // Free auto pages, the mapped runs and the PRT ranges, all sorted by start.
    private static readonly List<(ulong Offset, ulong Length)> _ammFreePages = [];
    private static readonly List<AmmMapping> _ammMappings = [];
    private static readonly List<(ulong Start, ulong End)> _ammPrt = [];
    // Auto pages held by more than one mapping (a multimap, or a remap in flight), by direct memory offset. A
    // page that is not listed has one holder, so the table stays empty unless a title aliases.
    private static readonly Dictionary<ulong, int> _ammPageHolders = [];
    private static bool _ammAreaReserved;
    private static ulong _ammAddressSlot;

    // One run of the AMM area backed by direct memory. Auto runs came from the pages given with usage auto and
    // go back to them when unmapped.
    private readonly record struct AmmMapping(ulong Address, ulong Length, ulong Offset, bool Auto, int Protection, int Type);

    // The value is the record type written to the command buffer. MapDirect records as Map.
    private enum AmmCommandKind
    {
        Map = 1,
        Unmap,
        Remap,
        MultiMap,
        ModifyProtect,
        ModifyMtypeProtect,
        MapAsPrt,
        AllocatePaForPrt,
        RemapIntoPrt,
        UnmapToPrt,
    }

    private sealed class AmmCommand
    {
        public ulong RecordOffset;
        public AmmCommandKind Kind;
        public ulong Address;
        public ulong Size;
        public int Protection;
        public int Type;
        // The protection bits a ModifyProtect command replaces.
        public int Mask;
        // The range a remap takes its pages from.
        public ulong Source;
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
        // Four output pointers: AMM start and end, multimap start and end. The multimap range follows the AMM range.
        _ = TryReserveAmmArea(ctx);
        var startPtr = ctx[CpuRegister.Rdi];
        var endPtr = ctx[CpuRegister.Rsi];
        var multimapStartPtr = ctx[CpuRegister.Rdx];
        var multimapEndPtr = ctx[CpuRegister.Rcx];
        if (startPtr == 0 || endPtr == 0 || multimapStartPtr == 0 || multimapEndPtr == 0)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        if (!(ctx.TryWriteUInt64(startPtr, AmmAreaStart) &&
              ctx.TryWriteUInt64(endPtr, AmmAreaEnd) &&
              ctx.TryWriteUInt64(multimapStartPtr, AmmAreaEnd) &&
              ctx.TryWriteUInt64(multimapEndPtr, AmmMultimapAreaEnd)))
        {
            return SetAmmResult(ctx, KernelEfault);
        }

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
        AppendAmmMap(ctx);

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
        AppendAmmRange(ctx, AmmCommandKind.Unmap);

    [SysAbiExport(
        Nid = "lwS-7y3jcBI",
        ExportName = "sceAmprAmmSubmitCommandBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmSubmitCommandBuffer(CpuContext ctx) =>
        SetAmmResult(ctx, SubmitAmmBuffer(ctx, out _));

    // The entry takes the buffer the commands were written to, the bytes written and the priority.
    private static int SubmitAmmBuffer(CpuContext ctx, out AmprSubmission? submission)
    {
        submission = null;
        var commandBuffer = ctx[CpuRegister.Rdi];
        var priority = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (commandBuffer == 0 || priority > 2)
        {
            return KernelEinval;
        }

        var result = SubmitCommandBuffer(
            ctx,
            commandBuffer,
            AmprEngine.Amm,
            priority,
            (_, executionResult, errorOffset) => ReportAmmFailure(commandBuffer, executionResult, errorOffset),
            out submission);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return result;
        }

        if (submission!.CompletedInline)
        {
            ReportAmmFailure(commandBuffer, submission.ExecutionResult, submission.ErrorOffset);
        }

        return 0;
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

        return AppendAmmCommand(ctx, new AmmCommand
        {
            Kind = AmmCommandKind.Map,
            Address = ctx[CpuRegister.Rsi],
            Size = ctx[CpuRegister.Rcx],
            Type = unchecked((int)ctx[CpuRegister.R8]),
            Protection = unchecked((int)ctx[CpuRegister.R9]),
            DirectOffset = offset,
        });
    }

    // Map takes (buffer, va, size, memory type, protection).
    private static int AppendAmmMap(CpuContext ctx) =>
        AppendAmmCommand(ctx, new AmmCommand
        {
            Kind = AmmCommandKind.Map,
            Address = ctx[CpuRegister.Rsi],
            Size = ctx[CpuRegister.Rdx],
            Type = unchecked((int)ctx[CpuRegister.Rcx]),
            Protection = unchecked((int)ctx[CpuRegister.R8]),
        });

    // Unmap, MapAsPrt and UnmapToPrt take (buffer, va, size).
    private static int AppendAmmRange(CpuContext ctx, AmmCommandKind kind) =>
        AppendAmmCommand(ctx, new AmmCommand
        {
            Kind = kind,
            Address = ctx[CpuRegister.Rsi],
            Size = ctx[CpuRegister.Rdx],
        });

    // The commands after Map and Unmap validate the protection they record, as the measure calls do. Map and
    // Unmap keep accepting any protection a title passes.
    private static int AppendAmmCommand(CpuContext ctx, AmmCommand command)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var hasProtection = command.Kind is not (AmmCommandKind.Map or AmmCommandKind.Unmap or
            AmmCommandKind.MapAsPrt or AmmCommandKind.UnmapToPrt);
        var hasMask = command.Kind is AmmCommandKind.ModifyProtect or AmmCommandKind.ModifyMtypeProtect;
        var hasSource = command.Kind is AmmCommandKind.Remap or AmmCommandKind.MultiMap or AmmCommandKind.RemapIntoPrt;
        if (commandBuffer == 0 || !IsAmmRange(command.Address, command.Size) ||
            (hasSource && !IsAmmRange(command.Source, command.Size)) ||
            (hasProtection && !IsValidAmmProtection(command.Protection)) ||
            (hasMask && !IsValidAmmProtection(command.Mask)))
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

        // The record carries the kind, protection, range and the source range or direct offset. A protect
        // command puts its mask and memory type in the last eight bytes instead.
        Span<byte> record = stackalloc byte[(int)AmmRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)command.Kind);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x04..], unchecked((uint)command.Protection));
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], command.Address);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], command.Size);
        if (hasMask)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record[0x18..], unchecked((uint)command.Mask));
            BinaryPrimitives.WriteUInt32LittleEndian(record[0x1C..], unchecked((uint)command.Type));
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], command.DirectOffset ?? command.Source);
        }

        lock (state)
        {
            command.RecordOffset = state.WriteOffset;
        }

        if (!AppendCommandBufferRecord(ctx, commandBuffer, record))
        {
            return SetAmmResult(ctx, KernelEbusy);
        }

        lock (state)
        {
            state.AmmCommands.Add(command);
        }

        TraceAmpr(ctx, "amm_" + command.Kind, commandBuffer, command.Address, command.Size);
        return SetAmmResult(ctx, 0);
    }

    private static bool IsAmmRange(ulong address, ulong size) =>
        size != 0 && address % AmmPageSize == 0 && size % AmmPageSize == 0 &&
        address >= AmmAreaStart && address < AmmAreaEnd && size <= AmmAreaEnd - address;

    // The AMM protection bits: CPU and GPU read and write, and the extra access bits at 0x40 to 0x200.
    private static bool IsValidAmmProtection(int protection) =>
        (unchecked((uint)protection) & 0xFFFFFC0Cu) == 0;

    // The kernel protection a page of the given AMM protection maps with.
    private static int AmmHostProtection(int protection)
    {
        var host = protection & 0x33;
        if ((protection & 0x140) != 0)
        {
            host |= 1;
        }

        if ((protection & 0x280) != 0)
        {
            host |= 3;
        }

        if ((host & 2) != 0)
        {
            host |= 1;
        }

        return host;
    }

    private static int ExecuteAmmCommand(CpuContext ctx, AmmCommand command)
    {
        lock (_ammGate)
        {
            if (!TryReserveAmmArea(ctx))
            {
                return KernelEbusy;
            }

            return command.Kind switch
            {
                AmmCommandKind.Map => ExecuteAmmMapLocked(ctx, command),
                AmmCommandKind.Unmap => ExecuteAmmUnmapLocked(ctx, command, toPrt: false),
                AmmCommandKind.UnmapToPrt => ExecuteAmmUnmapLocked(ctx, command, toPrt: true),
                AmmCommandKind.Remap => ExecuteAmmRemapLocked(ctx, command, alias: false, intoPrt: false),
                AmmCommandKind.MultiMap => ExecuteAmmRemapLocked(ctx, command, alias: true, intoPrt: false),
                AmmCommandKind.RemapIntoPrt => ExecuteAmmRemapLocked(ctx, command, alias: false, intoPrt: true),
                AmmCommandKind.ModifyProtect or AmmCommandKind.ModifyMtypeProtect => ExecuteAmmProtectLocked(ctx, command),
                AmmCommandKind.MapAsPrt => ExecuteAmmMapAsPrtLocked(ctx, command),
                AmmCommandKind.AllocatePaForPrt => ExecuteAmmAllocatePrtLocked(ctx, command),
                _ => KernelEinval,
            };
        }
    }

    private static int ExecuteAmmMapLocked(CpuContext ctx, AmmCommand command)
    {
        // A map over mapped pages replaces them, so it first returns the range to the area.
        var result = ReleaseAmmRangeLocked(ctx, command.Address, command.Size);
        if (result != 0)
        {
            return result;
        }

        RemoveAmmPrtLocked(command.Address, command.Address + command.Size);
        List<AmmMapping> runs;
        if (command.DirectOffset is null)
        {
            if (!TryTakeAmmPagesLocked(command.Size, out var pages))
            {
                return KernelEagain;
            }

            runs = pages.ConvertAll(page => new AmmMapping(0, page.Length, page.Offset, true, 0, command.Type));
        }
        else
        {
            runs = [new AmmMapping(0, command.Size, command.DirectOffset.Value, false, 0, command.Type)];
        }

        return MapAmmRunsLocked(ctx, command.Address, runs, command.Protection);
    }

    // Unmap returns the range to the reserved area. UnmapToPrt returns it to the PRT state, which AnyPS5 only
    // allows on a range that is PRT.
    private static int ExecuteAmmUnmapLocked(CpuContext ctx, AmmCommand command, bool toPrt)
    {
        var end = command.Address + command.Size;
        if (toPrt && !IsAmmPrtLocked(command.Address, end))
        {
            return KernelEinval;
        }

        var result = ReleaseAmmRangeLocked(ctx, command.Address, command.Size);
        if (result == 0 && !toPrt)
        {
            RemoveAmmPrtLocked(command.Address, end);
        }

        return result;
    }

    // A PRT range reads as zeros. The area's unmapped pages already do that for the GPU, so the range goes back to
    // the reserved area and is remembered as PRT. A CPU read of a PRT page that is not backed faults here, where
    // AnyPS5 maps it as read-only zero pages.
    private static int ExecuteAmmMapAsPrtLocked(CpuContext ctx, AmmCommand command)
    {
        var result = ReleaseAmmRangeLocked(ctx, command.Address, command.Size);
        if (result == 0)
        {
            AddAmmPrtLocked(command.Address, command.Address + command.Size);
        }

        return result;
    }

    // Backs the unbacked pages of the range with auto pages and gives the backed ones the new protection. AnyPS5
    // requires the range to be PRT; here a range that was never mapped as PRT is backed too, as a title that
    // asks for backing needs the pages either way.
    private static int ExecuteAmmAllocatePrtLocked(CpuContext ctx, AmmCommand command)
    {
        var end = command.Address + command.Size;
        SplitAmmMappingAtLocked(command.Address);
        SplitAmmMappingAtLocked(end);
        var segments = new List<(ulong Start, ulong Length, bool Backed)>();
        var cursor = command.Address;
        var index = FirstAmmMappingAtOrAfterLocked(command.Address);
        ulong unbacked = 0;
        while (cursor < end)
        {
            if (index < _ammMappings.Count && _ammMappings[index].Address == cursor)
            {
                var stop = cursor;
                while (index < _ammMappings.Count && _ammMappings[index].Address == stop && stop < end)
                {
                    stop += _ammMappings[index].Length;
                    index++;
                }

                segments.Add((cursor, stop - cursor, true));
                cursor = stop;
            }
            else
            {
                var next = index < _ammMappings.Count ? Math.Min(_ammMappings[index].Address, end) : end;
                segments.Add((cursor, next - cursor, false));
                unbacked += next - cursor;
                cursor = next;
            }
        }

        // Nothing changes when the pool cannot back every unbacked page.
        ulong free = 0;
        foreach (var (_, length) in _ammFreePages)
        {
            free += length;
        }

        if (free < unbacked)
        {
            return KernelEagain;
        }

        foreach (var (start, length, backed) in segments)
        {
            int result;
            if (backed)
            {
                result = ApplyAmmProtectionLocked(ctx, start, length, command.Protection, ~0, command.Type);
            }
            else if (!TryTakeAmmPagesLocked(length, out var pages))
            {
                return KernelEagain;
            }
            else
            {
                var runs = pages.ConvertAll(page => new AmmMapping(0, page.Length, page.Offset, true, 0, command.Type));
                result = MapAmmRunsLocked(ctx, start, runs, command.Protection);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    // Remap moves the pages of the source range to the address, MultiMap keeps them at the source too, and
    // RemapIntoPrt moves them into a PRT range. The pages take the command's protection. A MultiMap maps the same
    // direct memory at both addresses, so the two views share storage as they do on hardware.
    private static int ExecuteAmmRemapLocked(CpuContext ctx, AmmCommand command, bool alias, bool intoPrt)
    {
        var end = command.Address + command.Size;
        var sourceEnd = command.Source + command.Size;
        if ((alias || intoPrt) && command.Address < sourceEnd && command.Source < end)
        {
            return KernelEinval;
        }

        if (intoPrt && !IsAmmPrtLocked(command.Address, end))
        {
            return KernelEinval;
        }

        if (!TryGetAmmRunsLocked(command.Source, command.Size, out var sourceRuns))
        {
            return KernelEinval;
        }

        var runs = sourceRuns.ConvertAll(run => run with { Address = 0 });
        // The pages stay held while both ranges are released, so a page the source and the destination share
        // is not returned to the pool in between.
        foreach (var run in runs)
        {
            AddAmmPageHoldersLocked(run);
        }

        var result = 0;
        if (!alias)
        {
            result = ReleaseAmmRangeLocked(ctx, command.Source, command.Size);
            if (result == 0)
            {
                RemoveAmmPrtLocked(command.Source, sourceEnd);
            }
        }

        if (result == 0)
        {
            result = ReleaseAmmRangeLocked(ctx, command.Address, command.Size);
        }

        if (result != 0)
        {
            foreach (var run in runs)
            {
                ReleaseAmmPageHoldersLocked(run);
            }

            return result;
        }

        if (!intoPrt)
        {
            RemoveAmmPrtLocked(command.Address, end);
        }

        return MapAmmRunsLocked(ctx, command.Address, runs, command.Protection);
    }

    // ModifyProtect replaces the protection bits under the mask; ModifyMtypeProtect also sets the memory type. The
    // range must be mapped.
    private static int ExecuteAmmProtectLocked(CpuContext ctx, AmmCommand command)
    {
        if (!TryGetAmmRunsLocked(command.Address, command.Size, out _))
        {
            return KernelEinval;
        }

        return ApplyAmmProtectionLocked(
            ctx,
            command.Address,
            command.Size,
            command.Protection,
            command.Mask,
            command.Kind == AmmCommandKind.ModifyMtypeProtect ? command.Type : null);
    }

    // Sets the protection of a range that is split at its ends and fully mapped, and applies it to the host in runs
    // of equal host protection.
    private static int ApplyAmmProtectionLocked(CpuContext ctx, ulong address, ulong size, int protection, int mask, int? type)
    {
        var end = address + size;
        var groupStart = 0UL;
        var groupLength = 0UL;
        var groupHost = 0;
        for (var index = FirstAmmMappingAtOrAfterLocked(address); index < _ammMappings.Count && _ammMappings[index].Address < end; index++)
        {
            var mapping = _ammMappings[index];
            var updated = (mapping.Protection & ~mask) | (protection & mask);
            _ammMappings[index] = mapping with { Protection = updated, Type = type ?? mapping.Type };
            var host = AmmHostProtection(updated);
            if (groupLength != 0 && host == groupHost)
            {
                groupLength += mapping.Length;
                continue;
            }

            var flushed = ProtectAmmHostRange(ctx, groupStart, groupLength, groupHost);
            if (flushed != 0)
            {
                return flushed;
            }

            groupStart = mapping.Address;
            groupLength = mapping.Length;
            groupHost = host;
        }

        return ProtectAmmHostRange(ctx, groupStart, groupLength, groupHost);
    }

    private static int ProtectAmmHostRange(CpuContext ctx, ulong address, ulong length, int hostProtection) =>
        length == 0
            ? 0
            : KernelMemoryCompatExports.InvokeKernelMemoryOperation(
                ctx,
                KernelMemoryCompatExports.KernelMprotect,
                address,
                length,
                unchecked((ulong)(uint)hostProtection));

    // Maps the runs one after the other from the address. A failure unmaps what was mapped and gives the runs'
    // pages up.
    private static int MapAmmRunsLocked(CpuContext ctx, ulong address, List<AmmMapping> runs, int protection)
    {
        var start = address;
        var host = unchecked((ulong)(uint)AmmHostProtection(protection));
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var result = ctx.TryWriteUInt64(_ammAddressSlot, address)
                ? KernelMemoryCompatExports.InvokeKernelMemoryOperation(
                    ctx,
                    KernelMemoryCompatExports.KernelMapDirectMemory,
                    _ammAddressSlot,
                    run.Length,
                    host,
                    AmmMapFixed,
                    run.Offset,
                    0)
                : KernelEfault;
            if (result != 0)
            {
                for (var j = i; j < runs.Count; j++)
                {
                    ReleaseAmmPageHoldersLocked(runs[j]);
                }

                _ = ReleaseAmmRangeLocked(ctx, start, address - start);
                return result;
            }

            InsertAmmMappingLocked(new AmmMapping(address, run.Length, run.Offset, run.Auto, protection, run.Type));
            address += run.Length;
        }

        return 0;
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

        SplitAmmMappingAtLocked(address);
        SplitAmmMappingAtLocked(end);
        first = FirstAmmMappingAtOrAfterLocked(address);
        while (first < _ammMappings.Count && _ammMappings[first].Address < end)
        {
            var mapping = _ammMappings[first];
            _ammMappings.RemoveAt(first);
            ReleaseAmmPageHoldersLocked(mapping);
        }

        return 0;
    }

    private static void InsertAmmMappingLocked(AmmMapping mapping) =>
        _ammMappings.Insert(FirstAmmMappingAtOrAfterLocked(mapping.Address), mapping);

    private static int FirstAmmMappingAtOrAfterLocked(ulong address)
    {
        var index = _ammMappings.FindIndex(mapping => mapping.Address >= address);
        return index < 0 ? _ammMappings.Count : index;
    }

    // Splits the mapping that spans the address in two there.
    private static void SplitAmmMappingAtLocked(ulong address)
    {
        var index = _ammMappings.FindIndex(mapping => mapping.Address < address && address < mapping.Address + mapping.Length);
        if (index < 0)
        {
            return;
        }

        var mapping = _ammMappings[index];
        var headLength = address - mapping.Address;
        _ammMappings[index] = mapping with { Length = headLength };
        _ammMappings.Insert(index + 1, mapping with
        {
            Address = address,
            Length = mapping.Length - headLength,
            Offset = mapping.Offset + headLength,
        });
    }

    // The mapped runs of a range, split at its ends. False when a page of it is not mapped.
    private static bool TryGetAmmRunsLocked(ulong address, ulong size, out List<AmmMapping> runs)
    {
        var end = address + size;
        SplitAmmMappingAtLocked(address);
        SplitAmmMappingAtLocked(end);
        runs = [];
        var cursor = address;
        for (var index = FirstAmmMappingAtOrAfterLocked(address); cursor < end; index++)
        {
            if (index >= _ammMappings.Count || _ammMappings[index].Address != cursor)
            {
                runs.Clear();
                return false;
            }

            runs.Add(_ammMappings[index]);
            cursor += _ammMappings[index].Length;
        }

        return true;
    }

    // A run held once more than it is mapped keeps its pages out of the pool until every holder lets go.
    private static void AddAmmPageHoldersLocked(AmmMapping run)
    {
        if (!run.Auto)
        {
            return;
        }

        for (var offset = run.Offset; offset < run.Offset + run.Length; offset += AmmPageSize)
        {
            _ammPageHolders[offset] = _ammPageHolders.TryGetValue(offset, out var holders) ? holders + 1 : 2;
        }
    }

    private static void ReleaseAmmPageHoldersLocked(AmmMapping run)
    {
        if (!run.Auto)
        {
            return;
        }

        if (_ammPageHolders.Count == 0)
        {
            ReleaseAmmPagesLocked(run.Offset, run.Length);
            return;
        }

        var freeStart = 0UL;
        var freeLength = 0UL;
        for (var offset = run.Offset; offset < run.Offset + run.Length; offset += AmmPageSize)
        {
            if (_ammPageHolders.TryGetValue(offset, out var holders))
            {
                if (holders <= 2)
                {
                    _ammPageHolders.Remove(offset);
                }
                else
                {
                    _ammPageHolders[offset] = holders - 1;
                }

                continue;
            }

            if (freeLength != 0 && freeStart + freeLength == offset)
            {
                freeLength += AmmPageSize;
                continue;
            }

            ReleaseAmmPagesLocked(freeStart, freeLength);
            freeStart = offset;
            freeLength = AmmPageSize;
        }

        ReleaseAmmPagesLocked(freeStart, freeLength);
    }

    private static void AddAmmPrtLocked(ulong start, ulong end)
    {
        RemoveAmmPrtLocked(start, end);
        var index = _ammPrt.FindIndex(range => range.Start > start);
        index = index < 0 ? _ammPrt.Count : index;
        // Touching ranges merge, so a range is PRT exactly when one entry covers it.
        if (index > 0 && _ammPrt[index - 1].End == start)
        {
            start = _ammPrt[index - 1].Start;
            _ammPrt.RemoveAt(--index);
        }

        if (index < _ammPrt.Count && _ammPrt[index].Start == end)
        {
            end = _ammPrt[index].End;
            _ammPrt.RemoveAt(index);
        }

        _ammPrt.Insert(index, (start, end));
    }

    private static void RemoveAmmPrtLocked(ulong start, ulong end)
    {
        var index = _ammPrt.FindIndex(range => range.End > start);
        while (index >= 0 && index < _ammPrt.Count && _ammPrt[index].Start < end)
        {
            var range = _ammPrt[index];
            _ammPrt.RemoveAt(index);
            if (range.Start < start)
            {
                _ammPrt.Insert(index++, (range.Start, start));
            }

            if (range.End > end)
            {
                _ammPrt.Insert(index++, (end, range.End));
            }
        }
    }

    private static bool IsAmmPrtLocked(ulong start, ulong end) =>
        _ammPrt.Exists(range => range.Start <= start && end <= range.End);

    // Forgets the AMM state, for tests that give each case a fresh guest memory.
    internal static void ResetAmmStateForTests()
    {
        lock (_ammGate)
        {
            _ammFreePages.Clear();
            _ammMappings.Clear();
            _ammPrt.Clear();
            _ammPageHolders.Clear();
            _ammAreaReserved = false;
            _ammAddressSlot = 0;
        }
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
                ? KernelMemoryCompatExports.ReserveBackingRange(ctx, _ammAddressSlot, AmmMultimapAreaEnd - AmmAreaStart, AmmMapFixed, 0)
                : KernelEfault;
            if (result != 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][ERROR] ampr.amm area reservation failed: 0x{AmmAreaStart:X16}+0x{AmmMultimapAreaEnd - AmmAreaStart:X} result=0x{result:X8}");
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
