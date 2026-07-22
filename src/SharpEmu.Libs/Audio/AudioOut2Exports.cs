// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
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
    // Keep these modest. Some Prospero titles stack-allocate QueryMemory results
    // next to the frame canary: a 16-byte {size,align} write to [rbp-0x38] plants
    // align at [rbp-0x30] (observed canary=0x100). Size-only (8 bytes) on stack.
    private const int AudioOut2ContextMemorySize = 0x4000;
    private const int AudioOut2ContextMemoryAlignment = 0x100;
    // Exact object body size. Do not page-align to 64K — callers that
    // stack-allocate from this size planted 0x10000 on the canary with a 64K VLA.
    private const int SpeakerArrayHeaderSize = 0x40;
    private const int SpeakerArrayEntrySize = 0x100;
    // Extra scratch the title writes after the per-channel entries (coefficients).
    private const int SpeakerArrayScratchBytes = 0x400;
    private const uint SpeakerArrayDefaultChannels = 8;
    private const uint SpeakerArrayMaxChannels = 32;
    // Field read by titles at object+0x34 (mov eax,[rbx+0x34]).
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
    private const ushort PortStateOutputConnectedPrimary = 0x01;
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;
    private static long _pushTraceCount;

    private static readonly ConcurrentDictionary<ulong, byte> SpeakerArrays = new();
    private static readonly ConcurrentDictionary<ulong, ContextState> Contexts = new();
    private static readonly ConcurrentDictionary<ulong, int> Ports = new();

    private sealed class ContextState
    {
        private readonly object _paceGate = new();
        private long _nextAdvanceTimestamp;

        public ContextState(uint frequency, uint channels, uint grainSamples)
        {
            Frequency = frequency == 0 ? 48000 : frequency;
            Channels = channels == 0 ? 2 : channels;
            GrainSamples = grainSamples == 0 ? 256 : grainSamples;
        }

        public uint Frequency { get; }
        public uint Channels { get; }
        public uint GrainSamples { get; }

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

        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        param.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x00..], AudioOut2ContextParamSize);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x08..], 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(param[0x0C..], 0x400);

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
            TraceAudioOut2(
                $"context-query-memory stack-size-only out=0x{memoryInfoAddress:X} " +
                $"size=0x{AudioOut2ContextMemorySize:X}");
            return ctx.Memory.TryWrite(memoryInfoAddress, sizeOnly)
                ? SetReturn(ctx, 0)
                : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Span<byte> memoryInfo = stackalloc byte[0x10];
        memoryInfo.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x00..], AudioOut2ContextMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x08..], AudioOut2ContextMemoryAlignment);
        TraceAudioOut2(
            $"context-query-memory out=0x{memoryInfoAddress:X} " +
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

        uint channels = 2;
        uint frequency = 48000;
        uint grain = 256;
        Span<byte> param = stackalloc byte[AudioOut2ContextParamSize];
        if (ctx.Memory.TryRead(paramAddress, param))
        {
            var pc = BinaryPrimitives.ReadUInt32LittleEndian(param[0x04..]);
            var pf = BinaryPrimitives.ReadUInt32LittleEndian(param[0x08..]);
            var pg = BinaryPrimitives.ReadUInt32LittleEndian(param[0x0C..]);
            if (pc is > 0 and <= 8) channels = pc;
            if (pf is >= 8000 and <= 192000) frequency = pf;
            if (pg is >= 64 and <= 0x4000) grain = pg;
            TraceAudioOut2($"context-param address=0x{paramAddress:X} bytes={Convert.ToHexString(param)}");
        }

        var handle = (ulong)Interlocked.Increment(ref _nextContextHandle);
        Contexts[handle] = new ContextState(frequency, channels, grain);
        TraceAudioOut2($"context-create handle=0x{handle:X} frequency={frequency} channels={channels} grain={grain} memory=0x{memoryAddress:X} size=0x{memorySize:X}");
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
        var handle = ctx[CpuRegister.Rdi];
        if (Interlocked.Increment(ref _pushTraceCount) <= 8)
        {
            TraceAudioOut2($"context-push handle=0x{handle:X} data=0x{ctx[CpuRegister.Rsi]:X}");
        }

        // FMOD's PS5 output path uses ContextPush as the submission clock and
        // does not call ContextAdvance. Pace pushes to one hardware grain so
        // the feeder cannot outrun playback and starve the title.
        if (Contexts.TryGetValue(handle, out var context))
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
        // ABI out is a 32-bit queue depth (callers compare dword [out]). A
        // uint64 write into a stack slot at [rbp-0x14] next to the canary at
        // [rbp-0x10] zeroed the canary low half and aborted the audio thread.
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
    public static int AudioOut2PortSetAttributes(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        // rdi=user/context, rsi=type, rdx=outPort* (fallback rcx if rdx unusable).
        var outPortAddress = ResolveGuestOutBuffer(ctx[CpuRegister.Rdx], ctx[CpuRegister.Rcx]);
        if (outPortAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        if (type is < 0 or > 0x100)
        {
            type = 0;
        }

        var portId = (uint)Interlocked.Increment(ref _nextPortId);
        var handle = 0x2000_0000UL | ((ulong)(uint)type << 16) | portId;
        Ports[handle] = type;
        if (!TryWriteUInt64(ctx, outPortAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2($"port-create handle=0x{handle:X} type={type} out=0x{outPortAddress:X}");
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
        // minted by PortCreate — some titles synthesize port ids themselves.
        if (IsGuestStackAddress(stateAddress))
        {
            TraceAudioOut2(
                $"port-get-state skip-stack handle=0x{portHandle:X} state=0x{stateAddress:X}");
            return SetReturn(ctx, 0);
        }

        Span<byte> state = stackalloc byte[PortStateSize];
        state.Clear();
        //   +0x00 u16 output   = CONNECTED_PRIMARY (1)
        //   +0x02 u8  channels = 2
        //   +0x04 s16 volume   = -1 (N/A for main)
        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], PortStateOutputConnectedPrimary);
        state[0x02] = 2;
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
        TraceAudioOut2(
            $"speaker-array-get-size rdi=0x{ctx[CpuRegister.Rdi]:X} " +
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
            TraceAudioOut2(
                $"speaker-array-create alloc-failed bytes=0x{bytes:X} channels={channels}");
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        SpeakerArrays[memory] = 0;
        // Publish ONLY the out-handle slot. rdx is often an adjacent size /
        // reserved local on the caller stack — writing it previously fed canary
        // corruption.
        if (!TryWriteUInt64(ctx, outHandleAddress, memory))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAudioOut2(
            $"speaker-array-create object=0x{memory:X} bytes=0x{bytes:X} " +
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

    // BatchMap fixed dmem VAs have been observed around 0x1559_xxxx_xxxx.
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
