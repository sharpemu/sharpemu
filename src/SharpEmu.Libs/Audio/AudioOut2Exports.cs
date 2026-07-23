// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Audio;

public static class AudioOut2Exports
{
    // FMOD's PS5 backend allocates this ABI structure as four 16-byte lanes.
    // Clearing 0x80 bytes here overwrote the caller's stack canary immediately
    // following the 0x40-byte parameter block.
    private const int AudioOut2ContextParamSize = 0x40;
    // Keep these modest. GTA V Enhanced stack-allocates QueryMemory results next
    // to the frame canary: a 16-byte {size,align} write to [rbp-0x38] plants
    // align at [rbp-0x30] (observed canary=0x100). Size-only (8 bytes) on stack.
    private const int AudioOut2ContextMemorySize = 0x4000;
    private const int AudioOut2ContextMemoryAlignment = 0x100;
    // Exact object body size. Do not page-align to 64K — the RAGE Main Thread
    // stack-allocates this and a 64K VLA is what planted 0x10000 on the canary.
    private const int SpeakerArrayHeaderSize = 0x40;
    private const int SpeakerArrayEntrySize = 0x100;
    // Extra scratch the title writes after the per-channel entries (coefficients).
    private const int SpeakerArrayScratchBytes = 0x400;
    private const uint SpeakerArrayDefaultChannels = 8;
    private const uint SpeakerArrayMaxChannels = 32;
    // Field read by GTA at object+0x34 (see AV at eboot+0xB07D: mov eax,[rbx+0x34]).
    private const int SpeakerArrayDivisorFieldOffset = 0x34;
    private const int SpeakerArrayResultFieldOffset = 0x3C;
    private const uint SpeakerArrayDefaultDivisor = 1;
    private const int SpeakerArrayCoefficientBytes = 0x400;
    // OrbisAudioOutPortState is 0x20 bytes. Never grow this from r8/r9 — those
    // regs arrive polluted with GetSize leftovers (0x840/0x10C/0x180) and caused
    // PortGetState/GetSpeakerInfo to overwrite the speaker-array param block
    // (param+0x18 == first PortGetState out) and smash the Main Thread canary
    // with ContextMemoryAlignment (0x100).
    private const int PortStateSize = 0x20;
    private const int SpeakerInfoSize = 0x20;
    private const int PortParamSize = 0x40;
    private const int AttributeEntrySize = 0x18;
    private const uint PortAttributeIdPcm = 0;
    private const ushort PortStateOutputConnectedPrimary = 0x01;
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;
    private static long _pushTraceCount;
    private static long _submitTraceCount;
    private static long _attributePcmTraceCount;

    private static readonly ConcurrentDictionary<ulong, byte> SpeakerArrays = new();
    private static readonly ConcurrentDictionary<ulong, ContextState> Contexts = new();
    private static readonly ConcurrentDictionary<ulong, PortState> Ports = new();

    private sealed class ContextState
    {
        private readonly object _paceGate = new();
        private long _nextAdvanceTimestamp;

        public ContextState(ulong handle, uint frequency, uint grainSamples, uint queueDepth, IHostAudioStream? backend)
        {
            Handle = handle;
            Frequency = frequency == 0 ? 48000 : frequency;
            GrainSamples = grainSamples == 0 ? 256 : grainSamples;
            QueueDepth = queueDepth == 0 ? 4 : queueDepth;
            Backend = backend;
        }

        public ulong Handle { get; }
        public uint Frequency { get; }
        public uint GrainSamples { get; }
        public uint QueueDepth { get; }
        public IHostAudioStream? Backend { get; }

        public void PaceAdvance()
        {
            long delay;
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_nextAdvanceTimestamp < now)
                {
                    _nextAdvanceTimestamp = now;
                }

                delay = _nextAdvanceTimestamp - now;
                _nextAdvanceTimestamp += checked(
                    (long)Math.Ceiling(Stopwatch.Frequency * (double)GrainSamples / Frequency));
            }

            if (delay > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            }
        }
    }

    private sealed class PortState
    {
        public PortState(
            ulong handle,
            ulong contextHandle,
            ushort portType,
            uint dataFormat,
            uint samplingFrequency,
            uint grainSamples)
        {
            Handle = handle;
            ContextHandle = contextHandle;
            PortType = portType;
            DataFormat = dataFormat;
            SamplingFrequency = samplingFrequency == 0 ? 48000 : samplingFrequency;
            GrainSamples = grainSamples == 0 ? 256 : grainSamples;
        }

        public ulong Handle { get; }
        public ulong ContextHandle { get; }
        /// <summary>Full Prospero port type (low byte = MAIN/BGM/…, 0x0100 = object).</summary>
        public ushort PortType { get; }
        public uint DataFormat { get; }
        public uint SamplingFrequency { get; }
        public uint GrainSamples { get; }
        public ulong PcmAddress;
    }

    // Two host streams: primary FMOD context (menus) and everything else
    // (Bink/intro). Mixing those into one waveOut re-crunched audio; the OS
    // mixer keeps separate devices clean.
    private static readonly object HostBackendGate = new();
    private static IHostAudioStream? PrimaryBackend;
    private static IHostAudioStream? SecondaryBackend;
    private static string PrimaryBackendName = "none";
    private static string SecondaryBackendName = "none";
    private static ulong PrimaryContextHandle;
    private static readonly object HostSubmitGate = new();

    [SysAbiExport(
        Nid = "g2tViFIohHE",
        ExportName = "sceAudioOut2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Initialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "t5YrizufpQc",
        ExportName = "sceAudioOut2ContextResetParam",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextResetParam(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Layout matches libSceAudioOut2 SceAudioOut2ContextParam (no size prefix):
        // max_ports, max_object_ports, guarantee_object_ports, queue_depth,
        // num_grains, flags, reserved...
        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], 256);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 256);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x10..], 512);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x14..], 1);

        return ctx.Memory.TryWrite(paramAddress, param)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "pDmme7Bgm6E",
        ExportName = "sceAudioOut2ContextQueryMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextQueryMemory(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryInfoAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
        if (paramAddress == 0 || memoryInfoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Heap: {size, alignment} (16 bytes), matching sceAudioPropagationSystemQueryMemory.
        // Stack: SIZE ONLY as a full ulong (8 bytes). Writing alignment at +8 is how
        // [rbp-0x30] became 0x100 on GTA V Enhanced. Do NOT shrink this to uint32 —
        // Main reads the out as a 64-bit size; a 4-byte write leaves a garbage high
        // dword (observed 0x7<<32|0x4000) and the allocator aborts with int 0x41.
        if (IsGuestStackAddress(memoryInfoAddress))
        {
            Span<byte> sizeOnly = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(sizeOnly, AudioOut2ContextMemorySize);
            Console.Error.WriteLine(
                $"[LOADER][TRACE] audio_out2.context-query-memory stack-size-only " +
                $"out=0x{memoryInfoAddress:X} size=0x{AudioOut2ContextMemorySize:X}");
            return ctx.Memory.TryWrite(memoryInfoAddress, sizeOnly)
                ? SetReturn(ctx, 0)
                : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> memoryInfo = stackalloc byte[0x10];
        memoryInfo.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x00..], AudioOut2ContextMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x08..], AudioOut2ContextMemoryAlignment);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] audio_out2.context-query-memory out=0x{memoryInfoAddress:X} " +
            $"size=0x{AudioOut2ContextMemorySize:X} align=0x{AudioOut2ContextMemoryAlignment:X}");
        return ctx.Memory.TryWrite(memoryInfoAddress, memoryInfo)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "0x6o1VVAYSY",
        ExportName = "sceAudioOut2ContextCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextCreate(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = ctx[CpuRegister.Rdx];
        var outContextAddress = ctx[CpuRegister.Rcx];
        if (paramAddress == 0 || memoryAddress == 0 || memorySize == 0 || outContextAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Prospero AudioOut2 context params are port/queue config, not an AudioOut
        // open-style frequency/channel block. Sample rate is fixed at 48 kHz.
        uint frequency = 48000;
        uint grain = 256;
        uint queueDepth = 4;
        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        if (ctx.Memory.TryRead(paramAddress, param))
        {
            var qd = BinaryPrimitives.ReadUInt32LittleEndian(param[0x0C..]);
            var ng = BinaryPrimitives.ReadUInt32LittleEndian(param[0x10..]);
            if (qd is >= 1 and <= 32) queueDepth = qd;
            if (ng is >= 64 and <= 0x4000) grain = ng;
            TraceAudioOut2($"context-param address=0x{paramAddress:X} bytes={Convert.ToHexString(param)}");
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        // Backend is bound lazily on first real Push (primary vs secondary device).
        Contexts[handle] = new ContextState(handle, frequency, grain, queueDepth, backend: null);
        TraceAudioOut2(
            $"context-create handle=0x{handle:X} frequency={frequency} grain={grain} " +
            $"queue={queueDepth} memory=0x{memoryAddress:X} size=0x{memorySize:X} backend=pending");
        return TryWriteUInt64(ctx, outContextAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "on6ZH7Abo10",
        ExportName = "sceAudioOut2ContextDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextDestroy(CpuContext ctx)
    {
        // Shared backend lifetime is process-wide; just drop the context entry.
        Contexts.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "DxGyV8dtOR8",
        ExportName = "sceAudioOut2ContextBedWrite",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextBedWrite(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "aII9h5nli9U",
        ExportName = "sceAudioOut2ContextPush",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextPush(CpuContext ctx)
    {
        // ABI: sceAudioOut2ContextPush(ctx, blocking). RSI is a blocking flag
        // (observed 1), not a PCM pointer. PCM is attached earlier via
        // PortSetAttributes(attribute_id=PCM) and flushed here.
        var handle = ctx[CpuRegister.Rdi];
        var blocking = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (Interlocked.Increment(ref _pushTraceCount) <= 8)
        {
            TraceAudioOut2($"context-push handle=0x{handle:X} blocking={blocking}");
        }

        if (!Contexts.TryGetValue(handle, out var context))
        {
            return SetReturn(ctx, 0);
        }

        // Host Submit already blocks on the waveOut queue; only fall back to
        // software pacing when nothing was queued (silence / non-primary ctx).
        if (!TrySubmitContextAudio(ctx, context))
        {
            context.PaceAdvance();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "PE2zHMqLSHs",
        ExportName = "sceAudioOut2ContextAdvance",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextAdvance(CpuContext ctx)
    {
        if (Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var state))
        {
            state.PaceAdvance();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        // ABI out is a 32-bit queue depth (GTA compares dword [out] to 4). A
        // uint64 write into a stack slot at [rbp-0x14] next to the canary at
        // [rbp-0x10] zeroed the canary low half and killed Bink Snd @ eboot+0xAE36.
        var outLevelAddress = ctx[CpuRegister.Rsi];
        if (outLevelAddress == 0)
        {
            outLevelAddress = ctx[CpuRegister.Rdx];
        }

        if (outLevelAddress != 0)
        {
            Span<byte> level = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(level, 0);
            if (!ctx.Memory.TryWrite(outLevelAddress, level))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "Q8DZkKQ-SYc",
        ExportName = "sceAudioOut2LoContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2LoContextGetQueueLevel(CpuContext ctx) =>
        AudioOut2ContextGetQueueLevel(ctx);

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx)
    {
        // sceAudioOut2PortSetAttributes(port, attributes*, num).
        // Attribute id 0 = PCM; value points at { const void* data }.
        var portHandle = ctx[CpuRegister.Rdi];
        var attributesAddress = ctx[CpuRegister.Rsi];
        var attributeCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        if (!Ports.TryGetValue(portHandle, out var port))
        {
            return SetReturn(ctx, 0);
        }

        if (attributeCount == 0 || attributesAddress == 0)
        {
            return SetReturn(ctx, 0);
        }

        if (attributeCount > 32)
        {
            attributeCount = 32;
        }

        Span<byte> entry = stackalloc byte[AttributeEntrySize];
        Span<byte> pcm = stackalloc byte[8];
        for (uint i = 0; i < attributeCount; i++)
        {
            if (!ctx.Memory.TryRead(attributesAddress + (i * AttributeEntrySize), entry))
            {
                break;
            }

            var attributeId = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var valueAddress = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x08..]);
            var valueSize = BinaryPrimitives.ReadUInt64LittleEndian(entry[0x10..]);
            if (attributeId != PortAttributeIdPcm || valueAddress == 0 || valueSize < 8)
            {
                continue;
            }

            if (!ctx.Memory.TryRead(valueAddress, pcm))
            {
                continue;
            }

            port.PcmAddress = BinaryPrimitives.ReadUInt64LittleEndian(pcm);
            var n = Interlocked.Increment(ref _attributePcmTraceCount);
            if (n <= 8 || n % 500 == 0)
            {
                TraceAudioOut2(
                    $"port-set-pcm#{n} port=0x{portHandle:X} pcm=0x{port.PcmAddress:X} " +
                    $"format=0x{port.DataFormat:X} grains={port.GrainSamples}");
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        // sceAudioOut2PortCreate(ctx, PortParam*, outPort*).
        var contextHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx]);
        if (outPortAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ushort portType = 0;
        uint dataFormat = 0x0000_0200; // float stereo default
        uint samplingFrequency = 48000;
        uint grainSamples = 256;
        if (Contexts.TryGetValue(contextHandle, out var context))
        {
            grainSamples = context.GrainSamples;
            samplingFrequency = context.Frequency;
        }

        if (paramAddress != 0 && IsPlausibleGuestObjectPointer(paramAddress))
        {
            Span<byte> param = stackalloc byte[PortParamSize];
            if (ctx.Memory.TryRead(paramAddress, param))
            {
                portType = BinaryPrimitives.ReadUInt16LittleEndian(param);
                dataFormat = BinaryPrimitives.ReadUInt32LittleEndian(param[0x04..]);
                var freq = BinaryPrimitives.ReadUInt32LittleEndian(param[0x08..]);
                if (freq is >= 8000 and <= 192000)
                {
                    samplingFrequency = freq;
                }
            }
        }

        var portId = (uint)Interlocked.Increment(ref _nextPortId);
        // Handle encodes only the low type byte; PortState keeps the full type
        // so object ports (0x01xx) can still be filtered at submit time.
        var handle = 0x2000_0000UL | ((ulong)(portType & 0xFF) << 16) | portId;
        Ports[handle] = new PortState(
            handle,
            contextHandle,
            portType,
            dataFormat,
            samplingFrequency,
            grainSamples);
        if (!TryWriteUInt64(ctx, outPortAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2(
            $"port-create handle=0x{handle:X} ctx=0x{contextHandle:X} type=0x{portType:X} " +
            $"format=0x{dataFormat:X} freq={samplingFrequency} out=0x{outPortAddress:X}");
        return SetReturn(ctx, 0);
    }

    // Fixed-size connected stereo state. Do not trust r8/r9 for byte counts.
    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var portHandle = ctx[CpuRegister.Rdi];
        var stateAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);
        if (stateAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Stack out-buffers with garbage handles were writing 0x20 bytes over
        // caller frames / canaries (state=0x7FFFDE1FF688 right before fail).
        // Heap outs still get a real state blob even when the handle wasn't
        // minted by PortCreate — this title synthesizes port ids itself.
        if (IsGuestStackAddress(stateAddress))
        {
            TraceAudioOut2(
                $"port-get-state skip-stack handle=0x{portHandle:X} state=0x{stateAddress:X}");
            return SetReturn(ctx, 0);
        }

        Span<byte> state = stackalloc byte[PortStateSize];
        state.Clear();
        //   +0x00 u16 output   = CONNECTED_PRIMARY (1)
        //   +0x02 u8  channels = from port format when known, else 2
        //   +0x04 s16 volume   = -1 (N/A for main)
        byte channels = 2;
        if (Ports.TryGetValue(portHandle, out var port) &&
            TryDecodeDataFormat(port.DataFormat, out var decodedChannels, out _, out _))
        {
            channels = (byte)Math.Clamp(decodedChannels, 1, 16);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], PortStateOutputConnectedPrimary);
        state[0x02] = channels;
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], -1);

        if (!ctx.Memory.TryWrite(stateAddress, state))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2(
            $"port-get-state handle=0x{portHandle:X} state=0x{stateAddress:X} bytes=0x{PortStateSize:X}");
        return SetReturn(ctx, 0);
    }

    // rdi=out buffer, rsi=type/flag (not a pointer). Fixed-size write only.
    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rdi], ctx[CpuRegister.Rdx]);
        if (infoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Same rule as PortGetState — never bulk-write speaker info onto the stack.
        if (IsGuestStackAddress(infoAddress))
        {
            TraceAudioOut2($"get-speaker-info skip-stack out=0x{infoAddress:X}");
            return SetReturn(ctx, 0);
        }

        Span<byte> info = stackalloc byte[SpeakerInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 48000);
        BinaryPrimitives.WriteUInt16LittleEndian(info[0x08..], PortStateOutputConnectedPrimary);

        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2(
            $"get-speaker-info out=0x{infoAddress:X} type=0x{ctx[CpuRegister.Rsi]:X} bytes=0x{SpeakerInfoSize:X}");
        return SetReturn(ctx, 0);
    }

    // Matches sceAudio3dGetSpeakerArrayMemorySize(uiNumSpeakers, bIs3d): size is
    // returned directly in rax. Exact channel-scaled body — never a 64K slab.
    [SysAbiExport(
        Nid = "G1YOKDJYX2Y",
        ExportName = "sceAudioOut2GetSpeakerArrayMemorySize",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerArrayMemorySize(CpuContext ctx)
    {
        var numChannels = (uint)ctx[CpuRegister.Rdi];
        if (numChannels == 0 || numChannels > SpeakerArrayMaxChannels)
        {
            numChannels = SpeakerArrayDefaultChannels;
        }

        var size = ComputeSpeakerArrayBytes(numChannels);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] audio_out2.speaker-array-get-size rdi=0x{ctx[CpuRegister.Rdi]:X} " +
            $"rsi=0x{ctx[CpuRegister.Rsi]:X} rdx=0x{ctx[CpuRegister.Rdx]:X} -> 0x{size:X}");
        ctx[CpuRegister.Rax] = unchecked((ulong)size);
        return size;
    }

    [SysAbiExport(
        Nid = "4BlZurolOAo",
        ExportName = "sceAudioOut2GetSpeakerArrayCoefficients",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerArrayCoefficients(CpuContext ctx) =>
        WriteZeroSpeakerArrayCoefficients(ctx, "coefficients");

    [SysAbiExport(
        Nid = "28QqMnuuJ9Y",
        ExportName = "sceAudioOut2GetSpeakerArrayAmbisonicsCoefficients",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerArrayAmbisonicsCoefficients(CpuContext ctx) =>
        WriteZeroSpeakerArrayCoefficients(ctx, "ambisonics-coefficients");

    // rdi = param (may share a heap slab with PortGetState/GetSpeakerInfo outs —
    // do NOT read buffer*/size* from it). rsi = &outHandle, rdx = reserved/size
    // slot (leave alone), rcx = channels. Always heap-allocate a fresh object.
    [SysAbiExport(
        Nid = "+k91hoTuoA8",
        ExportName = "sceAudioOut2SpeakerArrayCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2SpeakerArrayCreate(CpuContext ctx)
    {
        var param = ctx[CpuRegister.Rdi];
        var outHandleAddress = ctx[CpuRegister.Rsi];
        var outReservedAddress = ctx[CpuRegister.Rdx];
        var channels = (uint)ctx[CpuRegister.Rcx];
        if (channels == 0 || channels > SpeakerArrayMaxChannels)
        {
            channels = SpeakerArrayDefaultChannels;
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var bytes = ComputeSpeakerArrayBytes(channels);
        if (!TryAllocateSpeakerArrayMemory(ctx, (ulong)bytes, out var memory) ||
            !InitializeSpeakerArrayObject(ctx, memory, channels))
        {
            Console.Error.WriteLine(
                $"[LOADER][ERROR] audio_out2.speaker-array-create alloc-failed bytes=0x{bytes:X} " +
                $"channels={channels}");
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        SpeakerArrays[memory] = 0;
        // Publish ONLY the out-handle slot. rdx is an adjacent size/reserved
        // local on GTA's stack — writing it previously fed canary corruption.
        if (!TryWriteUInt64(ctx, outHandleAddress, memory))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] audio_out2.speaker-array-create object=0x{memory:X} bytes=0x{bytes:X} " +
            $"channels={channels} param=0x{param:X} out=0x{outHandleAddress:X} " +
            $"reserved=0x{outReservedAddress:X} (untouched)");

        ctx[CpuRegister.Rax] = memory;
        return 0;
    }

    [SysAbiExport(
        Nid = "erCWQR5eKiQ",
        ExportName = "sceAudioOut2SpeakerArrayDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2SpeakerArrayDestroy(CpuContext ctx)
    {
        SpeakerArrays.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx)
    {
        Ports.TryRemove(ctx[CpuRegister.Rdi], out _);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "IaZXJ9M79uo",
        ExportName = "sceAudioOut2UserDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserDestroy(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "xywYcRB7nbQ",
        ExportName = "sceAudioOut2UserCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2UserCreate(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var outUserAddress = ctx[CpuRegister.Rsi];
        if ((userId != 0 && userId != 1 && userId != 1000 && userId != 0x10000000 && userId != 255) ||
            outUserAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = (ulong)Interlocked.Increment(ref _nextUserHandle);
        return TryWriteUInt64(ctx, outUserAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static IHostAudioStream? ResolveContextBackend(ContextState context, out string backendName)
    {
        lock (HostBackendGate)
        {
            if (PrimaryContextHandle == 0)
            {
                PrimaryContextHandle = context.Handle;
            }

            if (context.Handle == PrimaryContextHandle)
            {
                if (PrimaryBackend is null)
                {
                    try
                    {
                        var audio = HostPlatform.Current.Audio;
                        PrimaryBackend = audio.OpenStereoPcm16Stream(context.Frequency);
                        PrimaryBackendName = audio.BackendName + "-primary";
                    }
                    catch (Exception exception)
                    {
                        PrimaryBackendName = "silent";
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] AudioOut2 primary backend unavailable: {exception.Message}");
                    }
                }

                backendName = PrimaryBackendName;
                return PrimaryBackend;
            }

            if (SecondaryBackend is null)
            {
                try
                {
                    var audio = HostPlatform.Current.Audio;
                    SecondaryBackend = audio.OpenStereoPcm16Stream(context.Frequency);
                    SecondaryBackendName = audio.BackendName + "-secondary";
                }
                catch (Exception exception)
                {
                    SecondaryBackendName = "silent";
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] AudioOut2 secondary backend unavailable: {exception.Message}");
                }
            }

            backendName = SecondaryBackendName;
            return SecondaryBackend;
        }
    }

    private static bool TrySubmitContextAudio(CpuContext ctx, ContextState context)
    {
        var frames = checked((int)context.GrainSamples);
        if (frames <= 0)
        {
            return false;
        }

        // Serialize submits per process so ArrayPool buffers stay private; primary
        // and secondary devices still receive independent PCM (no digital sum).
        lock (HostSubmitGate)
        {
            if (!TryPickMainBed(context.Handle, out var mainPort))
            {
                mainPort = null;
            }

            if (!TryPickAuxBed(context.Handle, out var auxPort))
            {
                auxPort = null;
            }

            if (mainPort is null && auxPort is null)
            {
                return false;
            }

            var mix = ArrayPool<float>.Shared.Rent(frames * 2);
            var source = ArrayPool<byte>.Shared.Rent(frames * 16 * sizeof(float));
            var output = ArrayPool<byte>.Shared.Rent(frames * AudioPcmConversion.OutputFrameSize);
            try
            {
                mix.AsSpan(0, frames * 2).Clear();
                var mixedPorts = 0;
                ulong lastPort = 0;
                uint lastFormat = 0;
                var lastChannels = 0;

                // At most one MAIN/BGM + one AUX on this context. Menus stay on
                // MAIN; intro/Bink rides AUX without stacking every bed.
                for (var bed = 0; bed < 2; bed++)
                {
                    var port = bed == 0 ? mainPort : auxPort;
                    if (port is null ||
                        !TryDecodeDataFormat(port.DataFormat, out var ch, out var bps, out var isFloat))
                    {
                        continue;
                    }

                    var byteLength = checked(frames * ch * bps);
                    if (byteLength <= 0 || byteLength > source.Length)
                    {
                        continue;
                    }

                    var sourceSpan = source.AsSpan(0, byteLength);
                    if (!ctx.Memory.TryRead(port.PcmAddress, sourceSpan))
                    {
                        continue;
                    }

                    MixPortIntoStereo(
                        sourceSpan,
                        mix.AsSpan(0, frames * 2),
                        frames,
                        ch,
                        bps,
                        isFloat,
                        additive: mixedPorts > 0);
                    mixedPorts++;
                    lastPort = port.Handle;
                    lastFormat = port.DataFormat;
                    lastChannels = ch;
                }

                if (mixedPorts == 0)
                {
                    return false;
                }

                var outputSpan = output.AsSpan(0, frames * AudioPcmConversion.OutputFrameSize);
                var peak = 0f;
                var any = false;
                for (var frame = 0; frame < frames; frame++)
                {
                    var left = Math.Clamp(mix[frame * 2], -1f, 1f);
                    var right = Math.Clamp(mix[(frame * 2) + 1], -1f, 1f);
                    var framePeak = Math.Max(Math.Abs(left), Math.Abs(right));
                    if (framePeak > peak)
                    {
                        peak = framePeak;
                    }

                    if (framePeak > 1e-7f)
                    {
                        any = true;
                    }

                    BinaryPrimitives.WriteInt16LittleEndian(
                        outputSpan[(frame * AudioPcmConversion.OutputFrameSize)..],
                        FloatToPcm16(left));
                    BinaryPrimitives.WriteInt16LittleEndian(
                        outputSpan[((frame * AudioPcmConversion.OutputFrameSize) + 2)..],
                        FloatToPcm16(right));
                }

                if (!any)
                {
                    return false;
                }

                // Bind primary only after a real grain so silent early contexts
                // do not steal the menu device.
                var backend = ResolveContextBackend(context, out var backendName);
                if (backend is null)
                {
                    return false;
                }

                var n = Interlocked.Increment(ref _submitTraceCount);
                if (n <= 8 || n % 200 == 0)
                {
                    TraceAudioOut2(
                        $"context-submit#{n} handle=0x{context.Handle:X} frames={frames} " +
                        $"ports={mixedPorts} lastPort=0x{lastPort:X} format=0x{lastFormat:X} " +
                        $"ch={lastChannels} peak={peak:F4} backend={backendName}");
                }

                return backend.Submit(outputSpan);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(mix);
                ArrayPool<byte>.Shared.Return(source);
                ArrayPool<byte>.Shared.Return(output);
            }
        }
    }

    private static bool TryPickMainBed(ulong contextHandle, out PortState? chosen)
    {
        chosen = null;
        var chosenScore = int.MinValue;
        foreach (var port in Ports.Values)
        {
            if (port.ContextHandle != contextHandle ||
                port.PcmAddress == 0 ||
                IsObjectPort(port.PortType) ||
                !IsMainOrBgmPort(port.PortType) ||
                !TryDecodeDataFormat(port.DataFormat, out var channels, out _, out _))
            {
                continue;
            }

            var score = channels switch
            {
                2 => 300,
                1 => 200,
                8 => 100,
                _ => 50,
            };
            if ((port.PortType & 0xFF) == 0)
            {
                score += 20; // MAIN over BGM
            }

            if (score > chosenScore)
            {
                chosenScore = score;
                chosen = port;
            }
        }

        return chosen is not null;
    }

    private static bool TryPickAuxBed(ulong contextHandle, out PortState? chosen)
    {
        chosen = null;
        var chosenScore = int.MinValue;
        foreach (var port in Ports.Values)
        {
            if (port.ContextHandle != contextHandle ||
                port.PcmAddress == 0 ||
                IsObjectPort(port.PortType) ||
                (port.PortType & 0xFF) != 6 ||
                !TryDecodeDataFormat(port.DataFormat, out var channels, out _, out _))
            {
                continue;
            }

            var score = channels switch
            {
                2 => 300,
                1 => 200,
                8 => 100,
                _ => 50,
            };
            if (score > chosenScore)
            {
                chosenScore = score;
                chosen = port;
            }
        }

        return chosen is not null;
    }

    private static bool IsMainOrBgmPort(ushort portType)
    {
        var kind = portType & 0xFF;
        return kind is 0 or 1;
    }

    private static void MixPortIntoStereo(
        ReadOnlySpan<byte> source,
        Span<float> mix,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        bool additive)
    {
        var frameSize = channels * bytesPerSample;
        for (var frame = 0; frame < frames; frame++)
        {
            var frameBytes = source.Slice(frame * frameSize, frameSize);
            float left;
            float right;
            if (channels >= 8)
            {
                var fl = ReadNormalizedSample(frameBytes, 0, bytesPerSample, isFloat);
                var fr = ReadNormalizedSample(frameBytes, 1, bytesPerSample, isFloat);
                var c = ReadNormalizedSample(frameBytes, 2, bytesPerSample, isFloat);
                var bl = ReadNormalizedSample(frameBytes, 4, bytesPerSample, isFloat);
                var br = ReadNormalizedSample(frameBytes, 5, bytesPerSample, isFloat);
                var sl = ReadNormalizedSample(frameBytes, 6, bytesPerSample, isFloat);
                var sr = ReadNormalizedSample(frameBytes, 7, bytesPerSample, isFloat);
                const float side = 0.70710678f;
                left = fl + (c * side) + (bl * side) + (sl * side);
                right = fr + (c * side) + (br * side) + (sr * side);
            }
            else
            {
                left = ReadNormalizedSample(frameBytes, 0, bytesPerSample, isFloat);
                right = channels == 1
                    ? left
                    : ReadNormalizedSample(frameBytes, 1, bytesPerSample, isFloat);
            }

            if (additive)
            {
                mix[frame * 2] += left;
                mix[(frame * 2) + 1] += right;
            }
            else
            {
                mix[frame * 2] = left;
                mix[(frame * 2) + 1] = right;
            }
        }
    }

    private static float ReadNormalizedSample(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (isFloat)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(sample);
            var value = BitConverter.Int32BitsToSingle(bits);
            return float.IsFinite(value) ? value : 0f;
        }

        return BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768f;
    }

    private static short FloatToPcm16(float value)
    {
        var scale = value < 0f ? 32768f : short.MaxValue;
        return (short)Math.Clamp(MathF.Round(value * scale), short.MinValue, short.MaxValue);
    }

    private static bool IsObjectPort(ushort portType) => (portType & 0xFF00) == 0x0100;

    private static bool TryDecodeDataFormat(
        uint dataFormat,
        out int channels,
        out int bytesPerSample,
        out bool isFloat)
    {
        channels = (int)((dataFormat >> 8) & 0xFF);
        if (channels == 0)
        {
            channels = 2;
        }

        if (channels is < 1 or > 16)
        {
            bytesPerSample = 0;
            isFloat = false;
            return false;
        }

        var dataType = dataFormat & 0x7Fu;
        isFloat = dataType == 0;
        bytesPerSample = isFloat ? 4 : dataType == 1 ? 2 : 0;
        return bytesPerSample != 0;
    }

    private static int ComputeSpeakerArrayBytes(uint channels) =>
        SpeakerArrayHeaderSize + (int)(channels * SpeakerArrayEntrySize) + SpeakerArrayScratchBytes;

    private static bool InitializeSpeakerArrayObject(CpuContext ctx, ulong memory, uint channels)
    {
        // Header only — never wipe the full GetSize slab (and never touch stack).
        Span<byte> body = stackalloc byte[SpeakerArrayHeaderSize];
        body.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(body[0x00..], (uint)SpeakerArrayHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body[0x04..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(body[SpeakerArrayDivisorFieldOffset..], SpeakerArrayDefaultDivisor);
        BinaryPrimitives.WriteUInt32LittleEndian(body[SpeakerArrayResultFieldOffset..], 0);
        return ctx.Memory.TryWrite(memory, body);
    }

    // Prefer the high guest arena (0x6000_xxxx). TryAllocateHleData advances
    // _nextVirtualAddress into the title's direct-memory window (~0x1559_xxxx);
    // publishing an object there made sceKernelBatchMap(fixed, 0x1559C80000,
    // 0x20000) return NOT_FOUND and abort RenderThread with int 0x41.
    // Never mint the old 0x1559C0xxxx "cookie" pointers — they are unmapped and
    // collide with dmem VAs.
    private static bool TryAllocateSpeakerArrayMemory(CpuContext ctx, ulong bytes, out ulong memory)
    {
        memory = 0;
        var length = Math.Max(bytes, 0x1000UL);

        if (TryAllocateViaGuestAllocator(ctx, length, 0x1000, out memory) &&
            IsSafeSpeakerArrayAddress(memory))
        {
            return true;
        }

        if (Kernel.KernelMemoryCompatExports.TryAllocateHleData(ctx, length, 0x1000, out memory) &&
            IsSafeSpeakerArrayAddress(memory))
        {
            return true;
        }

        memory = 0;
        return false;
    }

    private static bool TryAllocateViaGuestAllocator(CpuContext ctx, ulong length, ulong alignment, out ulong memory)
    {
        memory = 0;
        var allocator = ctx.Memory as IGuestMemoryAllocator;
        if (allocator is null && ctx.Memory is ICpuMemoryWrapper { Inner: IGuestMemoryAllocator inner })
        {
            allocator = inner;
        }

        return allocator is not null && allocator.TryAllocateGuestMemory(length, alignment, out memory);
    }

    private static bool IsSafeSpeakerArrayAddress(ulong value) =>
        IsPlausibleGuestObjectPointer(value) &&
        !IsGuestStackAddress(value) &&
        !IsDirectMemoryWindowAddress(value);

    // GTA V Enhanced BatchMap fixed dmem VAs observed around 0x1559_xxxx_xxxx.
    // Keep HLE speaker-array objects out of that window.
    private static bool IsDirectMemoryWindowAddress(ulong value) =>
        value >= 0x0000_1400_0000_0000UL && value < 0x0000_1800_0000_0000UL;

    private static bool IsPlausibleGuestObjectPointer(ulong value) =>
        value >= 0x1000_0000UL &&
        value != 0x10000UL &&
        value < 0x0000_8000_0000_0000UL;

    // Windows user stacks sit in 0x00007FFFxxxxxxxx. Never treat those as
    // heap objects we can bulk-initialize.
    private static bool IsGuestStackAddress(ulong value) =>
        value >= 0x0000_7FF0_0000_0000UL && value <= 0x0000_7FFF_FFFF_FFFFUL;

    private static ulong ResolveGuestOutBuffer(ulong primary, ulong secondary)
    {
        // Accept heap or stack out-buffers (PortGetState legitimately uses both),
        // but never small integers / size constants.
        if (IsWritableOutBuffer(primary))
        {
            return primary;
        }

        if (IsWritableOutBuffer(secondary))
        {
            return secondary;
        }

        return 0;
    }

    private static bool IsWritableOutBuffer(ulong value) =>
        value != 0 &&
        value != 0x10000UL &&
        value >= 0x1000UL &&
        (IsPlausibleGuestObjectPointer(value) || IsGuestStackAddress(value));

    private static int WriteZeroSpeakerArrayCoefficients(CpuContext ctx, string label)
    {
        var destination = ctx[CpuRegister.Rsi];
        if (destination == 0)
        {
            destination = ctx[CpuRegister.Rdx];
        }

        // Coefficients are large — only wipe real heap objects, never stack.
        if (destination != 0 &&
            IsPlausibleGuestObjectPointer(destination) &&
            !IsGuestStackAddress(destination))
        {
            Span<byte> zeros = stackalloc byte[SpeakerArrayCoefficientBytes];
            zeros.Clear();
            if (!ctx.Memory.TryWrite(destination, zeros))
            {
                TraceAudioOut2($"{label} write-failed dest=0x{destination:X}");
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        TraceAudioOut2($"{label} ok dest=0x{destination:X}");
        return SetReturn(ctx, 0);
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceAudioOut2(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_OUT2"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] audio_out2.{message}");
        }
    }
}
