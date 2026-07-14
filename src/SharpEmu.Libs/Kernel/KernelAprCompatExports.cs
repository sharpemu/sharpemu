// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

public static class KernelAprCompatExports
{
    private static readonly ConcurrentDictionary<uint, AprSubmission> _submittedCommandBuffers = new();
    private static int _nextSubmissionId;
    private static int _aprWaitTraceCount;
    private static readonly bool _traceApr =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal);

    private readonly record struct AprSubmission(ulong CommandBuffer, ulong Priority, ulong ResultAddress);

    [SysAbiExport(
        Nid = "ASoW5WE-UPo",
        ExportName = "sceKernelAprSubmitCommandBufferAndGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBufferAndGetResult(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var priority = ctx[CpuRegister.Rsi];
        var resultAddress = ctx[CpuRegister.Rdx];
        var outSubmissionId = ctx[CpuRegister.Rcx];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        if (submissionId == 0)
        {
            submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        }

        _submittedCommandBuffers[submissionId] = new AprSubmission(commandBuffer, priority, resultAddress);

        var completionResult = AmprExports.CompleteCommandBuffer(ctx, commandBuffer);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }

        if (outSubmissionId != 0 && !ctx.TryWriteUInt32(outSubmissionId, submissionId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (resultAddress != 0 && !TryWriteAprResult(ctx, resultAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceApr(ctx, "submit_get_result", submissionId, commandBuffer, priority, resultAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "rqwFKI4PAiM",
        ExportName = "sceKernelAprWaitCommandBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprWaitCommandBuffer(CpuContext ctx)
    {
        var submissionId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var waitArg1 = ctx[CpuRegister.Rsi];
        var waitArg2 = ctx[CpuRegister.Rdx];

        if (!_submittedCommandBuffers.TryRemove(submissionId, out var submission))
        {
            TraceAprWaitFailure(ctx, "wait_missing", submissionId, commandBuffer: 0, waitArg1, waitArg2);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        // Completion output was written when the command was submitted.
        TraceApr(ctx, "wait", submissionId, submission.CommandBuffer, waitArg1, waitArg2);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eE4Szl8sil8",
        ExportName = "sceKernelAprSubmitCommandBuffer",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        _submittedCommandBuffers[submissionId] = new AprSubmission(commandBuffer, ctx[CpuRegister.Rsi], ResultAddress: 0);

        var completionResult = AmprExports.CompleteCommandBuffer(ctx, commandBuffer);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }

        TraceApr(ctx, "submit", submissionId, commandBuffer, ctx[CpuRegister.Rsi], 0);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qvMUCyyaCSI",
        ExportName = "sceKernelAprSubmitCommandBufferAndGetId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprSubmitCommandBufferAndGetId(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var outSubmissionId = ctx[CpuRegister.Rdx];
        if (commandBuffer == 0 || outSubmissionId == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        _submittedCommandBuffers[submissionId] = new AprSubmission(commandBuffer, ctx[CpuRegister.Rsi], ResultAddress: 0);

        var completionResult = AmprExports.CompleteCommandBuffer(ctx, commandBuffer);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }

        if (!ctx.TryWriteUInt32(outSubmissionId, submissionId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceApr(ctx, "submit_get_id", submissionId, commandBuffer, ctx[CpuRegister.Rsi], outSubmissionId);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Success stub: the argument layout is unknown and callers tolerate the
    // empty answer (Quake streams fine), so no output payload is written until
    // the real signature is reversed.
    [SysAbiExport(
        Nid = "WvEu7yl3Ivg",
        ExportName = "sceKernelAprGetFileSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAprGetFileSize(CpuContext ctx)
    {
        return ctx.SetReturn(0);
    }

    private static bool TryWriteAprResult(CpuContext ctx, ulong resultAddress)
    {
        Span<byte> result = stackalloc byte[sizeof(ulong)];
        result.Clear();
        return ctx.Memory.TryWrite(resultAddress, result);
    }

    private static void TraceApr(
        CpuContext ctx,
        string operation,
        uint submissionId,
        ulong commandBuffer,
        ulong priority,
        ulong aux)
    {
        if (!_traceApr)
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] apr.{operation}: id=0x{submissionId:X8} cmd=0x{commandBuffer:X16} priority=0x{priority:X16} aux=0x{aux:X16} ret=0x{returnRip:X16}");
        if (aux != 0 &&
            ctx.TryReadUInt64(aux, out var result0) &&
            ctx.TryReadUInt64(aux + sizeof(ulong), out var result1))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] apr.{operation}.result: addr=0x{aux:X16} q0=0x{result0:X16} q1=0x{result1:X16}");
        }
    }

    private static void TraceAprWaitFailure(
        CpuContext ctx,
        string operation,
        uint submissionId,
        ulong commandBuffer,
        ulong priority,
        ulong resultAddress)
    {
        if (!_traceApr)
        {
            return;
        }

        var traceCount = Interlocked.Increment(ref _aprWaitTraceCount);
        if (traceCount > 32 && (traceCount & 0x3FF) != 0)
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] apr.{operation}: id=0x{submissionId:X8} cmd=0x{commandBuffer:X16} " +
            $"rsi=0x{priority:X16} rdx=0x{resultAddress:X16} rcx=0x{ctx[CpuRegister.Rcx]:X16} " +
            $"r8=0x{ctx[CpuRegister.R8]:X16} r9=0x{ctx[CpuRegister.R9]:X16} ret=0x{returnRip:X16}");
        TraceReadableQword(ctx, operation, "rsi", priority);
        TraceReadableQword(ctx, operation, "rdx", resultAddress);
        TraceReadableQword(ctx, operation, "rcx", ctx[CpuRegister.Rcx]);
        TraceReadableQword(ctx, operation, "r8", ctx[CpuRegister.R8]);
    }

    private static void TraceReadableQword(CpuContext ctx, string operation, string name, ulong address)
    {
        if (address == 0 || !ctx.TryReadUInt64(address, out var value))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] apr.{operation}.{name}: addr=0x{address:X16} q0=0x{value:X16}");
    }
}
