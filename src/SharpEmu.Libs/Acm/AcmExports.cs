// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Acm;

public static class AcmExports
{
    private const int AcmBatchErrorBytes = 32;
    private static readonly ConcurrentDictionary<uint, byte> Contexts = new();
    private static int _nextContextHandle;
    private static int _nextBatchHandle;

    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextCreate(CpuContext ctx)
    {
        var outContextAddress = ctx[CpuRegister.Rdi];
        if (outContextAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextContextHandle));
        Span<byte> handleBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(handleBytes, handle);
        if (!ctx.Memory.TryWrite(outContextAddress, handleBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Contexts[handle] = 0;
        Trace($"context_create context={handle}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "jBgBjAj02R8",
        ExportName = "sceAcmContextDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextDestroy(CpuContext ctx)
    {
        var context = unchecked((uint)ctx[CpuRegister.Rdi]);
        Contexts.TryRemove(context, out _);
        Trace($"context_destroy context={context}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "tW9W+CAG4FE",
        ExportName = "sceAcmBatchStartBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartBuffer(CpuContext ctx)
    {
        var context = unchecked((uint)ctx[CpuRegister.Rdi]);
        var commandsAddress = ctx[CpuRegister.Rsi];
        var commandsSize = ctx[CpuRegister.Rdx];
        var errorAddress = ctx[CpuRegister.Rcx];
        var batchAddress = ctx[CpuRegister.R8];

        if (!Contexts.ContainsKey(context) ||
            batchAddress == 0 ||
            (commandsSize != 0 && commandsAddress == 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return CompleteBatchStart(ctx, context, 1, errorAddress, batchAddress);
    }

    [SysAbiExport(
        Nid = "8fe55ktlNVo",
        ExportName = "sceAcmBatchStartBuffers",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartBuffers(CpuContext ctx)
    {
        var context = unchecked((uint)ctx[CpuRegister.Rdi]);
        var infoCount = unchecked((uint)ctx[CpuRegister.Rsi]);
        var infoArrayAddress = ctx[CpuRegister.Rdx];
        var errorAddress = ctx[CpuRegister.Rcx];
        var batchAddress = ctx[CpuRegister.R8];

        if (!Contexts.ContainsKey(context) ||
            batchAddress == 0 ||
            (infoCount != 0 && infoArrayAddress == 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return CompleteBatchStart(ctx, context, infoCount, errorAddress, batchAddress);
    }

    // DSP batch submission and synchronization. The emulator runs no ACM DSP
    // jobs (FFT/panner/reverb output stays silent), but Scream's workers trap
    // with int 0x41/0x42 asserts whenever a submission call reports failure,
    // so the whole batch surface must report success.
    [SysAbiExport(
        Nid = "WeZOIm8+8WI",
        ExportName = "sceAcmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchInitialize(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "Mk1xvQXIdkk",
        ExportName = "sceAcmBatchInitializeLite",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchInitializeLite(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "A5NXCXK5Gfc",
        ExportName = "sceAcmBatchStart",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStart(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "S3BPrjCfZ90",
        ExportName = "sceAcmBatchStartMultiple",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartMultiple(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "uqDIauipRbo",
        ExportName = "sceAcmBatchProcess",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchProcess(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        Nid = "RLN3gRlXJLE",
        ExportName = "sceAcmBatchWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchWait(CpuContext ctx)
    {
        var context = unchecked((uint)ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(
            Contexts.ContainsKey(context)
                ? OrbisGen2Result.ORBIS_GEN2_OK
                : OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
    }

    [SysAbiExport(
        Nid = "r7z5YQFZo+U",
        ExportName = "sceAcmBatchJobNotification",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchJobNotification(CpuContext ctx) =>
        AdvanceBatchInfo(ctx, 32);

    [SysAbiExport(
        Nid = "u70oWo92SYQ",
        ExportName = "sceAcm_ConvReverb_SharedInput",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmConvReverbSharedInput(CpuContext ctx) =>
        AdvanceBatchInfo(ctx, 1024);

    [SysAbiExport(
        Nid = "9nLbWmRDpa8",
        ExportName = "sceAcm_ConvReverb_SharedIr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmConvReverbSharedIr(CpuContext ctx) =>
        AdvanceBatchInfo(ctx, 1024);

    [SysAbiExport(
        Nid = "KovqaFbmtsM",
        ExportName = "sceAcm_FFT",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmFft(CpuContext ctx) =>
        AdvanceBatchInfo(ctx, 256);

    [SysAbiExport(
        Nid = "DR-ZCmvVR9Q",
        ExportName = "sceAcm_IFFT",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmIfft(CpuContext ctx) =>
        AdvanceBatchInfo(ctx, 256);

    [SysAbiExport(
        Nid = "LA4RCNKnFjg",
        ExportName = "sceAcm_Panner",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmPanner(CpuContext ctx) =>
        AdvanceBatchInfo(ctx, 512);

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextHandle, 0);
        Interlocked.Exchange(ref _nextBatchHandle, 0);
    }

    private static int CompleteBatchStart(
        CpuContext ctx,
        uint context,
        uint infoCount,
        ulong errorAddress,
        ulong batchAddress)
    {
        if (errorAddress != 0)
        {
            Span<byte> error = stackalloc byte[AcmBatchErrorBytes];
            error.Clear();
            if (!ctx.Memory.TryWrite(errorAddress, error))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        var batch = unchecked((uint)Interlocked.Increment(ref _nextBatchHandle));
        Span<byte> batchBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(batchBytes, batch);
        if (!ctx.Memory.TryWrite(batchAddress, batchBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Trace($"batch_start context={context} count={infoCount} batch={batch}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int AdvanceBatchInfo(CpuContext ctx, ulong byteCount)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        Span<byte> info = stackalloc byte[24];
        if (!ctx.Memory.TryRead(infoAddress, info))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var buffer = BinaryPrimitives.ReadUInt64LittleEndian(info);
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(info[8..]);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(info[16..]);
        if (buffer != 0 && size != 0)
        {
            var nextOffset = offset > ulong.MaxValue - byteCount
                ? ulong.MaxValue
                : offset + byteCount;
            BinaryPrimitives.WriteUInt64LittleEndian(info[8..], Math.Min(size, nextOffset));
            if (!ctx.Memory.TryWrite(infoAddress, info))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static void Trace(string message)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_ACM"),
                "1",
                StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] acm.{message}");
        }
    }
}
