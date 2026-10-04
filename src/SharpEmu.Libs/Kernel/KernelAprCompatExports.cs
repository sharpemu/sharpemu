// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using System.Buffers.Binary;
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

    private readonly record struct AprSubmission(
        ulong CommandBuffer,
        ulong Priority,
        ulong ResultAddress,
        int ExecutionResult,
        uint ErrorOffset);

    public static void ResetRuntimeState()
    {
        _submittedCommandBuffers.Clear();
        Volatile.Write(ref _nextSubmissionId, 0);
        Volatile.Write(ref _aprWaitTraceCount, 0);
    }

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

        var submissionId = AllocateSubmissionId();

        var completionResult = AmprExports.CompleteCommandBuffer(
            ctx,
            commandBuffer,
            out var executionResult,
            out var errorOffset);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }
        _submittedCommandBuffers[submissionId] =
            new AprSubmission(commandBuffer, priority, resultAddress, executionResult, errorOffset);

        if (outSubmissionId != 0 && !ctx.TryWriteUInt32(outSubmissionId, submissionId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (resultAddress != 0 && !TryWriteAprResult(ctx, resultAddress, executionResult, errorOffset))
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

        if (submission.ResultAddress != 0 &&
            !TryWriteAprResult(ctx, submission.ResultAddress, submission.ExecutionResult, submission.ErrorOffset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

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

        var submissionId = AllocateSubmissionId();
        var completionResult = AmprExports.CompleteCommandBuffer(
            ctx,
            commandBuffer,
            out var executionResult,
            out var errorOffset);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }
        _submittedCommandBuffers[submissionId] =
            new AprSubmission(commandBuffer, ctx[CpuRegister.Rsi], 0, executionResult, errorOffset);

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

        var submissionId = AllocateSubmissionId();
        var completionResult = AmprExports.CompleteCommandBuffer(
            ctx,
            commandBuffer,
            out var executionResult,
            out var errorOffset);
        if (completionResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return completionResult;
        }
        _submittedCommandBuffers[submissionId] =
            new AprSubmission(commandBuffer, ctx[CpuRegister.Rsi], 0, executionResult, errorOffset);

        if (!ctx.TryWriteUInt32(outSubmissionId, submissionId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceApr(ctx, "submit_get_id", submissionId, commandBuffer, ctx[CpuRegister.Rsi], outSubmissionId);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static uint RegisterCompletedSubmission(
        ulong commandBuffer,
        ulong priority,
        ulong resultAddress,
        int executionResult,
        uint errorOffset)
    {
        var submissionId = AllocateSubmissionId();
        _submittedCommandBuffers[submissionId] =
            new AprSubmission(commandBuffer, priority, resultAddress, executionResult, errorOffset);
        return submissionId;
    }

    internal static bool TryTakeCompletedSubmission(
        uint submissionId,
        out ulong resultAddress,
        out int executionResult,
        out uint errorOffset)
    {
        if (_submittedCommandBuffers.TryRemove(submissionId, out var submission))
        {
            resultAddress = submission.ResultAddress;
            executionResult = submission.ExecutionResult;
            errorOffset = submission.ErrorOffset;
            return true;
        }

        resultAddress = 0;
        executionResult = 0;
        errorOffset = 0;
        return false;
    }

    private static uint AllocateSubmissionId()
    {
        var submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        if (submissionId == 0)
            submissionId = unchecked((uint)Interlocked.Increment(ref _nextSubmissionId));
        return submissionId;
    }

    internal static bool TryWriteAprResult(
        CpuContext ctx,
        ulong resultAddress,
        int executionResult,
        uint errorOffset)
    {
        Span<byte> result = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteInt32LittleEndian(result, executionResult);
        BinaryPrimitives.WriteUInt32LittleEndian(result[sizeof(int)..], errorOffset);
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
