// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using LibAtrac9;
using SharpEmu.HLE;
using System.Buffers;
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
    private const int OrbisAjmErrorJobCreation = unchecked((int)0x80930012);
    private const ulong MaxSilentPcmBytes = 1 << 20;
    private const uint Atrac9CodecType = 1;
    // instanceId packs codecType into the high bits and the instance slot
    // into the low InstanceIdSlotBits bits (see AjmInstanceCreate's
    // `(codecType << InstanceIdSlotBits) | instanceSlot` and the
    // `& InstanceIdSlotMask` unpacks in AjmInstanceDestroy/GetError).
    private const int InstanceIdSlotBits = 14;
    private const uint InstanceIdSlotMask = (1u << InstanceIdSlotBits) - 1;
    // Registration is pure bookkeeping (a HashSet.Add), so the only real
    // constraint is that codecType must not overflow the 32-bit instanceId
    // once shifted left by InstanceIdSlotBits -- not any hardcoded list of
    // known Sony codec ids, which a retail title's Gen5 codec type (e.g. 24)
    // can legitimately fall outside of.
    private const uint MaxCodecType = 1u << (32 - InstanceIdSlotBits);
    private const int MaxInstanceIndex = 0x2FFF;
    private const int MaxDecodeBufferBytes = 64 * 1024 * 1024;

    // AjmJobFlags: version:3, codec:8, run_flags:2, control_flags:3,
    // reserved:29, sideband_flags:3.
    private const ulong AjmJobRunFlagGetCodecInfo = 1UL << 11;
    private const ulong AjmJobRunFlagMultipleFrames = 1UL << 12;
    private const ulong AjmJobSidebandFlagGaplessDecode = 1UL << 45;
    private const ulong AjmJobSidebandFlagFormat = 1UL << 46;
    private const ulong AjmJobSidebandFlagStream = 1UL << 47;

    // AjmBuffer { u8* p_address; u64 size; }
    private const int AjmBufferDescriptorBytes = 16;
    private const int MaxBufferDescriptors = 64;
    private static readonly ConcurrentDictionary<uint, AjmContextState> Contexts = new();
    private static int _nextContextId;
    private static int _nextBatchId;

    private const uint AjmCodecMp3 = 0;

    private sealed class AjmInstanceState
    {
        public required uint Id { get; init; }

        public required uint Codec { get; init; }

        public required ulong Flags { get; init; }

        /// <summary>Channel ceiling from the instance flags; the decoded stream's
        /// own config decides the actual PCM layout.</summary>
        public required int MaxChannels { get; init; }

        public required Atrac9PcmEncoding Encoding { get; init; }

        public Atrac9DecodeState? Atrac9 { get; init; }

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

        public Dictionary<ulong, ulong> RegisteredMemoryPages { get; } = new();

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
        if (!Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "bkRHEYG6lEM",
        ExportName = "sceAjmMemoryRegister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmMemoryRegister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var address = ctx[CpuRegister.Rsi];
        var pages = ctx[CpuRegister.Rdx];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        if (address == 0 || pages == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        // AJM hardware requires explicit registration. SharpEmu decodes from
        // the shared guest address space, so retaining the range is enough.
        lock (state.Gate)
        {
            state.RegisteredMemoryPages[address] = pages;
        }

        Trace($"memory_register context={contextId} address=0x{address:X16} pages={pages}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "pIpGiaYkHkM",
        ExportName = "sceAjmMemoryUnregister",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmMemoryUnregister(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var address = ctx[CpuRegister.Rsi];
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        if (address == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        lock (state.Gate)
        {
            state.RegisteredMemoryPages.Remove(address);
        }

        Trace($"memory_unregister context={contextId} address=0x{address:X16}");
        return ctx.SetReturn(0);
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
            return TraceInstanceCreateFailure(ctx, contextId, codecType, flags, OrbisAjmErrorInvalidContext);
        }

        if (codecType >= MaxCodecType || outputAddress == 0)
        {
            return TraceInstanceCreateFailure(ctx, contextId, codecType, flags, OrbisAjmErrorInvalidParameter);
        }

        // AjmInstanceFlags carries a codec id in the high bits plus a low
        // channel/revision word whose exact split is not settled: Demon's Souls
        // creates ATRAC9 voices with low words 1, 2, 4 and 8. Rejecting any of
        // them leaves the title holding SCE_AJM_INSTANCE_INVALID for that voice
        // and it plays silence forever, so nothing here is fatal — the decoded
        // stream's own config is what actually describes the PCM anyway.
        var maxChannels = unchecked((int)(flags & 0xF));
        var encoding = GetPcmEncoding(flags);

        uint instanceId;
        lock (state.Gate)
        {
            if (!state.RegisteredCodecs.Contains(codecType))
            {
                return TraceInstanceCreateFailure(ctx, contextId, codecType, flags, OrbisAjmErrorCodecNotRegistered);
            }

            if (state.InstancesBySlot.Count >= MaxInstanceIndex)
            {
                return TraceInstanceCreateFailure(ctx, contextId, codecType, flags, OrbisAjmErrorOutOfResources);
            }

            var nextInstanceIndex = state.NextInstanceIndex;
            uint instanceSlot;
            do
            {
                nextInstanceIndex = nextInstanceIndex % MaxInstanceIndex + 1;
                instanceSlot = unchecked((uint)nextInstanceIndex);
            }
            while (state.InstancesBySlot.ContainsKey(instanceSlot));

            instanceId = (codecType << InstanceIdSlotBits) | instanceSlot;
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
                    Id = instanceId,
                    Codec = codecType,
                    Flags = flags,
                    MaxChannels = maxChannels,
                    Encoding = encoding,
                    Atrac9 = codecType == Atrac9CodecType ? new Atrac9DecodeState() : null,
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

        var instanceSlot = instanceId & InstanceIdSlotMask;
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
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var codecType = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var state))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidContext);
        }

        bool removed;
        lock (state.Gate)
        {
            removed = state.RegisteredCodecs.Remove(codecType);
        }

        Trace($"module_unregister context={contextId} codec={codecType} was_registered={removed}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "MmpF1XsQiHw",
        ExportName = "sceAjmBatchInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchInitialize(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rdi];
        var bufferSize = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];
        if (bufferAddress == 0 || infoAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Span<byte> info = stackalloc byte[AjmBatchInfoBytes];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info, bufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(info[16..], bufferSize);
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Trace($"batch_initialize buffer=0x{bufferAddress:X16} size=0x{bufferSize:X} info=0x{infoAddress:X16}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "1t3ixYNXyuc",
        ExportName = "sceAjmDecAt9ParseConfigData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmDecAt9ParseConfigData(CpuContext ctx)
    {
        var configAddress = ctx[CpuRegister.Rdi];
        var outputAddress = ctx[CpuRegister.Rsi];
        if (configAddress == 0 || outputAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        Span<byte> configData = stackalloc byte[4];
        if (!ctx.Memory.TryRead(configAddress, configData))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        try
        {
            var config = new LibAtrac9.Atrac9Config(configData.ToArray());
            Span<byte> info = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(info[0..], unchecked((uint)config.ChannelCount));
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], unchecked((uint)config.SampleRate));
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], unchecked((uint)config.FrameSamples));
            BinaryPrimitives.WriteUInt32LittleEndian(info[12..], unchecked((uint)config.SuperframeSamples));
            BinaryPrimitives.WriteUInt32LittleEndian(info[16..], unchecked((uint)config.SuperframeBytes));
            return ctx.SetReturn(
                ctx.Memory.TryWrite(outputAddress, info)
                    ? 0
                    : OrbisAjmErrorInvalidParameter);
        }
        catch (InvalidDataException)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }
    }

    [SysAbiExport(
        Nid = "ezM2OhNxzck",
        ExportName = "sceAjmBatchJobInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobInitialize(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var parametersAddress = ctx[CpuRegister.Rdx];
        var parametersSize = ctx[CpuRegister.Rcx];
        var resultAddress = ctx[CpuRegister.R8];

        if (!TryAppendBatchJob(ctx, infoAddress, AjmJobControlSize))
        {
            return ctx.SetReturn(OrbisAjmErrorJobCreation);
        }

        var status = Atrac9DecodeState.ResultInvalidParameter;
        if (TryGetInstance(instanceId, out var instance))
        {
            if (instance.Codec != Atrac9CodecType)
            {
                status = 0;
            }
            else if (instance.Atrac9 is not null &&
                     parametersAddress != 0 &&
                     parametersSize >= 4)
            {
                Span<byte> configData = stackalloc byte[4];
                if (ctx.Memory.TryRead(parametersAddress, configData) &&
                    instance.Atrac9.TryInitialize(configData))
                {
                    status = 0;
                }
            }
        }

        WriteBasicResult(ctx, resultAddress, status);
        Trace(
            $"batch_job_initialize instance=0x{instanceId:X8} params=0x{parametersAddress:X16}+0x{parametersSize:X} status=0x{status:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "uJ3m8INuikg",
        ExportName = "sceAjmBatchJobClearContext",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobClearContext(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var resultAddress = ctx[CpuRegister.Rdx];

        if (!TryAppendBatchJob(ctx, infoAddress, AjmJobControlSize))
        {
            return ctx.SetReturn(OrbisAjmErrorJobCreation);
        }

        var status = Atrac9DecodeState.ResultInvalidParameter;
        if (TryGetInstance(instanceId, out var instance))
        {
            instance.Atrac9?.Reset();
            status = 0;
        }

        WriteBasicResult(ctx, resultAddress, status);
        return ctx.SetReturn(0);
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
    public static int AjmBatchJobDecode(CpuContext ctx) =>
        AjmBatchJobDecodeCore(ctx, multipleFrames: true);

    [SysAbiExport(
        Nid = "5LLWbpP5xi8",
        ExportName = "sceAjmBatchJobDecodeSingle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobDecodeSingle(CpuContext ctx) =>
        AjmBatchJobDecodeCore(ctx, multipleFrames: false);

    /// <summary>
    /// The flag-driven decode entry point. Titles built on the modern AJM API
    /// drive ATRAC9 playback exclusively through Run/RunSplit —
    /// <see cref="AjmBatchJobDecode"/> is never called — so leaving these
    /// unresolved silences every streamed voice.
    /// </summary>
    [SysAbiExport(
        Nid = "jVCWcthifr8",
        ExportName = "sceAjmBatchJobRun",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobRun(CpuContext ctx) => AjmBatchJobRunCore(ctx, split: false);

    [SysAbiExport(
        Nid = "Z9NVCesiP0Q",
        ExportName = "sceAjmBatchJobRunSplit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobRunSplit(CpuContext ctx) => AjmBatchJobRunCore(ctx, split: true);

    // The BufferRa variants only append a guest return address after the nine
    // shared arguments, so they decode identically.
    [SysAbiExport(
        Nid = "ElslOCpOIns",
        ExportName = "sceAjmBatchJobRunBufferRa",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobRunBufferRa(CpuContext ctx) => AjmBatchJobRunCore(ctx, split: false);

    [SysAbiExport(
        Nid = "7jdAXK+2fMo",
        ExportName = "sceAjmBatchJobRunSplitBufferRa",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobRunSplitBufferRa(CpuContext ctx) => AjmBatchJobRunCore(ctx, split: true);

    /// <summary>
    /// Records the gapless-decode window for a looping voice. SharpEmu decodes
    /// whole superframes and does not trim lead-in/lead-out samples yet, so this
    /// only has to acknowledge the job instead of failing the batch.
    /// </summary>
    [SysAbiExport(
        Nid = "SkEwpiu3tZg",
        ExportName = "sceAjmBatchJobSetGaplessDecode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobSetGaplessDecode(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var gaplessAddress = ctx[CpuRegister.Rdx];
        var reset = unchecked((int)ctx[CpuRegister.Rcx]);
        var resultAddress = ctx[CpuRegister.R8];

        if (!TryAppendBatchJob(ctx, infoAddress, AjmJobControlSize))
        {
            return ctx.SetReturn(OrbisAjmErrorJobCreation);
        }

        var status = TryGetInstance(instanceId, out _) ? 0 : Atrac9DecodeState.ResultInvalidParameter;
        WriteBasicResult(ctx, resultAddress, status);
        Trace(
            $"batch_job_set_gapless_decode instance=0x{instanceId:X8} " +
            $"gapless=0x{gaplessAddress:X16} reset={reset} status=0x{status:X8}");
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "3cAg7xN995U",
        ExportName = "sceAjmBatchJobGetStatistics",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int AjmBatchJobGetStatistics(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var resultAddress = ctx[CpuRegister.Rsi];

        if (!TryAppendBatchJob(ctx, infoAddress, AjmJobGetStatisticsSize))
        {
            return ctx.SetReturn(OrbisAjmErrorJobCreation);
        }

        if (resultAddress != 0)
        {
            Span<byte> result = stackalloc byte[AjmStatisticsResultBytes];
            result.Clear();
            if (!ctx.Memory.TryWrite(resultAddress, result))
            {
                return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
            }
        }

        ctx.GetXmmRegister(0, out var intervalBits, out _);
        var interval = BitConverter.Int32BitsToSingle(unchecked((int)(uint)intervalBits));
        Trace($"batch_job_get_statistics interval={interval:R} result=0x{resultAddress:X16}");
        return ctx.SetReturn(0);
    }


    /// <summary>
    /// Decodes one MP3 job into the guest's output buffer. Reported through the
    /// same result shape as ATRAC9 so the sideband writer stays codec-agnostic.
    /// </summary>
    private static Atrac9DecodeResult DecodeMp3(
        CpuContext ctx,
        AjmInstanceState instance,
        ulong inputAddress,
        ulong inputSize,
        ulong outputAddress,
        ulong outputSize)
    {
        var mp3 = instance.Mp3!;
        if (inputAddress != 0 &&
            inputSize is > 0 and <= MaxDecodeBufferBytes &&
            outputAddress != 0 &&
            outputSize is > 0 and <= MaxDecodeBufferBytes)
        {
            var input = new byte[inputSize];
            var output = new byte[outputSize];
            if (ctx.Memory.TryRead(inputAddress, input))
            {
                var decoded = mp3.Decode(input, output, pcm16: instance.PreferPcm16);
                if (decoded.OutputWritten > 0 &&
                    ctx.Memory.TryWrite(outputAddress, output.AsSpan(0, decoded.OutputWritten)))
                {
                    // Zero any remainder so stale PCM does not leak into FMOD.
                    if ((ulong)decoded.OutputWritten < outputSize)
                    {
                        ClearGuestMemory(
                            ctx,
                            outputAddress + (ulong)decoded.OutputWritten,
                            outputSize - (ulong)decoded.OutputWritten);
                    }

                    return new Atrac9DecodeResult(
                        0,
                        decoded.InputConsumed,
                        decoded.OutputWritten,
                        mp3.TotalDecodedSamples,
                        decoded.Frames);
                }

                if (decoded.InputConsumed > 0)
                {
                    ClearGuestMemory(ctx, outputAddress, outputSize);
                    return new Atrac9DecodeResult(
                        0,
                        decoded.InputConsumed,
                        0,
                        mp3.TotalDecodedSamples,
                        0);
                }
            }
        }

        // Fallback: silence and consume the input so the guest does not spin.
        if (outputAddress != 0 && outputSize is > 0 and <= MaxDecodeBufferBytes)
        {
            ClearGuestMemory(ctx, outputAddress, outputSize);
        }

        return new Atrac9DecodeResult(
            0,
            unchecked((int)Math.Min(inputSize, int.MaxValue)),
            0,
            mp3.TotalDecodedSamples,
            inputSize != 0 || outputSize != 0 ? 1u : 0u);
    }

    private static bool TryGetInstance(uint instanceId, out AjmInstanceState instance)
    {
        instance = null!;
        var codec = instanceId >> InstanceIdSlotBits;
        var slot = instanceId & InstanceIdSlotMask;
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

    private static int AjmBatchJobDecodeCore(CpuContext ctx, bool multipleFrames)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var inputAddress = ctx[CpuRegister.Rdx];
        var inputSize = ctx[CpuRegister.Rcx];
        var outputAddress = ctx[CpuRegister.R8];
        var outputSize = ctx[CpuRegister.R9];
        var resultAddress = ReadStackArg64(ctx, 0);

        if (!TryAppendBatchJob(ctx, infoAddress, AjmJobRunSize))
        {
            return ctx.SetReturn(OrbisAjmErrorJobCreation);
        }

        Atrac9DecodeResult result;
        if (!TryGetInstance(instanceId, out var instance))
        {
            result = new Atrac9DecodeResult(
                Atrac9DecodeState.ResultInvalidParameter,
                0,
                0,
                0,
                0);
        }
        else if (instance.Mp3 is not null)
        {
            // GTA V Enhanced streams menu music through AJM MP3 (codec 0).
            // Decoded eagerly here so BatchStart/Wait stay synchronous no-ops.
            result = DecodeMp3(
                ctx,
                instance,
                inputAddress,
                inputSize,
                outputAddress,
                outputSize);
        }
        else if (instance.Codec != Atrac9CodecType || instance.Atrac9 is null)
        {
            if (outputAddress != 0 &&
                outputSize is > 0 and <= MaxDecodeBufferBytes)
            {
                ClearGuestMemory(ctx, outputAddress, outputSize);
            }

            result = new Atrac9DecodeResult(
                0,
                unchecked((int)Math.Min(inputSize, int.MaxValue)),
                0,
                0,
                inputSize != 0 || outputSize != 0 ? 1u : 0u);
        }
        else
        {
            result = DecodeAtrac9(
                ctx,
                instance,
                inputAddress,
                inputSize,
                outputAddress,
                outputSize,
                multipleFrames);
        }

        WriteDecodeStreamResult(ctx, resultAddress, result, multipleFrames);
        Trace(
            $"batch_job_decode instance=0x{instanceId:X8} in=0x{inputAddress:X16}+0x{inputSize:X} " +
            $"out=0x{outputAddress:X16}+0x{outputSize:X} consumed={result.InputConsumed} " +
            $"produced={result.OutputWritten} frames={result.Frames} status=0x{result.Status:X8}");
        return ctx.SetReturn(0);
    }

    private static int TraceInstanceCreateFailure(
        CpuContext ctx,
        uint contextId,
        uint codecType,
        ulong flags,
        int error)
    {
        Trace($"instance_create_failed context={contextId} codec={codecType} flags=0x{flags:X} error=0x{unchecked((uint)error):X8}");
        return ctx.SetReturn(error);
    }

    private readonly record struct AjmGuestBuffer(ulong Address, int Length);

    /// <summary>
    /// Shared body of sceAjmBatchJobRun / sceAjmBatchJobRunSplit. The split form
    /// passes arrays of AjmBuffer descriptors instead of one pointer/size pair;
    /// the descriptors are a single logical stream, so they are gathered on the
    /// way in and scattered on the way out.
    /// </summary>
    private static int AjmBatchJobRunCore(CpuContext ctx, bool split)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        var instanceId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var flags = ctx[CpuRegister.Rdx];
        var inputAddress = ctx[CpuRegister.Rcx];
        var inputCountOrSize = ctx[CpuRegister.R8];
        var outputAddress = ctx[CpuRegister.R9];
        var outputCountOrSize = ReadStackArg64(ctx, 0);
        var sidebandAddress = ReadStackArg64(ctx, 1);
        var sidebandSize = ReadStackArg64(ctx, 2);

        var descriptors = split
            ? Math.Min(inputCountOrSize, MaxBufferDescriptors) + Math.Min(outputCountOrSize, MaxBufferDescriptors)
            : 0;
        var jobSize = split
            ? AjmJobRunSplitBaseSize + (descriptors * AjmBufferDescriptorBytes)
            : AjmJobRunSize;
        if (!TryAppendBatchJob(ctx, infoAddress, jobSize))
        {
            return ctx.SetReturn(OrbisAjmErrorJobCreation);
        }

        var multipleFrames = (flags & AjmJobRunFlagMultipleFrames) != 0;
        Atrac9Config? config = null;
        Atrac9DecodeResult result;

        if (!TryGetInstance(instanceId, out var instance))
        {
            result = new Atrac9DecodeResult(Atrac9DecodeState.ResultInvalidParameter, 0, 0, 0, 0);
        }
        else if (!TryCollectBuffers(ctx, split, inputAddress, inputCountOrSize, out var inputs, out var inputLength) ||
                 !TryCollectBuffers(ctx, split, outputAddress, outputCountOrSize, out var outputs, out var outputLength))
        {
            result = new Atrac9DecodeResult(Atrac9DecodeState.ResultInvalidParameter, 0, 0, 0, 0);
        }
        else if (instance.Codec != Atrac9CodecType || instance.Atrac9 is null)
        {
            foreach (var buffer in outputs)
            {
                ClearGuestMemory(ctx, buffer.Address, (ulong)buffer.Length);
            }

            result = new Atrac9DecodeResult(
                0,
                inputLength,
                0,
                0,
                inputLength != 0 || outputLength != 0 ? 1u : 0u);
        }
        else
        {
            config = instance.Atrac9.Config;
            result = DecodeAtrac9Scattered(ctx, instance, inputs, inputLength, outputs, outputLength, multipleFrames);
            config ??= instance.Atrac9.Config;
        }

        WriteRunSideband(ctx, sidebandAddress, sidebandSize, flags, result, instance, config);
        Trace(
            $"batch_job_run{(split ? "_split" : string.Empty)} instance=0x{instanceId:X8} flags=0x{flags:X} " +
            $"in=0x{inputAddress:X16}#{inputCountOrSize} out=0x{outputAddress:X16}#{outputCountOrSize} " +
            $"sideband=0x{sidebandAddress:X16}+0x{sidebandSize:X} consumed={result.InputConsumed} " +
            $"produced={result.OutputWritten} frames={result.Frames} status=0x{result.Status:X8}");
        TraceBufferDescriptors(ctx, split, "in", inputAddress, inputCountOrSize);
        TraceBufferDescriptors(ctx, split, "out", outputAddress, outputCountOrSize);
        return ctx.SetReturn(0);
    }

    private static void TraceBufferDescriptors(
        CpuContext ctx,
        bool split,
        string label,
        ulong address,
        ulong countOrSize)
    {
        if (!split ||
            address == 0 ||
            countOrSize == 0 ||
            countOrSize > MaxBufferDescriptors ||
            !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Span<byte> descriptor = stackalloc byte[AjmBufferDescriptorBytes];
        // Hoisted out of the loop: a stackalloc per iteration accumulates across
        // up to MaxBufferDescriptors passes and never unwinds until the method
        // returns.
        Span<byte> head = stackalloc byte[16];
        for (var index = 0UL; index < countOrSize; index++)
        {
            if (!ctx.Memory.TryRead(address + (index * AjmBufferDescriptorBytes), descriptor))
            {
                return;
            }

            var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
            var bufferSize = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]);
            var readable = bufferAddress != 0 && bufferSize != 0 && ctx.Memory.TryRead(bufferAddress, head);
            Trace(
                $"buffer[{label}][{index}] address=0x{bufferAddress:X16} size=0x{bufferSize:X} " +
                $"head={(readable ? Convert.ToHexString(head) : "<unreadable>")}");
        }
    }

    private static bool TryCollectBuffers(
        CpuContext ctx,
        bool split,
        ulong address,
        ulong countOrSize,
        out List<AjmGuestBuffer> buffers,
        out int totalLength)
    {
        buffers = new List<AjmGuestBuffer>(split ? (int)Math.Min(countOrSize, MaxBufferDescriptors) : 1);
        totalLength = 0;

        if (!split)
        {
            if (countOrSize > MaxDecodeBufferBytes || (countOrSize != 0 && address == 0))
            {
                return false;
            }

            if (countOrSize != 0)
            {
                buffers.Add(new AjmGuestBuffer(address, unchecked((int)countOrSize)));
                totalLength = unchecked((int)countOrSize);
            }

            return true;
        }

        if (countOrSize > MaxBufferDescriptors || (countOrSize != 0 && address == 0))
        {
            return false;
        }

        Span<byte> descriptor = stackalloc byte[AjmBufferDescriptorBytes];
        for (var index = 0UL; index < countOrSize; index++)
        {
            if (!ctx.Memory.TryRead(address + (index * AjmBufferDescriptorBytes), descriptor))
            {
                return false;
            }

            var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
            var bufferSize = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]);
            if (bufferSize == 0)
            {
                continue;
            }

            if (bufferAddress == 0 ||
                bufferSize > MaxDecodeBufferBytes ||
                (ulong)totalLength + bufferSize > MaxDecodeBufferBytes)
            {
                return false;
            }

            buffers.Add(new AjmGuestBuffer(bufferAddress, unchecked((int)bufferSize)));
            totalLength += unchecked((int)bufferSize);
        }

        return true;
    }

    private static Atrac9DecodeResult DecodeAtrac9Scattered(
        CpuContext ctx,
        AjmInstanceState instance,
        List<AjmGuestBuffer> inputs,
        int inputLength,
        List<AjmGuestBuffer> outputs,
        int outputLength,
        bool multipleFrames)
    {
        var input = ArrayPool<byte>.Shared.Rent(Math.Max(inputLength, 1));
        var output = ArrayPool<byte>.Shared.Rent(Math.Max(outputLength, 1));
        try
        {
            var gathered = 0;
            foreach (var buffer in inputs)
            {
                if (!ctx.Memory.TryRead(buffer.Address, input.AsSpan(gathered, buffer.Length)))
                {
                    return new Atrac9DecodeResult(Atrac9DecodeState.ResultInvalidParameter, 0, 0, 0, 0);
                }

                gathered += buffer.Length;
            }

            var result = instance.Atrac9!.Decode(
                input.AsSpan(0, inputLength),
                output.AsSpan(0, outputLength),
                instance.Encoding,
                requestedChannels: 0,
                multipleFrames);

            var scattered = 0;
            foreach (var buffer in outputs)
            {
                if (scattered >= result.OutputWritten)
                {
                    break;
                }

                var chunk = Math.Min(buffer.Length, result.OutputWritten - scattered);
                if (!ctx.Memory.TryWrite(buffer.Address, output.AsSpan(scattered, chunk)))
                {
                    return result with
                    {
                        Status = result.Status | Atrac9DecodeState.ResultInvalidParameter,
                        OutputWritten = scattered,
                    };
                }

                scattered += chunk;
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    private static void WriteRunSideband(
        CpuContext ctx,
        ulong address,
        ulong size,
        ulong flags,
        Atrac9DecodeResult result,
        AjmInstanceState? instance,
        Atrac9Config? config)
    {
        if (address == 0 || size < AjmSidebandResultBytes)
        {
            return;
        }

        // Layout is positional and driven purely by the job flags:
        // Result, then Stream, Format, GaplessDecode, MFrame, CodecInfo — each
        // only present when its flag is set and there is still room.
        Span<byte> sideband = stackalloc byte[
            AjmSidebandResultBytes +
            AjmSidebandStreamBytes +
            AjmSidebandFormatBytes +
            AjmSidebandGaplessDecodeBytes +
            AjmSidebandMFrameBytes];
        sideband.Clear();

        BinaryPrimitives.WriteInt32LittleEndian(sideband, result.Status);
        var offset = AjmSidebandResultBytes;

        if ((flags & AjmJobSidebandFlagStream) != 0 && (ulong)(offset + AjmSidebandStreamBytes) <= size)
        {
            BinaryPrimitives.WriteInt32LittleEndian(sideband[offset..], result.InputConsumed);
            BinaryPrimitives.WriteInt32LittleEndian(sideband[(offset + 4)..], result.OutputWritten);
            BinaryPrimitives.WriteUInt64LittleEndian(sideband[(offset + 8)..], result.TotalDecodedSamples);
            offset += AjmSidebandStreamBytes;
        }

        if ((flags & AjmJobSidebandFlagFormat) != 0 && (ulong)(offset + AjmSidebandFormatBytes) <= size)
        {
            var channels = config?.ChannelCount ?? instance?.MaxChannels ?? 0;
            BinaryPrimitives.WriteUInt32LittleEndian(sideband[offset..], unchecked((uint)channels));
            BinaryPrimitives.WriteUInt32LittleEndian(sideband[(offset + 4)..], ChannelMaskFor(channels));
            BinaryPrimitives.WriteUInt32LittleEndian(sideband[(offset + 8)..], unchecked((uint)(config?.SampleRate ?? 0)));
            BinaryPrimitives.WriteUInt32LittleEndian(
                sideband[(offset + 12)..],
                unchecked((uint)(instance?.Encoding ?? Atrac9PcmEncoding.Signed16)));
            offset += AjmSidebandFormatBytes;
        }

        if ((flags & AjmJobSidebandFlagGaplessDecode) != 0 && (ulong)(offset + AjmSidebandGaplessDecodeBytes) <= size)
        {
            offset += AjmSidebandGaplessDecodeBytes;
        }

        if ((flags & AjmJobRunFlagMultipleFrames) != 0 && (ulong)(offset + AjmSidebandMFrameBytes) <= size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sideband[offset..], result.Frames);
            offset += AjmSidebandMFrameBytes;
        }

        _ = ctx.Memory.TryWrite(address, sideband[..offset]);

        if ((flags & AjmJobRunFlagGetCodecInfo) != 0 && (ulong)offset < size)
        {
            // No codec-info block is produced yet; zero it so the caller reads
            // defined values instead of whatever the buffer held before.
            ClearGuestMemory(
                ctx,
                address + (ulong)offset,
                Math.Min(size - (ulong)offset, AjmSidebandCodecInfoBytes));
        }
    }

    private static uint ChannelMaskFor(int channels) =>
        channels switch
        {
            1 => 0x4,
            2 => 0x3,
            6 => 0x3F,
            8 => 0x63F,
            _ => 0,
        };

    private static Atrac9DecodeResult DecodeAtrac9(
        CpuContext ctx,
        AjmInstanceState instance,
        ulong inputAddress,
        ulong inputSize,
        ulong outputAddress,
        ulong outputSize,
        bool multipleFrames)
    {
        if ((inputSize != 0 && inputAddress == 0) ||
            (outputSize != 0 && outputAddress == 0) ||
            inputSize > MaxDecodeBufferBytes ||
            outputSize > MaxDecodeBufferBytes)
        {
            return new Atrac9DecodeResult(
                Atrac9DecodeState.ResultInvalidParameter,
                0,
                0,
                0,
                0);
        }

        var inputLength = unchecked((int)inputSize);
        var outputLength = unchecked((int)outputSize);
        var input = ArrayPool<byte>.Shared.Rent(Math.Max(inputLength, 1));
        var output = ArrayPool<byte>.Shared.Rent(Math.Max(outputLength, 1));
        try
        {
            if (inputLength != 0 &&
                !ctx.Memory.TryRead(inputAddress, input.AsSpan(0, inputLength)))
            {
                return new Atrac9DecodeResult(
                    Atrac9DecodeState.ResultInvalidParameter,
                    0,
                    0,
                    0,
                    0);
            }

            var result = instance.Atrac9!.Decode(
                input.AsSpan(0, inputLength),
                output.AsSpan(0, outputLength),
                instance.Encoding,
                requestedChannels: 0,
                multipleFrames);

            if (result.OutputWritten != 0 &&
                !ctx.Memory.TryWrite(outputAddress, output.AsSpan(0, result.OutputWritten)))
            {
                return result with
                {
                    Status = result.Status | Atrac9DecodeState.ResultInvalidParameter,
                    OutputWritten = 0,
                };
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(output);
        }
    }

    private static Atrac9PcmEncoding GetPcmEncoding(ulong flags) =>
        ((flags >> 7) & 0x7) switch
        {
            1 => Atrac9PcmEncoding.Signed32,
            2 => Atrac9PcmEncoding.Float,
            _ => Atrac9PcmEncoding.Signed16,
        };

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
        Interlocked.Exchange(ref _nextBatchId, 0);
    }

    // AjmBatchInfo: buffer, offset, size, last_good_job, last_good_job_ra (5× u64).
    private const int AjmBatchInfoBytes = 40;
    private const ulong AjmBatchInfoOffsetField = 8;
    private const ulong AjmBatchInfoSizeField = 16;
    private const ulong AjmBatchInfoLastGoodJobField = 24;
    private const ulong AjmBatchInfoLastGoodJobRaField = 32;
    private const ulong AjmJobControlSize = 48;
    private const ulong AjmJobRunSize = 64;
    // SCE_AJM_JOB_RUN_SPLIT_SIZE(N) is 32 + 16 bytes per descriptor.
    private const ulong AjmJobRunSplitBaseSize = 32;
    private const ulong AjmJobGetStatisticsSize = 88;
    private const int AjmStatisticsResultBytes = 48;
    private const int AjmSidebandResultBytes = 8;
    private const int AjmSidebandStreamBytes = 16;
    private const int AjmSidebandFormatBytes = 24;
    private const int AjmSidebandGaplessDecodeBytes = 8;
    private const int AjmSidebandMFrameBytes = 8;
    private const int AjmSidebandCodecInfoBytes = 64;
    // AjmSidebandResult (8) + AjmSidebandStream (16) + AjmSidebandMFrame (8).
    private const int DecodeSidebandBytes =
        AjmSidebandResultBytes + AjmSidebandStreamBytes + AjmSidebandMFrameBytes;

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
               TryWriteUInt64(ctx, infoAddress + AjmBatchInfoLastGoodJobRaField, 0) &&
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
        Atrac9DecodeResult result,
        bool multipleFrames)
    {
        if (resultAddress == 0)
        {
            return;
        }

        Span<byte> sideband = stackalloc byte[DecodeSidebandBytes];
        sideband.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(sideband, result.Status);
        BinaryPrimitives.WriteInt32LittleEndian(sideband[8..], result.InputConsumed);
        BinaryPrimitives.WriteInt32LittleEndian(sideband[12..], result.OutputWritten);
        BinaryPrimitives.WriteUInt64LittleEndian(sideband[16..], result.TotalDecodedSamples);
        if (multipleFrames)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sideband[24..], result.Frames);
        }

        _ = ctx.Memory.TryWrite(
            resultAddress,
            multipleFrames ? sideband : sideband[..24]);
    }

    private static void WriteBasicResult(CpuContext ctx, ulong resultAddress, int status)
    {
        if (resultAddress == 0)
        {
            return;
        }

        Span<byte> result = stackalloc byte[8];
        result.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(result, status);
        _ = ctx.Memory.TryWrite(resultAddress, result);
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
