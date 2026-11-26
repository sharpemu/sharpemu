// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Ampr;

// The rest of the AMM exports: the remap, protect and PRT commands, the GpuMaskId and measure forms, and the
// submit variants. Each command records a fixed AmmRecordSize record into the buffer and runs when it is
// submitted. The GpuMaskId forms take one more argument that the commands do not use.
public static partial class AmprExports
{
    private const ulong AmmUsageStatsSize = 0x18;
    private const ushort AmmPageTablePoolEntries = 512;
    private const uint AmmRingIdleFlags = 0x7;
    private const int AmmSubmissionHistory = 256;
    private static readonly ConcurrentDictionary<uint, AmprSubmission> _ammSubmissions = new();
    private static int _ammSubmissionIssued;

    [SysAbiExport(
        Nid = "ojBkmG7+CgE",
        ExportName = "sceAmprAmmCommandBufferMapWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferMapWithGpuMaskId(CpuContext ctx) =>
        AppendAmmMap(ctx);

    // Remap and MultiMap take (buffer, va, source va, size, protection).
    [SysAbiExport(
        Nid = "ijvRMHfwwjc",
        ExportName = "sceAmprAmmCommandBufferRemap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferRemap(CpuContext ctx) =>
        AppendAmmRemap(ctx, AmmCommandKind.Remap);

    [SysAbiExport(
        Nid = "Fo0Oaoqukg4",
        ExportName = "sceAmprAmmCommandBufferRemapWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferRemapWithGpuMaskId(CpuContext ctx) =>
        AppendAmmRemap(ctx, AmmCommandKind.Remap);

    [SysAbiExport(
        Nid = "j2C9no3P4Nw",
        ExportName = "sceAmprAmmCommandBufferMultiMap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferMultiMap(CpuContext ctx) =>
        AppendAmmRemap(ctx, AmmCommandKind.MultiMap);

    [SysAbiExport(
        Nid = "Qyz8VbxnaII",
        ExportName = "sceAmprAmmCommandBufferMultiMapWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferMultiMapWithGpuMaskId(CpuContext ctx) =>
        AppendAmmRemap(ctx, AmmCommandKind.MultiMap);

    // ModifyProtect takes (buffer, va, size, protection, mask).
    [SysAbiExport(
        Nid = "joNORJjCFhg",
        ExportName = "sceAmprAmmCommandBufferModifyProtect",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferModifyProtect(CpuContext ctx) =>
        AppendAmmProtect(ctx, mtype: false);

    [SysAbiExport(
        Nid = "spjcxyNX-KE",
        ExportName = "sceAmprAmmCommandBufferModifyProtectWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferModifyProtectWithGpuMaskId(CpuContext ctx) =>
        AppendAmmProtect(ctx, mtype: false);

    // ModifyMtypeProtect takes (buffer, va, size, memory type, protection, mask).
    [SysAbiExport(
        Nid = "rKq9nG+L9Xo",
        ExportName = "sceAmprAmmCommandBufferModifyMtypeProtect",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferModifyMtypeProtect(CpuContext ctx) =>
        AppendAmmProtect(ctx, mtype: true);

    [SysAbiExport(
        Nid = "ie-UuLGbYXo",
        ExportName = "sceAmprAmmCommandBufferModifyMtypeProtectWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferModifyMtypeProtectWithGpuMaskId(CpuContext ctx) =>
        AppendAmmProtect(ctx, mtype: true);

    [SysAbiExport(
        Nid = "ZJCgt+aPHAU",
        ExportName = "sceAmprAmmCommandBufferMapAsPrt",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferMapAsPrt(CpuContext ctx) =>
        AppendAmmRange(ctx, AmmCommandKind.MapAsPrt);

    // AllocatePaForPrt takes (buffer, va, size, memory type, protection).
    [SysAbiExport(
        Nid = "d3lKWPM6odc",
        ExportName = "sceAmprAmmCommandBufferAllocatePaForPrt",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferAllocatePaForPrt(CpuContext ctx) =>
        AppendAmmCommand(ctx, new AmmCommand
        {
            Kind = AmmCommandKind.AllocatePaForPrt,
            Address = ctx[CpuRegister.Rsi],
            Size = ctx[CpuRegister.Rdx],
            Type = unchecked((int)ctx[CpuRegister.Rcx]),
            Protection = unchecked((int)ctx[CpuRegister.R8]),
        });

    // RemapIntoPrt takes (buffer, va, source va, size, protection, opcode); the opcode only selects an encoding
    // of the same command.
    [SysAbiExport(
        Nid = "0Xo9g4goDXU",
        ExportName = "sceAmprAmmCommandBufferRemapIntoPrt",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferRemapIntoPrt(CpuContext ctx) =>
        AppendAmmRemap(ctx, AmmCommandKind.RemapIntoPrt);

    [SysAbiExport(
        Nid = "3A2X6OCHtnA",
        ExportName = "sceAmprAmmCommandBufferUnmapToPrt",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmCommandBufferUnmapToPrt(CpuContext ctx) =>
        AppendAmmRange(ctx, AmmCommandKind.UnmapToPrt);

    private static int AppendAmmRemap(CpuContext ctx, AmmCommandKind kind) =>
        AppendAmmCommand(ctx, new AmmCommand
        {
            Kind = kind,
            Address = ctx[CpuRegister.Rsi],
            Source = ctx[CpuRegister.Rdx],
            Size = ctx[CpuRegister.Rcx],
            Protection = unchecked((int)ctx[CpuRegister.R8]),
        });

    private static int AppendAmmProtect(CpuContext ctx, bool mtype) =>
        AppendAmmCommand(ctx, mtype
            ? new AmmCommand
            {
                Kind = AmmCommandKind.ModifyMtypeProtect,
                Address = ctx[CpuRegister.Rsi],
                Size = ctx[CpuRegister.Rdx],
                Type = unchecked((int)ctx[CpuRegister.Rcx]),
                Protection = unchecked((int)ctx[CpuRegister.R8]),
                Mask = unchecked((int)ctx[CpuRegister.R9]),
            }
            : new AmmCommand
            {
                Kind = AmmCommandKind.ModifyProtect,
                Address = ctx[CpuRegister.Rsi],
                Size = ctx[CpuRegister.Rdx],
                Protection = unchecked((int)ctx[CpuRegister.Rcx]),
                Mask = unchecked((int)ctx[CpuRegister.R8]),
            });

    // The measure calls return the bytes the command takes in the buffer, or EINVAL for arguments the command
    // would reject. Every AMM command is one AmmRecordSize record.
    private static int MeasureAmm(CpuContext ctx, bool valid)
    {
        if (!valid)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        ctx[CpuRegister.Rax] = AmmRecordSize;
        return (int)AmmRecordSize;
    }

    private static bool IsValidAmmMapRange(ulong address, ulong size) =>
        address != 0 && size != 0 && ((address | size) & (AmmPageSize - 1)) == 0 && address + size > address;

    // Measure(va, size, memory type, protection)
    [SysAbiExport(
        Nid = "6hbai6KIXkk",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeMap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeMap(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]) &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.Rcx])));

    [SysAbiExport(
        Nid = "m+fYyX8oFqw",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeMapWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeMapWithGpuMaskId(CpuContext ctx) =>
        AmmMeasureCommandSizeMap(ctx);

    // MeasureMapDirect(va, direct offset, size, memory type, protection)
    [SysAbiExport(
        Nid = "ZFDZoN9IbVU",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeMapDirect",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeMapDirect(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rdx]) &&
            (ctx[CpuRegister.Rsi] & (AmmPageSize - 1)) == 0 &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.R8])));

    [SysAbiExport(
        Nid = "KUjtdPZJo5I",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeMapDirectWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeMapDirectWithGpuMaskId(CpuContext ctx) =>
        AmmMeasureCommandSizeMapDirect(ctx);

    // MeasureUnmap(va, size)
    [SysAbiExport(
        Nid = "Ayg6PIon2wA",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeUnmap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeUnmap(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]));

    // MeasureMapAsPrt(va, size)
    [SysAbiExport(
        Nid = "4SSJ+cn+Pvg",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeMapAsPrt",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeMapAsPrt(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]));

    // MeasureAllocatePaForPrt(va, size, memory type, protection)
    [SysAbiExport(
        Nid = "2yYv9QWB3hU",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeAllocatePaForPrt",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeAllocatePaForPrt(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]) &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.Rcx])));

    // MeasureRemap and MeasureMultiMap take (va, source va, size, protection).
    [SysAbiExport(
        Nid = "8sFq5s8eHnQ",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeRemap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeRemap(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rdx]) &&
            IsValidAmmMapRange(ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]) &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.Rcx])));

    [SysAbiExport(
        Nid = "b50TWwRmzu8",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeRemapWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeRemapWithGpuMaskId(CpuContext ctx) =>
        AmmMeasureCommandSizeRemap(ctx);

    [SysAbiExport(
        Nid = "U6PxwrRchl4",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeMultiMap",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeMultiMap(CpuContext ctx) =>
        AmmMeasureCommandSizeRemap(ctx);

    [SysAbiExport(
        Nid = "uGs4yyYTASM",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeMultiMapWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeMultiMapWithGpuMaskId(CpuContext ctx) =>
        AmmMeasureCommandSizeRemap(ctx);

    // MeasureModifyProtect(va, size, protection, mask)
    [SysAbiExport(
        Nid = "JPQagYw-y+4",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeModifyProtect",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeModifyProtect(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]) &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.Rdx])) &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.Rcx])));

    [SysAbiExport(
        Nid = "gdSoJ6dvztU",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeModifyProtectWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeModifyProtectWithGpuMaskId(CpuContext ctx) =>
        AmmMeasureCommandSizeModifyProtect(ctx);

    // MeasureModifyMtypeProtect(va, size, memory type, protection, mask)
    [SysAbiExport(
        Nid = "Ok2Nxka7qI4",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeModifyMtypeProtect",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeModifyMtypeProtect(CpuContext ctx) =>
        MeasureAmm(ctx, IsValidAmmMapRange(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]) &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.Rcx])) &&
            IsValidAmmProtection(unchecked((int)ctx[CpuRegister.R8])));

    [SysAbiExport(
        Nid = "c5D63Y35GjI",
        ExportName = "sceAmprAmmMeasureAmmCommandSizeModifyMtypeProtectWithGpuMaskId",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmMeasureCommandSizeModifyMtypeProtectWithGpuMaskId(CpuContext ctx) =>
        AmmMeasureCommandSizeModifyMtypeProtect(ctx);

    [SysAbiExport(
        Nid = "touqMEt6qXQ",
        ExportName = "sceAmprAmmSetPageTablePoolOccupancyNotificationThreshold",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmSetPageTablePoolOccupancyNotificationThreshold(CpuContext ctx) =>
        SetAmmResult(ctx, 0);

    // The page table pool is not modelled: the stats report the pool AnyPS5 reports, empty and with every ring idle.
    [SysAbiExport(
        Nid = "KqiWXLgCVe0",
        ExportName = "sceAmprAmmGetUsageStatsData",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmGetUsageStatsData(CpuContext ctx)
    {
        var stats = ctx[CpuRegister.Rdi];
        if (stats == 0)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        if (!ctx.TryReadUInt64(stats, out var sizeInBytes))
        {
            return SetAmmResult(ctx, KernelEfault);
        }

        if (sizeInBytes > AmmUsageStatsSize)
        {
            return SetAmmResult(ctx, KernelEinval);
        }

        Span<byte> data = stackalloc byte[(int)AmmUsageStatsSize];
        data.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(data, sizeInBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(data[0x08..], AmmPageTablePoolEntries);
        BinaryPrimitives.WriteUInt32LittleEndian(data[0x10..], AmmRingIdleFlags);
        return SetAmmResult(ctx, sizeInBytes == 0 || ctx.Memory.TryWrite(stats, data[..(int)sizeInBytes]) ? 0 : KernelEfault);
    }

    // SubmitCommandBuffer2(buffer, bytes, priority, result*, id*) reports the submission's result and id. A buffer that
    // is still running reports success; its failure is logged when it ends, as for SubmitCommandBuffer.
    [SysAbiExport(
        Nid = "OJf3vCckPAM",
        ExportName = "sceAmprAmmSubmitCommandBuffer2",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmSubmitCommandBuffer2(CpuContext ctx)
    {
        var resultPointer = ctx[CpuRegister.Rcx];
        var idPointer = ctx[CpuRegister.R8];
        var result = SubmitAmmBuffer(ctx, out var submission);
        if (result != 0)
        {
            return SetAmmResult(ctx, result);
        }

        var id = RegisterAmmSubmission(submission!);
        if (resultPointer != 0)
        {
            Span<byte> value = stackalloc byte[8];
            value.Clear();
            if (submission!.CompletedInline)
            {
                BinaryPrimitives.WriteInt32LittleEndian(value, submission.ExecutionResult);
                BinaryPrimitives.WriteUInt32LittleEndian(value[4..], submission.ErrorOffset);
            }

            if (!ctx.Memory.TryWrite(resultPointer, value))
            {
                return SetAmmResult(ctx, KernelEfault);
            }
        }

        return SetAmmResult(ctx, idPointer != 0 && !ctx.TryWriteUInt32(idPointer, id) ? KernelEfault : 0);
    }

    // SubmitCommandBuffer3(buffer, bytes, priority, id*)
    [SysAbiExport(
        Nid = "NnKhlMJtIsI",
        ExportName = "sceAmprAmmSubmitCommandBuffer3",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmSubmitCommandBuffer3(CpuContext ctx)
    {
        var idPointer = ctx[CpuRegister.Rcx];
        var result = SubmitAmmBuffer(ctx, out var submission);
        if (result != 0)
        {
            return SetAmmResult(ctx, result);
        }

        var id = RegisterAmmSubmission(submission!);
        return SetAmmResult(ctx, idPointer != 0 && !ctx.TryWriteUInt32(idPointer, id) ? KernelEfault : 0);
    }

    // Waits for a submission by the id a submit returned. An id that was never issued is ESRCH. One that finished
    // long ago and was forgotten has completed.
    [SysAbiExport(
        Nid = "HXymib4T8gc",
        ExportName = "sceAmprAmmWaitCommandBufferCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AmmWaitCommandBufferCompletion(CpuContext ctx)
    {
        var id = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (id == 0 || id > unchecked((uint)Volatile.Read(ref _ammSubmissionIssued)))
        {
            return SetAmmResult(ctx, KernelEsrch);
        }

        if (_ammSubmissions.TryGetValue(id, out var submission))
        {
            submission.Wait();
        }

        return SetAmmResult(ctx, 0);
    }

    private static uint RegisterAmmSubmission(AmprSubmission submission)
    {
        var id = unchecked((uint)Interlocked.Increment(ref _ammSubmissionIssued));
        _ammSubmissions[id] = submission;
        if (_ammSubmissions.Count > AmmSubmissionHistory * 2)
        {
            foreach (var (oldId, old) in _ammSubmissions)
            {
                if (oldId + AmmSubmissionHistory < id && old.IsCompleted)
                {
                    _ammSubmissions.TryRemove(oldId, out _);
                }
            }
        }

        return id;
    }
}
