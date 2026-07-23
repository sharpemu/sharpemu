// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AjmExports
{
    private const int OrbisAjmErrorInvalidContext = unchecked((int)0x80930002);
    private const int OrbisAjmErrorInvalidInstance = unchecked((int)0x80930003);
    private const int OrbisAjmErrorInvalidParameter = unchecked((int)0x80930005);
    private const int OrbisAjmErrorOutOfResources = unchecked((int)0x80930007);
    private const int OrbisAjmErrorCodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int OrbisAjmErrorCodecNotRegistered = unchecked((int)0x8093000A);
    private const int OrbisAjmErrorWrongRevisionFlag = unchecked((int)0x8093000B);
    private const uint MaxCodecType = 25;
    private const int MaxInstanceIndex = 0x2FFF;
    private static readonly ConcurrentDictionary<uint, AjmContextState> Contexts = new();
    private static int _nextContextId;
    private static int _nextBatchId;

    private const uint AjmCodecMp3 = 0;

    private sealed class AjmInstanceState
    {
        public required uint Codec { get; init; }
        public required ulong Flags { get; init; }
        public AjmMp3Decoder? Mp3 { get; init; }

        public bool PreferPcm16
        {
            get
            {
                // AjmInstanceFlags: version:3, channels:4, format:3
                var encoding = (Flags >> 7) & 0x7;
                return encoding is 0 or 1; // S16 / S32 — we emit S16 for both
            }
        }
    }

    private sealed class AjmContextState
    {
        public object Gate { get; } = new();

        public HashSet<uint> RegisteredCodecs { get; } = new();

        public Dictionary<uint, AjmInstanceState> InstancesBySlot { get; } = new();

        public int NextInstanceIndex { get; set; }
    }

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
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        if (codecType >= MaxCodecType || outputAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        if ((flags & 0x7) == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorWrongRevisionFlag);
        }

        uint instanceId;
        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Contains(codecType))
            {
                return ctx.SetReturn(OrbisAjmErrorCodecNotRegistered);
            }

            if (state.InstancesBySlot.Count >= MaxInstanceIndex)
            {
                return ctx.SetReturn(OrbisAjmErrorOutOfResources);
            }

            var nextInstanceIndex = state.NextInstanceIndex;
            uint instanceSlot;
            do
            {
                nextInstanceIndex = nextInstanceIndex % MaxInstanceIndex + 1;
                instanceSlot = unchecked((uint)nextInstanceIndex);
            }
            while (state.InstancesBySlot.ContainsKey(instanceSlot));

            instanceId = (codecType << 14) | instanceSlot;
            Span<byte> value = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(value, instanceId);
            if (!ctx.Memory.TryWrite(outputAddress, value))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }

            state.NextInstanceIndex = nextInstanceIndex;
            state.InstancesBySlot.Add(
                instanceSlot,
                new AjmInstanceState
                {
                    Codec = codecType,
                    Flags = flags,
                    Mp3 = codecType == AjmCodecMp3 ? new AjmMp3Decoder() : null,
                });
        }

        Trace($"instance_create context={contextId} codec={codecType} flags=0x{flags:X} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RbLbuKv8zho",
        ExportName = "sceAjmInstanceDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmInstanceDestroy(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        var instanceSlot = instanceId & 0x3FFF;
        lock (state.Gate)
        {
            if (instanceSlot == 0 || !state.InstancesBySlot.Remove(instanceSlot))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidInstance);
            }
        }

        Trace($"instance_destroy context={contextId} instance=0x{instanceId:X8}");
        return ctx.SetReturn(0);
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
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        // The caller owns and initializes the batch storage. This API resets
        // its submission cursor on hardware; FMOD does not consume a return
        // value or an additional output object here.
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    /// <summary>
    /// Enqueues a decode job on a batch. GTA V Enhanced streams menu music
    /// through AJM MP3 (codec 0); we decode eagerly here so BatchStart/Wait
    /// stay synchronous no-ops.
    /// </summary>
    [SysAbiExport(
        Nid = "39WxhR-ePew",
        ExportName = "sceAjmBatchJobDecode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobDecode(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var inputAddress = ctx[CpuRegister.Rdx];
        var inputSize = ctx[CpuRegister.Rcx];
        var outputAddress = ctx[CpuRegister.R8];
        var outputSize = ctx[CpuRegister.R9];
        var resultAddress = ReadStackArg64(ctx, 0);

        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        _ = TryAppendBatchJob(ctx, infoAddress, AjmJobRunSize);

        var inputConsumed = 0;
        var outputWritten = 0;
        ulong totalSamples = 0;
        var frames = 0u;
        var decoded = false;

        if (TryGetInstance(instanceId, out var instance) &&
            instance.Mp3 is not null &&
            inputAddress != 0 &&
            inputSize is > 0 and <= MaxSilentPcmBytes &&
            outputAddress != 0 &&
            outputSize is > 0 and <= MaxSilentPcmBytes)
        {
            var input = new byte[inputSize];
            var output = new byte[outputSize];
            if (ctx.Memory.TryRead(inputAddress, input))
            {
                var result = instance.Mp3.Decode(input, output, pcm16: instance.PreferPcm16);
                if (result.OutputWritten > 0)
                {
                    if (!ctx.Memory.TryWrite(outputAddress, output.AsSpan(0, result.OutputWritten)))
                    {
                        return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
                    }

                    // Zero any remainder so stale PCM does not leak into FMOD.
                    if ((ulong)result.OutputWritten < outputSize)
                    {
                        ClearGuestMemory(
                            ctx,
                            outputAddress + (ulong)result.OutputWritten,
                            outputSize - (ulong)result.OutputWritten);
                    }

                    decoded = true;
                    inputConsumed = result.InputConsumed;
                    outputWritten = result.OutputWritten;
                    frames = result.Frames;
                    totalSamples = instance.Mp3.TotalDecodedSamples;
                }
                else
                {
                    inputConsumed = result.InputConsumed;
                    totalSamples = instance.Mp3.TotalDecodedSamples;
                }
            }
        }

        if (!decoded)
        {
            // Fallback: silence + consume input so the guest does not spin.
            if (outputAddress != 0 && outputSize != 0 && outputSize <= MaxSilentPcmBytes)
            {
                ClearGuestMemory(ctx, outputAddress, outputSize);
            }

            if (inputConsumed == 0)
            {
                inputConsumed = inputSize > int.MaxValue ? int.MaxValue : (int)inputSize;
            }

            if (frames == 0 && (inputSize != 0 || outputSize != 0))
            {
                frames = 1;
            }
        }

        WriteDecodeStreamResult(
            ctx,
            resultAddress,
            inputConsumed,
            outputWritten,
            totalSamples,
            frames);

        Trace(
            $"batch_job_decode info=0x{infoAddress:X16} instance=0x{instanceId:X8} " +
            $"in=0x{inputAddress:X16}+0x{inputSize:X} out=0x{outputAddress:X16}+0x{outputSize:X} " +
            $"written={outputWritten} frames={frames} result=0x{resultAddress:X16}");
        return ctx.SetReturn(0);
    }

    private static bool TryGetInstance(uint instanceId, out AjmInstanceState instance)
    {
        instance = null!;
        var codec = instanceId >> 14;
        var slot = instanceId & 0x3FFF;
        if (slot == 0)
        {
            return false;
        }

        foreach (var context in Contexts.Values)
        {
            lock (context.Gate)
            {
                if (context.InstancesBySlot.TryGetValue(slot, out var found) &&
                    found.Codec == codec)
                {
                    instance = found;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Submits a built batch. Hot path after BatchJobDecode; unresolved WARNs
    /// dominate the log. Instant-complete: publish a batch id and clear any
    /// error out. Decode sidebands were already filled at job-enqueue time.
    /// </summary>
    [SysAbiExport(
        Nid = "5tOfnaClcqM",
        ExportName = "sceAjmBatchStart",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchStart(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var infoAddress = ctx[CpuRegister.Rsi];
        var priority = unchecked((int)ctx[CpuRegister.Rdx]);
        var errorAddress = ctx[CpuRegister.Rcx];
        var batchOutAddress = ctx[CpuRegister.R8];

        if (infoAddress == 0 || batchOutAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        ClearAjmBatchError(ctx, errorAddress);

        var batchId = unchecked((uint)Interlocked.Increment(ref _nextBatchId));
        Span<byte> batchValue = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(batchValue, batchId);
        if (!ctx.Memory.TryWrite(batchOutAddress, batchValue))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Trace(
            $"batch_start context={contextId} info=0x{infoAddress:X16} " +
            $"priority={priority} batch={batchId} error=0x{errorAddress:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "-qLsfDAywIY",
        ExportName = "sceAjmBatchWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchWait(CpuContext ctx)
    {
        // Batches complete synchronously in Start; Wait is a no-op success.
        var errorAddress = ctx[CpuRegister.Rcx];
        ClearAjmBatchError(ctx, errorAddress);
        Trace(
            $"batch_wait context={unchecked((uint)ctx[CpuRegister.Rdi])} " +
            $"batch={unchecked((uint)ctx[CpuRegister.Rsi])} " +
            $"timeout={unchecked((uint)ctx[CpuRegister.Rdx])}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "NVDXiUesSbA",
        ExportName = "sceAjmBatchCancel",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchCancel(CpuContext ctx)
    {
        Trace(
            $"batch_cancel context={unchecked((uint)ctx[CpuRegister.Rdi])} " +
            $"batch={unchecked((uint)ctx[CpuRegister.Rsi])}");
        return ctx.SetReturn(0);
    }

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _nextBatchId, 0);
    }

    // AjmBatchInfo: buffer, offset, size, last_good_job, last_good_job_ra (5× u64).
    private const ulong AjmBatchInfoOffsetField = 8;
    private const ulong AjmBatchInfoSizeField = 16;
    private const ulong AjmBatchInfoLastGoodJobField = 24;
    private const ulong AjmJobRunSize = 64;
    private const int OrbisAjmErrorJobCreation = unchecked((int)0x80930012);
    private const ulong MaxSilentPcmBytes = 1 << 20;
    // AjmSidebandResult (8) + AjmSidebandStream (16) + AjmSidebandMFrame (8).
    private const int DecodeSidebandBytes = 32;

    private static bool TryAppendBatchJob(CpuContext ctx, ulong infoAddress, ulong jobSize)
    {
        if (!TryReadUInt64(ctx, infoAddress, out var buffer) ||
            !TryReadUInt64(ctx, infoAddress + AjmBatchInfoOffsetField, out var offset) ||
            !TryReadUInt64(ctx, infoAddress + AjmBatchInfoSizeField, out var size))
        {
            return false;
        }

        if (buffer == 0 || jobSize == 0 || offset > size || size - offset < jobSize)
        {
            return false;
        }

        var jobAddress = buffer + offset;
        ClearGuestMemory(ctx, jobAddress, jobSize);
        return TryWriteUInt64(ctx, infoAddress + AjmBatchInfoLastGoodJobField, jobAddress) &&
               TryWriteUInt64(ctx, infoAddress + AjmBatchInfoOffsetField, offset + jobSize);
    }

    // AjmBatchError: int error_code; const void* job_addr; uint32_t cmd_offset; const void* job_ra;
    private const int AjmBatchErrorBytes = 24;

    private static void ClearAjmBatchError(CpuContext ctx, ulong errorAddress)
    {
        if (errorAddress == 0)
        {
            return;
        }

        Span<byte> error = stackalloc byte[AjmBatchErrorBytes];
        error.Clear();
        _ = ctx.Memory.TryWrite(errorAddress, error);
    }

    private static void WriteDecodeStreamResult(
        CpuContext ctx,
        ulong resultAddress,
        int inputConsumed,
        int outputWritten,
        ulong totalDecodedSamples,
        uint frames)
    {
        if (resultAddress == 0)
        {
            return;
        }

        Span<byte> sideband = stackalloc byte[DecodeSidebandBytes];
        sideband.Clear();
        // AjmSidebandResult.result / internal_result = 0 (OK)
        BinaryPrimitives.WriteInt32LittleEndian(sideband.Slice(8, 4), inputConsumed);
        BinaryPrimitives.WriteInt32LittleEndian(sideband.Slice(12, 4), outputWritten);
        BinaryPrimitives.WriteUInt64LittleEndian(sideband.Slice(16, 8), totalDecodedSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(sideband.Slice(24, 4), frames);
        _ = ctx.Memory.TryWrite(resultAddress, sideband);
    }

    private static void ClearGuestMemory(CpuContext ctx, ulong address, ulong byteCount)
    {
        if (address == 0 || byteCount == 0)
        {
            return;
        }

        var remaining = byteCount;
        var cursor = address;
        Span<byte> zero = stackalloc byte[256];
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, (ulong)zero.Length);
            if (!ctx.Memory.TryWrite(cursor, zero[..chunk]))
            {
                return;
            }

            cursor += (ulong)chunk;
            remaining -= (ulong)chunk;
        }
    }

    private static ulong ReadStackArg64(CpuContext ctx, int index)
    {
        var address = ctx[CpuRegister.Rsp] + sizeof(ulong) + ((ulong)index * sizeof(ulong));
        return TryReadUInt64(ctx, address, out var value) ? value : 0;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.{message}");
        }
    }
}
