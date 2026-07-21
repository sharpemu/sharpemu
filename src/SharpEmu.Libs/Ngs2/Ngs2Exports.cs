// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.Ngs2;

public static class Ngs2Exports
{
    private const int OrbisNgs2ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int OrbisNgs2ErrorInvalidSystemHandle = unchecked((int)0x804A0230);
    private const int OrbisNgs2ErrorInvalidRackHandle = unchecked((int)0x804A0261);
    private const int OrbisNgs2ErrorInvalidVoiceHandle = unchecked((int)0x804A0300);
    private const ulong HandleStorageSize = 0x20;
    private const int RenderBufferInfoSize = 0x18;
    private const ulong MaximumRenderBufferSize = 16 * 1024 * 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, SystemState> Systems = new();
    private static readonly Dictionary<ulong, RackState> Racks = new();
    private static readonly Dictionary<ulong, VoiceState> Voices = new();
    private static long _nextUid;
    private static long _renderCount;

    // NGS2 renders one grain of interleaved float32 per sceNgs2SystemRender.
    // The grain length defaults to 256 frames (matching the 8192-byte AudioOut
    // buffers games copy it into) until the title overrides it.
    private const int DefaultGrainSamples = 256;
    private const double OutputSampleRate = 48000.0;

    private sealed class SystemState
    {
        public SystemState(uint uid) => Uid = uid;

        public uint Uid { get; }
        public int GrainSamples { get; set; } = DefaultGrainSamples;
    }

    private sealed record RackState(ulong SystemHandle, uint RackId);

    private sealed class VoiceState
    {
        public VoiceState(ulong rackHandle, uint voiceIndex)
        {
            RackHandle = rackHandle;
            VoiceIndex = voiceIndex;
        }

        public ulong RackHandle { get; }
        public uint VoiceIndex { get; }

        // Software-mixer playback state. Pcm is the fully decoded mono waveform;
        // Position is a fractional read cursor advanced at the source/output rate
        // ratio each output frame.
        public short[]? Pcm { get; set; }
        public ulong SourceAddr { get; set; }
        public int SourceRate { get; set; }
        public double Position { get; set; }
        public bool Playing { get; set; }
        public int LoopStart { get; set; } = -1;
        public int LoopEnd { get; set; }
        public float Gain { get; set; } = 1f;
    }

    [SysAbiExport(
        Nid = "mPYgU4oYpuY",
        ExportName = "sceNgs2SystemCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreateWithAllocator(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 1, ownerHandle: 0, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Systems[handle] = new SystemState(unchecked((uint)Interlocked.Increment(ref _nextUid)));
        }

        return SetReturn(ctx, 0);
    }

    // Non-allocator create: identical to the WithAllocator form for our purposes.
    // The only signature difference is the caller-supplied buffer info in rsi
    // (vs an allocator callback); the system option (rdi) and out-handle (rdx)
    // sit at the same argument positions, so we reuse the same implementation.
    // Dead Cells uses these variants — leaving sceNgs2SystemCreate unresolved
    // gave the game a garbage system handle, so every later rack/voice call
    // failed and it polled sceNgs2VoiceGetState forever, freezing at FLIP 0.
    [SysAbiExport(
        Nid = "koBbCMvOKWw",
        ExportName = "sceNgs2SystemCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreate(CpuContext ctx) => Ngs2SystemCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "u-WrYDaJA3k",
        ExportName = "sceNgs2SystemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Systems.Remove(handle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            var rackHandles = Racks
                .Where(pair => pair.Value.SystemHandle == handle)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var rackHandle in rackHandles)
            {
                RemoveRackLocked(rackHandle);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "U546k6orxQo",
        ExportName = "sceNgs2RackCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreateWithAllocator(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rackId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.R8];
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 2, systemHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Racks[handle] = new RackState(systemHandle, rackId);
        }

        return SetReturn(ctx, 0);
    }

    // Non-allocator rack create: system handle (rdi), rack id (rsi) and the
    // out-handle (r8) share the WithAllocator argument layout, so reuse it.
    [SysAbiExport(
        Nid = "cLV4aiT9JpA",
        ExportName = "sceNgs2RackCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreate(CpuContext ctx) => Ngs2RackCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "lCqD7oycmIM",
        ExportName = "sceNgs2RackDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(handle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            RemoveRackLocked(handle);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "MwmHz8pAdAo",
        ExportName = "sceNgs2RackGetVoiceHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetVoiceHandle(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var voiceIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(rackHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            var existing = Voices.FirstOrDefault(
                pair => pair.Value.RackHandle == rackHandle && pair.Value.VoiceIndex == voiceIndex);
            if (existing.Key != 0)
            {
                return ctx.TryWriteUInt64(outHandleAddress, existing.Key)
                    ? SetReturn(ctx, 0)
                    : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 4, rackHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Voices[handle] = new VoiceState(rackHandle, voiceIndex);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "uu94irFOGpA",
        ExportName = "sceNgs2VoiceControl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceControl(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var paramList = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Voices.ContainsKey(voiceHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
        }

        if (ShouldTrace())
        {
            TraceVoiceParamList(ctx, voiceHandle, paramList);
        }

        HandleVoiceParams(ctx, voiceHandle, paramList);
        return SetReturn(ctx, 0);
    }

    // Parse the SceNgs2VoiceParamHead command list (header = u32 size, u32 id;
    // params are laid out contiguously) and apply the ones the mixer needs:
    // the waveform-blocks param arms a voice with decoded PCM, and the port
    // matrix param carries its output gain.
    private static void HandleVoiceParams(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        if (paramList == 0)
        {
            return;
        }

        var offset = paramList;
        for (var guard = 0; guard < 32; guard++)
        {
            if (!ctx.TryReadUInt32(offset, out var size) ||
                !ctx.TryReadUInt32(offset + 4, out var id))
            {
                return;
            }

            switch (id)
            {
                case 0x10000001:
                    ApplyWaveformParam(ctx, voiceHandle, offset);
                    break;
                case 0x20010001:
                    ApplyPortMatrixParam(ctx, voiceHandle, offset);
                    break;
            }

            // Advance to the next contiguous block; the game normally sends one
            // param per call (size==whole block), so stop when size is degenerate.
            if (size < 8 || size > 0x1000)
            {
                return;
            }

            offset += (size + 7) & ~7u;
        }
    }

    // Waveform-blocks param: the guest pointer at +8 references a "VAGp"
    // (PS-ADPCM) container. Decode it once and arm the voice for playback.
    private static void ApplyWaveformParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt64(paramOffset + 8, out var dataAddr) || dataAddr <= 0x10000)
        {
            return;
        }

        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var existing) &&
                existing.SourceAddr == dataAddr && existing.Pcm is not null)
            {
                // Same waveform already armed — don't restart it every frame.
                return;
            }
        }

        Span<byte> header = stackalloc byte[Ngs2VagDecoder.VagHeaderSize];
        if (!ctx.Memory.TryRead(dataAddr, header) || !Ngs2VagDecoder.IsVag(header))
        {
            return;
        }

        var declaredSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header[0x0C..]);
        var totalBytes = Ngs2VagDecoder.VagHeaderSize + Math.Clamp(declaredSize, 0, 8 * 1024 * 1024);
        var raw = System.Buffers.ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            if (!ctx.Memory.TryRead(dataAddr, raw.AsSpan(0, totalBytes)) ||
                !Ngs2VagDecoder.TryDecode(raw.AsSpan(0, totalBytes), out var waveform))
            {
                return;
            }

            lock (StateGate)
            {
                if (!Voices.TryGetValue(voiceHandle, out var voice))
                {
                    return;
                }

                voice.Pcm = waveform.Samples;
                voice.SourceAddr = dataAddr;
                voice.SourceRate = waveform.SampleRate;
                voice.LoopStart = waveform.LoopStart;
                voice.LoopEnd = waveform.LoopEnd > 0 ? waveform.LoopEnd : waveform.Samples.Length;
                voice.Position = 0;
                voice.Playing = true;
            }

            if (ShouldTrace())
            {
                var peak = 0;
                for (var i = 0; i < waveform.Samples.Length; i++)
                {
                    peak = Math.Max(peak, Math.Abs((int)waveform.Samples[i]));
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] ngs2.arm voice=0x{voiceHandle:X16} addr=0x{dataAddr:X} rate={waveform.SampleRate} samples={waveform.Samples.Length} loop={waveform.LoopStart} peak={peak}");
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
        }
    }

    // Port matrix param: the first float level is a reasonable proxy for the
    // voice's output gain until per-channel panning is implemented.
    private static void ApplyPortMatrixParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt32(paramOffset + 12, out var levelBits))
        {
            return;
        }

        var level = BitConverter.UInt32BitsToSingle(levelBits);
        if (!float.IsFinite(level) || level < 0f || level > 8f)
        {
            return;
        }

        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var voice))
            {
                voice.Gain = level;
            }
        }
    }

    // Empirically dump the SceNgs2VoiceParamHead-chained command list so we can
    // confirm the real struct layout (size/next/id) against public NGS2 sources
    // before building the software mixer. Assumed header: u16 size, s16 next
    // (byte offset to the next block, 0 = end), u32 id.
    private static void TraceVoiceParamList(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        if (paramList == 0)
        {
            return;
        }

        Span<byte> peek = stackalloc byte[32];
        var offset = paramList;
        for (int guard = 0; guard < 32; guard++)
        {
            if (!ctx.TryReadUInt16(offset, out var size) ||
                !ctx.TryReadUInt16(offset + 2, out var next) ||
                !ctx.TryReadUInt32(offset + 4, out var id))
            {
                Console.Error.WriteLine($"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} @0x{offset:X}: unreadable header");
                return;
            }

            peek.Clear();
            var readable = Math.Min((int)Math.Max((ushort)8, size), peek.Length);
            ctx.Memory.TryRead(offset, peek[..readable]);
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} id=0x{id:X} size={size} next={unchecked((short)next)} bytes={Convert.ToHexString(peek[..readable])}");

            // For the waveform-blocks param, follow the embedded pointers and
            // dump the pointed-to bytes so we can tell PCM16 from ATRAC9.
            if (id == 0x10000001 && Interlocked.Increment(ref _waveformDumps) <= 8)
            {
                for (int po = 8; po + 8 <= readable; po += 8)
                {
                    if (ctx.TryReadUInt64(offset + (ulong)po, out var ptr) && ptr > 0x10000 &&
                        ctx.Memory.TryRead(ptr, peek))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] ngs2.waveform @+{po} ptr=0x{ptr:X} head={Convert.ToHexString(peek)}");
                    }
                }
            }

            var advance = unchecked((short)next);
            if (advance <= 0)
            {
                return;
            }

            offset += (ulong)advance;
        }
    }

    private static long _waveformDumps;
    private static long _renderInfoDumps;

    [SysAbiExport(
        Nid = "AbYvTOZ8Pts",
        ExportName = "sceNgs2VoiceRunCommands",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceRunCommands(CpuContext ctx) => Ngs2VoiceControl(ctx);

    [SysAbiExport(
        Nid = "i0VnXM-C9fc",
        ExportName = "sceNgs2SystemRender",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemRender(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var bufferInfoAddress = ctx[CpuRegister.Rsi];
        var bufferInfoCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (bufferInfoCount != 0 && bufferInfoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> renderBufferInfo = stackalloc byte[RenderBufferInfoSize];
        for (uint i = 0; i < bufferInfoCount; i++)
        {
            var entryAddress = bufferInfoAddress + (i * RenderBufferInfoSize);
            if (!ctx.TryReadUInt64(entryAddress, out var bufferAddress) ||
                !ctx.TryReadUInt64(entryAddress + 8, out var bufferSize))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (bufferAddress != 0 && bufferSize != 0)
            {
                if (bufferSize > MaximumRenderBufferSize || !TryClearGuestBuffer(ctx, bufferAddress, bufferSize))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                // SceNgs2RenderBufferInfo: {ptr@0, size@8, waveformType@16,
                // channelsCount@20}. Mix the armed voices into the leading grain
                // as interleaved float32 — this is what the game copies to
                // sceAudioOutOutput, so it is where NGS2 audio must appear.
                var channels = 2;
                if (ctx.TryReadUInt32(entryAddress + 20, out var declaredChannels) &&
                    declaredChannels is > 0 and <= 8)
                {
                    channels = (int)declaredChannels;
                }

                MixVoicesIntoGrain(ctx, systemHandle, bufferAddress, bufferSize, channels);

                if (ShouldTrace() && Interlocked.Increment(ref _renderInfoDumps) <= 4)
                {
                    ctx.Memory.TryRead(entryAddress, renderBufferInfo);
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] ngs2.renderbufinfo addr=0x{bufferAddress:X} size={bufferSize} ch={channels} raw={Convert.ToHexString(renderBufferInfo)}");
                }
            }
        }

        var count = Interlocked.Increment(ref _renderCount);
        if (ShouldTrace() && (count <= 4 || count % 200 == 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.render#{count} system=0x{systemHandle:X16} buffers={bufferInfoCount}");
        }

        return SetReturn(ctx, 0);
    }

    // Sum every armed voice belonging to this system into the leading grain of
    // the render buffer as interleaved float32. The buffer was just zeroed, so
    // this is a plain additive mix; silence stays silence when nothing plays.
    private static void MixVoicesIntoGrain(
        CpuContext ctx, ulong systemHandle, ulong bufferAddress, ulong bufferSize, int channels)
    {
        int grain;
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return;
            }

            grain = system.GrainSamples;
        }

        var capacityFrames = (int)Math.Min((ulong)grain, bufferSize / (ulong)(channels * sizeof(float)));
        if (capacityFrames <= 0)
        {
            return;
        }

        var floatCount = capacityFrames * channels;
        var accum = ArrayPool<float>.Shared.Rent(floatCount);
        var mixedAnything = false;
        try
        {
            Array.Clear(accum, 0, floatCount);
            lock (StateGate)
            {
                foreach (var pair in Voices)
                {
                    var voice = pair.Value;
                    if (!voice.Playing || voice.Pcm is null || voice.Pcm.Length == 0)
                    {
                        continue;
                    }

                    if (!Racks.TryGetValue(voice.RackHandle, out var rack) ||
                        rack.SystemHandle != systemHandle)
                    {
                        continue;
                    }

                    MixOneVoice(accum, capacityFrames, channels, voice);
                    mixedAnything = true;
                }
            }

            if (mixedAnything)
            {
                WriteGrain(ctx, bufferAddress, accum, floatCount);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(accum);
        }
    }

    // Resample one voice from its source rate to 48 kHz (nearest-sample) and add
    // it to the front stereo pair. Advances the voice cursor and handles loop /
    // one-shot end. Must be called under StateGate.
    private static void MixOneVoice(float[] accum, int frames, int channels, VoiceState voice)
    {
        var pcm = voice.Pcm!;
        var loopEnd = voice.LoopEnd > 0 && voice.LoopEnd <= pcm.Length ? voice.LoopEnd : pcm.Length;
        var loopStart = voice.LoopStart;
        var step = voice.SourceRate / OutputSampleRate;
        var gain = voice.Gain / 32768f;
        var pos = voice.Position;
        for (var f = 0; f < frames; f++)
        {
            var idx = (int)pos;
            if (idx >= loopEnd)
            {
                if (loopStart >= 0 && loopStart < loopEnd)
                {
                    pos = loopStart;
                    idx = loopStart;
                }
                else
                {
                    voice.Playing = false;
                    break;
                }
            }

            if (idx < 0 || idx >= pcm.Length)
            {
                voice.Playing = false;
                break;
            }

            var sample = pcm[idx] * gain;
            var baseIndex = f * channels;
            accum[baseIndex] += sample;
            if (channels > 1)
            {
                accum[baseIndex + 1] += sample;
            }

            pos += step;
        }

        voice.Position = pos;
    }

    private static void WriteGrain(CpuContext ctx, ulong address, float[] accum, int count)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(count * sizeof(float));
        try
        {
            var span = bytes.AsSpan(0, count * sizeof(float));
            for (var i = 0; i < count; i++)
            {
                var value = Math.Clamp(accum[i], -1f, 1f);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)), value);
            }

            ctx.Memory.TryWrite(address, span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    [SysAbiExport(
        Nid = "pgFAiLR5qT4",
        ExportName = "sceNgs2SystemQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "0eFLVCfWVds",
        ExportName = "sceNgs2RackQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rdx]);

    // Report a fixed working-memory footprint for the requested object. The
    // out struct (SceNgs2BufferAllocator-style) begins with the size field.
    private static int WriteBufferSize(CpuContext ctx, ulong outAddress)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        Span<byte> info = stackalloc byte[RenderBufferInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0..8], 0x10000);
        BinaryPrimitives.WriteUInt64LittleEndian(info[8..16], 0x100);
        return ctx.Memory.TryWrite(outAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "l4Q2dWEH6UM",
        ExportName = "sceNgs2SystemSetGrainSamples",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetGrainSamples(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var grain = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            if (grain > 0 && grain <= 8192)
            {
                system.GrainSamples = grain;
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "-tbc2SxQD60",
        ExportName = "sceNgs2SystemSetSampleRate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetSampleRate(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "gThZqM5PYlQ",
        ExportName = "sceNgs2SystemLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemLock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "JXRC5n0RQls",
        ExportName = "sceNgs2SystemUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemUnlock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "-TOuuAQ-buE",
        ExportName = "sceNgs2VoiceGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetState(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        var stateSize = (int)Math.Min(ctx[CpuRegister.Rdx], 0x400);
        lock (StateGate)
        {
            if (!Voices.ContainsKey(voiceHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
        }

        // Report an idle (not-in-use) voice: all-zero state block.
        if (stateAddress != 0 && stateSize > 0)
        {
            if (!TryClearGuestBuffer(ctx, stateAddress, (ulong)stateSize))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "rEh728kXk3w",
        ExportName = "sceNgs2VoiceGetStateFlags",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetStateFlags(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var flagsAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Voices.ContainsKey(voiceHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
        }

        // No flags set: voice is idle.
        if (flagsAddress != 0 && !ctx.TryWriteUInt64(flagsAddress, 0))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    private static int ValidateSystem(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(
                ctx,
                Systems.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : OrbisNgs2ErrorInvalidSystemHandle);
        }
    }

    private static bool TryCreateHandle(CpuContext ctx, uint type, ulong ownerHandle, out ulong handle)
    {
        handle = 0;
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, HandleStorageSize, 16, out handle))
        {
            return false;
        }

        Span<byte> data = stackalloc byte[(int)HandleStorageSize];
        data.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(data[0..8], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(data[8..16], ownerHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..20], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..28], type);
        return ctx.Memory.TryWrite(handle, data);
    }

    private static bool TryClearGuestBuffer(CpuContext ctx, ulong address, ulong length)
    {
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        for (ulong offset = 0; offset < length;)
        {
            var chunkSize = (int)Math.Min((ulong)zeroes.Length, length - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..chunkSize]))
            {
                return false;
            }

            offset += unchecked((uint)chunkSize);
        }

        return true;
    }

    private static void RemoveRackLocked(ulong rackHandle)
    {
        Racks.Remove(rackHandle);
        foreach (var voiceHandle in Voices
                     .Where(pair => pair.Value.RackHandle == rackHandle)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Voices.Remove(voiceHandle);
        }
    }

    private static bool ShouldTrace() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_NGS2"),
            "1",
            StringComparison.Ordinal);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
    [SysAbiExport(
        Nid = "xa8oL9dmXkM",
        ExportName = "sceNgs2PanInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2PanInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "1WsleK-MTkE",
        ExportName = "sceNgs2GeomCalcListener",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomCalcListener(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "0lbbayqDNoE",
        ExportName = "sceNgs2GeomResetSourceParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetSourceParam(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "7Lcfo8SmpsU",
        ExportName = "sceNgs2GeomResetListenerParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetListenerParam(CpuContext ctx) => ctx.SetReturn(0);
}
