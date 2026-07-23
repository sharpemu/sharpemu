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
    private const int AudioOut2ContextMemorySize = 0x10000;
    private const int AudioOut2ContextMemoryAlignment = 0x10000;
    private static long _nextContextHandle = 1;
    private static long _nextUserHandle = 1;
    private static int _nextPortId;
    private static long _pushTraceCount;
    private static long _queueCallerTraceCount;

    // Ghost of Yōtei's NCA pump gates all audio production on the second
    // GetQueueLevel output ("free grain slots"): its submit routine clamps the
    // grain count to that value and never calls ContextPush when it is zero.
    // With the synchronous paced Push below, the host queue is drained by
    // construction, so level=0/available=depth is the true queue state rather
    // than a fabricated success value. Opt-in until validated on more titles.
    private static int _queueDepth = ParseQueueDepth(
        Environment.GetEnvironmentVariable("SHARPEMU_AUDIO_QUEUE_DEPTH"));

    private static int ParseQueueDepth(string? raw)
    {
        // Depth is grains of readahead the guest may queue; keep it small so a
        // misconfigured value cannot let the producer run far ahead of pacing.
        const int maxDepth = 64;
        return int.TryParse(raw, out var depth) && depth is > 0 and <= maxDepth
            ? depth
            : 0;
    }

    internal static void SetQueueDepthForTests(int depth) => _queueDepth = depth;

    // Per-context audio parameters captured at ContextCreate so ContextAdvance
    // can pace to the real playback cadence (grain samples at the sample rate).
    private static readonly ConcurrentDictionary<ulong, ContextState> Contexts = new();

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

        private int _queuedGrains;

        public int QueuedGrains => Volatile.Read(ref _queuedGrains);

        // The queued count is transient: a grain is "queued" only while its
        // paced Push is in flight, so pollers on other threads observe a
        // momentarily busy queue instead of one that can never fill or drain.
        public void BeginQueuedGrain(int depth)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _queuedGrains);
                if (observed >= depth)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _queuedGrains, observed + 1, observed) != observed);
        }

        public void EndQueuedGrain()
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _queuedGrains);
                if (observed <= 0)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _queuedGrains, observed - 1, observed) != observed);
        }

        // Blocks the advancing thread until one grain worth of wall-clock time
        // has elapsed since the previous advance, matching hardware timing so
        // audio-gated titles neither spin nor drift ahead.
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
        Nid = "XHl38ZNknbs",
        ExportName = "sceAudioOut2MasteringInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2MasteringInit(CpuContext ctx)
    {
        // Mastering is host-owned here. The guest only needs the service
        // initialization call to complete before constructing its audio graph.
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "TViD1EZXkNI",
        ExportName = "sceAudioOut2Set3DLatency",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2Set3DLatency(CpuContext ctx)
    {
        // The software path is paced by ContextPush/ContextAdvance, so no
        // separate host latency profile is needed.
        return SetReturn(ctx, 0);
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
        var memoryInfoAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0 || memoryInfoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> memoryInfo = stackalloc byte[0x20];
        memoryInfo.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x00..], AudioOut2ContextMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x08..], AudioOut2ContextMemoryAlignment);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x10..], AudioOut2ContextMemorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(memoryInfo[0x18..], AudioOut2ContextMemoryAlignment);

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

        // Read channels/frequency/grain from the reset-param blob so the
        // context can pace advances to the real audio cadence.
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
            // Values below one cache line are flags/counts in observed PS5
            // callers, not audio grains. Keep the hardware-sized default.
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
        var traceCount = Interlocked.Increment(ref _pushTraceCount);
        if (traceCount <= 16)
        {
            TraceAudioOut2($"context-push count={traceCount} rdi=0x{handle:X} rsi=0x{ctx[CpuRegister.Rsi]:X} rdx=0x{ctx[CpuRegister.Rdx]:X} rcx=0x{ctx[CpuRegister.Rcx]:X}");
        }

        if (Contexts.TryGetValue(handle, out var context))
        {
            // FMOD's PS5 output path uses ContextPush as the submission clock
            // and does not call ContextAdvance. Pace pushes to one hardware
            // grain so the feeder cannot outrun playback and starve the game.
            var depth = _queueDepth;
            if (depth > 0)
            {
                context.BeginQueuedGrain(depth);
            }

            context.PaceAdvance();
            if (depth > 0)
            {
                context.EndQueuedGrain();
            }
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
        // Advancing renders one grain of audio on hardware; pace it to the same
        // wall-clock cadence so the guest audio thread runs at the right speed.
        if (Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var context))
        {
            context.PaceAdvance();
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "4dq2rblWlg0",
        ExportName = "sceAudioOut2ContextSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextSetAttributes(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "R7d0F1g2qsU",
        ExportName = "sceAudioOut2ContextGetQueueLevel",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2ContextGetQueueLevel(CpuContext ctx)
    {
        // The advance path paces synchronously, so the queue is always drained.
        // Both ABI outputs are 32-bit values. A previous 64-bit write at the
        // first pointer overwrote the adjacent guest stack field/canary.
        var levelAddress = ctx[CpuRegister.Rsi];
        var availableAddress = ctx[CpuRegister.Rdx];
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_AUDIO_QUEUE_CALLER"), "1", StringComparison.Ordinal) &&
            Interlocked.Increment(ref _queueCallerTraceCount) <= 128)
        {
            var rbp = ctx[CpuRegister.Rbp];
            ulong callerRip = 0;
            var callerKnown = rbp <= ulong.MaxValue - sizeof(ulong) &&
                ctx.TryReadUInt64(rbp + sizeof(ulong), out callerRip);
            Console.Error.WriteLine(
                $"[LOADER][TRACE] audio_out2.queue-caller#{_queueCallerTraceCount} " +
                $"rbp=0x{rbp:X16} caller={(callerKnown ? $"0x{callerRip:X16}" : "?")} " +
                $"rsp=0x{ctx[CpuRegister.Rsp]:X16} rdi=0x{ctx[CpuRegister.Rdi]:X16} " +
                $"rsi=0x{levelAddress:X16} rdx=0x{availableAddress:X16}");
        }
        if (levelAddress == 0 || availableAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint level = 0;
        uint available = 0;
        var depth = _queueDepth;
        if (depth > 0 && Contexts.TryGetValue(ctx[CpuRegister.Rdi], out var context))
        {
            var queued = Math.Clamp(context.QueuedGrains, 0, depth);
            level = (uint)queued;
            available = (uint)(depth - queued);
        }

        return ctx.TryWriteUInt32(levelAddress, level) &&
               ctx.TryWriteUInt32(availableAddress, available)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "JK2wamZPzwM",
        ExportName = "sceAudioOut2PortCreate",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortCreate(CpuContext ctx)
    {
        var contextHandle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outPortAddress = ctx[CpuRegister.Rdx];
        if (!Contexts.ContainsKey(contextHandle) || paramAddress == 0 || outPortAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> param = stackalloc byte[0x20];
        if (!ctx.Memory.TryRead(paramAddress, param))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var type = BinaryPrimitives.ReadUInt32LittleEndian(param);
        if (type > byte.MaxValue)
        {
            type = 0;
        }

        var portId = unchecked((uint)Interlocked.Increment(ref _nextPortId)) & 0xFF;
        var handle = 0x2000_0000UL | ((ulong)type << 16) | portId;
        return TryWriteUInt64(ctx, outPortAddress, handle)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8XTArSPyWHk",
        ExportName = "sceAudioOut2PortSetAttributes",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortSetAttributes(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "gatEUKG+Ea4",
        ExportName = "sceAudioOut2PortGetState",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortGetState(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        if (handle == 0 || stateAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var type = (int)((handle >> 16) & 0xFF);
        Span<byte> state = stackalloc byte[0x20];
        state.Clear();
        var output = type == 2 ? 0x40 : 0x01;
        var channels = type == 2 ? 1 : 2;
        BinaryPrimitives.WriteUInt16LittleEndian(state[0x00..], unchecked((ushort)output));
        state[0x02] = unchecked((byte)channels);
        BinaryPrimitives.WriteInt16LittleEndian(state[0x04..], -1);

        return ctx.Memory.TryWrite(stateAddress, state)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "DImz2Ft9E2g",
        ExportName = "sceAudioOut2GetSpeakerInfo",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2GetSpeakerInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> info = stackalloc byte[0x40];
        info.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x00..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x08..], 48000);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cd+Rtw+D1x8",
        ExportName = "sceAudioOut2PortDestroy",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut2")]
    public static int AudioOut2PortDestroy(CpuContext ctx) => SetReturn(ctx, 0);

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
