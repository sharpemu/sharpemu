// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AjmExports
{
    private const int OrbisAjmErrorInvalidContext = unchecked((int)0x80930002);
    private const int OrbisAjmErrorInvalidInstance = unchecked((int)0x80930003);
    private const int OrbisAjmErrorInvalidBatch = unchecked((int)0x80930004);
    private const int OrbisAjmErrorInvalidParameter = unchecked((int)0x80930005);
    private const int OrbisAjmErrorOutOfMemory = unchecked((int)0x80930006);
    private const int OrbisAjmErrorOutOfResources = unchecked((int)0x80930007);
    private const int OrbisAjmErrorCodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int OrbisAjmErrorCodecNotRegistered = unchecked((int)0x8093000A);
    private const int OrbisAjmErrorWrongRevisionFlag = unchecked((int)0x8093000B);
    private const int OrbisAjmErrorMalformedBatch = unchecked((int)0x80930011);
    private const int OrbisAjmErrorBufferTooBig = unchecked((int)0x80930015);
    private const int OrbisAjmErrorInvalidAddress = unchecked((int)0x80930016);
    private const uint MaxCodecType = 23;
    private const int MaxInstanceIndex = 0x2FFF;
    private const ulong MaxJobBufferSize = 64UL * 1024UL * 1024UL;
    private static readonly ConcurrentDictionary<uint, AjmContextState> Contexts = new();
    private static readonly ConditionalWeakTable<ICpuMemory, AjmMemoryState> MemoryStates = new();
    private static int _nextContextId;
    private static long _batchTraceCount;

    private sealed class AjmContextState
    {
        public object Gate { get; } = new();

        public HashSet<uint> RegisteredCodecs { get; } = new();

        public Dictionary<uint, AjmInstanceState> Instances { get; } = new();

        public HashSet<uint> CompletedBatches { get; } = new();

        public int NextInstanceIndex { get; set; }

        public uint NextBatchId { get; set; }
    }

    private sealed class AjmInstanceState(ulong flags)
    {
        public ulong Flags { get; } = flags;

        public ulong TotalDecodedSamples { get; set; }
    }

    private sealed class AjmMemoryState
    {
        public object Gate { get; } = new();

        public Dictionary<ulong, AjmBatchBuilder> Builders { get; } = new();
    }

    private sealed class AjmBatchBuilder
    {
        public List<AjmDecodeJob> DecodeJobs { get; } = new();
    }

    private readonly record struct AjmDecodeJob(
        uint InstanceId,
        ulong InputAddress,
        ulong InputSize,
        ulong OutputAddress,
        ulong OutputSize,
        ulong SidebandAddress);

    public static int AjmInitialize(CpuContext ctx)
    {
        var reserved = ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        if (reserved != 0 || outputAddress == 0)
        {
            return unchecked((int)0x806A0001);
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, contextId);
        if (!ctx.Memory.TryWrite(outputAddress, value))
        {
            return unchecked((int)0x806A0001);
        }

        Contexts[contextId] = new AjmContextState();
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.initialize reserved={reserved} out=0x{outputAddress:X16} context={contextId}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MHur6qCsUus",
        ExportName = "sceAjmFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmFinalize(CpuContext ctx)
    {
        Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _);
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "Q3dyFuwGn64",
        ExportName = "sceAjmModuleRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var reserved = ctx[CpuRegister.Rdx];
        if (codecType >= MaxCodecType || reserved != 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Add(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecAlreadyRegistered);
            }
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ajm.module_register context={contextId} codec={codecType} reserved={reserved}");
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "AxoDrINp4J8",
        ExportName = "sceAjmInstanceCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceCreate(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var outputAddress = ctx[CpuRegister.Rcx];
        if (codecType >= MaxCodecType || outputAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if ((flags & 0x7) == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorWrongRevisionFlag);
        }

        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        uint instanceId;
        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Contains(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecNotRegistered);
            }

            if (state.NextInstanceIndex >= MaxInstanceIndex)
            {
                return ctx.SetReturn(OrbisAjmErrorOutOfResources);
            }

            instanceId = (codecType << 14) | unchecked((uint)++state.NextInstanceIndex);
            state.Instances[instanceId] = new AjmInstanceState(flags);
        }

        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, instanceId);
        return ctx.Memory.TryWrite(outputAddress, value)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisAjmErrorInvalidParameter);
    }

    [SysAbiExport(
        Nid = "Wi7DtlLV+KI",
        ExportName = "sceAjmModuleUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmModuleUnregister(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        var initializationAddress = ctx[CpuRegister.Rdi];
        var initializationSize = ctx[CpuRegister.Rsi];
        var batchAddress = ctx[CpuRegister.Rdx];
        if (batchAddress == 0 || initializationSize > MaxJobBufferSize)
        {
            return ctx.SetReturn(
                initializationSize > MaxJobBufferSize
                    ? OrbisAjmErrorBufferTooBig
                    : OrbisAjmErrorInvalidParameter);
        }

        if (!IsReadableRange(ctx, initializationAddress, initializationSize))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidAddress);
        }

        var memoryState = MemoryStates.GetValue(ctx.Memory, static _ => new AjmMemoryState());
        lock (memoryState.Gate)
        {
            memoryState.Builders[batchAddress] = new AjmBatchBuilder();
        }

        TraceBatch($"initialize batch=0x{batchAddress:X16} init=0x{initializationAddress:X16}+0x{initializationSize:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "39WxhR-ePew",
        ExportName = "sceAjmBatchJobDecode",
        Target = Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobDecode(CpuContext ctx)
    {
        var batchAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var inputAddress = ctx[CpuRegister.Rdx];
        var inputSize = ctx[CpuRegister.Rcx];
        var outputAddress = ctx[CpuRegister.R8];
        var outputSize = ctx[CpuRegister.R9];
        var rsp = ctx[CpuRegister.Rsp];
        if (!ctx.TryReadUInt64(rsp + sizeof(ulong), out var sidebandAddress))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidAddress);
        }

        if (batchAddress == 0 || instanceId == 0 || sidebandAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (inputSize > MaxJobBufferSize || outputSize > MaxJobBufferSize)
        {
            return ctx.SetReturn(OrbisAjmErrorBufferTooBig);
        }

        if (!IsReadableRange(ctx, inputAddress, inputSize) ||
            (outputSize != 0 && outputAddress == 0))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidAddress);
        }

        var memoryState = MemoryStates.GetValue(ctx.Memory, static _ => new AjmMemoryState());
        lock (memoryState.Gate)
        {
            if (!memoryState.Builders.TryGetValue(batchAddress, out var builder))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidBatch);
            }

            builder.DecodeJobs.Add(new AjmDecodeJob(
                instanceId,
                inputAddress,
                inputSize,
                outputAddress,
                outputSize,
                sidebandAddress));
        }

        TraceBatch(
            $"decode batch=0x{batchAddress:X16} instance={instanceId} " +
            $"input=0x{inputAddress:X16}+0x{inputSize:X} output=0x{outputAddress:X16}+0x{outputSize:X}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "5tOfnaClcqM",
        ExportName = "sceAjmBatchStart",
        Target = Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchStart(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var batchAddress = ctx[CpuRegister.Rsi];
        var batchSize = ctx[CpuRegister.Rdx];
        var priority = unchecked((int)ctx[CpuRegister.Rcx]);
        var outBatchIdAddress = ctx[CpuRegister.R8];
        if (batchAddress == 0 || outBatchIdAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if (batchSize == 0 || (batchSize & 7) != 0)
        {
            return ctx.SetReturn(OrbisAjmErrorMalformedBatch);
        }

        if (!Contexts.TryGetValue(contextId, out var context))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        List<AjmDecodeJob> jobs;
        var memoryState = MemoryStates.GetValue(ctx.Memory, static _ => new AjmMemoryState());
        lock (memoryState.Gate)
        {
            if (!memoryState.Builders.TryGetValue(batchAddress, out var builder))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidBatch);
            }

            jobs = new List<AjmDecodeJob>(builder.DecodeJobs);
        }

        Dictionary<uint, AjmInstanceState> instances;
        lock (context.Gate)
        {
            instances = new Dictionary<uint, AjmInstanceState>(jobs.Count);
            foreach (var job in jobs)
            {
                if (!context.Instances.TryGetValue(job.InstanceId, out var instance))
                {
                    return ctx.SetReturn(OrbisAjmErrorInvalidInstance);
                }

                instances[job.InstanceId] = instance;
            }
        }

        lock (memoryState.Gate)
        {
            memoryState.Builders.Remove(batchAddress);
        }

        foreach (var job in jobs)
        {
            if (!TryCompleteDecode(ctx, job, instances[job.InstanceId]))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidAddress);
            }
        }

        uint batchId;
        lock (context.Gate)
        {
            batchId = ++context.NextBatchId;
            if (batchId == 0)
            {
                batchId = ++context.NextBatchId;
            }

            if (!context.CompletedBatches.Add(batchId))
            {
                return ctx.SetReturn(OrbisAjmErrorOutOfMemory);
            }
        }

        Span<byte> value = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(value, batchId);
        if (!ctx.Memory.TryWrite(outBatchIdAddress, value))
        {
            lock (context.Gate)
            {
                context.CompletedBatches.Remove(batchId);
            }

            return ctx.SetReturn(OrbisAjmErrorInvalidAddress);
        }

        TraceBatch(
            $"start context={contextId} batch={batchId} address=0x{batchAddress:X16} " +
            $"size=0x{batchSize:X} priority={priority} jobs={jobs.Count}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "-qLsfDAywIY",
        ExportName = "sceAjmBatchWait",
        Target = Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchWait(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var batchId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var batchErrorAddress = ctx[CpuRegister.Rcx];
        if (!Contexts.TryGetValue(contextId, out var context))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        lock (context.Gate)
        {
            if (!context.CompletedBatches.Remove(batchId))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidBatch);
            }
        }

        if (batchErrorAddress != 0 && !TryZeroMemory(ctx, batchErrorAddress, 32))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidAddress);
        }

        TraceBatch($"wait context={contextId} batch={batchId}");
        return ctx.SetReturn(0);
    }

    private static bool TryCompleteDecode(CpuContext ctx, AjmDecodeJob job, AjmInstanceState instance)
    {
        if (!TryZeroMemory(ctx, job.OutputAddress, job.OutputSize) ||
            !TryZeroMemory(ctx, job.SidebandAddress, 32))
        {
            return false;
        }

        var inputConsumed = checked((int)job.InputSize);
        var outputWritten = checked((int)job.OutputSize);
        ulong totalDecodedSamples;
        lock (instance)
        {
            instance.TotalDecodedSamples += CalculateDecodedSamples(job.OutputSize, instance.Flags);
            totalDecodedSamples = instance.TotalDecodedSamples;
        }

        return ctx.TryWriteInt32(job.SidebandAddress, 0) &&
            ctx.TryWriteInt32(job.SidebandAddress + 4, 0) &&
            ctx.TryWriteInt32(job.SidebandAddress + 8, inputConsumed) &&
            ctx.TryWriteInt32(job.SidebandAddress + 12, outputWritten) &&
            ctx.TryWriteUInt64(job.SidebandAddress + 16, totalDecodedSamples);
    }

    private static ulong CalculateDecodedSamples(ulong outputSize, ulong instanceFlags)
    {
        var channels = (instanceFlags >> 3) & 0xF;
        if (channels == 0)
        {
            channels = 1;
        }

        var encoding = (instanceFlags >> 7) & 0x7;
        var bytesPerSample = encoding == 0 ? 2UL : 4UL;
        return outputSize / (channels * bytesPerSample);
    }

    private static bool IsReadableRange(CpuContext ctx, ulong address, ulong size)
    {
        if (size == 0)
        {
            return true;
        }

        if (address == 0 || address > ulong.MaxValue - (size - 1))
        {
            return false;
        }

        return ctx.TryReadByte(address, out _) && ctx.TryReadByte(address + size - 1, out _);
    }

    private static bool TryZeroMemory(CpuContext ctx, ulong address, ulong size)
    {
        if (size == 0)
        {
            return true;
        }

        if (address == 0 || size > MaxJobBufferSize || address > ulong.MaxValue - (size - 1))
        {
            return false;
        }

        Span<byte> zeros = stackalloc byte[4096];
        var remaining = size;
        var current = address;
        while (remaining != 0)
        {
            var chunkSize = (int)Math.Min((ulong)zeros.Length, remaining);
            if (!ctx.Memory.TryWrite(current, zeros[..chunkSize]))
            {
                return false;
            }

            current += unchecked((ulong)chunkSize);
            remaining -= unchecked((ulong)chunkSize);
        }

        return true;
    }

    private static void TraceBatch(string message)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var count = Interlocked.Increment(ref _batchTraceCount);
        if (count <= 16 || count % 1000 == 0)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.{message}");
        }
    }
}
