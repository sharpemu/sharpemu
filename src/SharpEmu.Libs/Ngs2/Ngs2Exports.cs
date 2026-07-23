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
        // Pitch ratio from sampler pitch param (1 = unity). Applied as a step scale.
        public float PitchRatio { get; set; } = 1f;
        // De-click envelopes (output frames at mix rate). Speaker "power-plug" pops
        // come from hard starts/stops and discontinuous stream joins.
        public int FadeInLeft { get; set; }
        public int FadeOutLeft { get; set; }
        public int FadeOutTotal { get; set; }

        // Guest stream windows last seen on waveform-blocks params. Titles often
        // rotate several ring halves; we re-pull on render when content changes.
        public uint StreamFlags { get; set; }
        public StreamWindow[] StreamWindows { get; } = new StreamWindow[8];
        public int StreamWindowCount { get; set; }
        // Coarse fingerprint of the last bed we mixed. Double-buffer rings often
        // re-submit the same grain under a new address; re-arming that is the tick.
        public long LastBedFingerprint { get; set; }
        public int LastBedSampleCount { get; set; }
        public int LastBedRate { get; set; }
        public long LastBedArmTickMs { get; set; }

        // Last OrbisNgs2WaveformFormat from a setup param (classic or custom sampler).
        public Ngs2WaveformArmer.WaveformFormat Format { get; set; } =
            new(Ngs2WaveformArmer.WaveformTypePcmI16, 1, 48000, 0, 0, 0);
    }

    private struct StreamWindow
    {
        public ulong DataAddr;
        public int ByteSize;
        public long ContentKey;
    }

    private const int MixFadeInFrames = 96;   // ~2 ms @ 48 kHz
    private const int MixFadeOutFrames = 128;
    private const int StreamCrossfadeSamples = 384; // longer join = less tick
    private const int DefaultStreamProbeBytes = 64 * 1024;
    // Suppress re-firing the same short stream grain (menu double-beep). Real SFX
    // usually differs in length/rate enough to pass this gate.
    private const int BedRetriggerCooldownMs = 4000;
    private const int BedLengthMatchSlack = 512;

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

    // Parse OrbisNgs2VoiceParamHeader list: u16 size, s16 next, u32 id.
    // Classic sampler ids live in 0x1xxxxxxx; some titles (custom sampler racks)
    // use the parallel 0x4xxxxxxx set with the same body layouts from the public
    // OrbisNgs2SamplerVoice* structs. Both are handled generically.
    private static void HandleVoiceParams(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        if (paramList == 0)
        {
            return;
        }

        var offset = paramList;
        for (var guard = 0; guard < 32; guard++)
        {
            if (!ctx.TryReadUInt16(offset, out var size) ||
                !ctx.TryReadUInt16(offset + 2, out var nextRaw) ||
                !ctx.TryReadUInt32(offset + 4, out var id))
            {
                return;
            }

            if (size < 8 || size > 0x1000)
            {
                return;
            }

            switch (id)
            {
                case 0x10000000:
                case 0x40010000:
                    // OrbisNgs2SamplerVoiceSetupParam / custom equivalent.
                    ApplySetupParam(ctx, voiceHandle, offset, size);
                    break;
                case 0x10000001:
                case 0x40010001:
                    // OrbisNgs2SamplerVoiceWaveformBlocksParam / custom equivalent.
                    ApplyWaveformBlocksParam(ctx, voiceHandle, offset, size);
                    break;
                case 0x10000002:
                case 0x40010002:
                    // Waveform address range (from/to) — arm from `from`.
                    ApplyWaveformAddressParam(ctx, voiceHandle, offset, size);
                    break;
                case 0x10000004:
                case 0x40010004:
                case 0x10000005:
                case 0x40010005:
                    // Pitch ratio (float @ +8).
                    ApplyPitchParam(ctx, voiceHandle, offset, size);
                    break;
                case 0x20010001:
                    ApplyPortMatrixParam(ctx, voiceHandle, offset);
                    break;
                case 0x40001300:
                    // Continuous control blob; trailing finite floats often encode gain.
                    ApplyContinuousControlParam(ctx, voiceHandle, offset, size);
                    break;
                case 0x7:
                    // OrbisNgs2VoiceCallbackParam (handler + user data) — not sample data.
                    break;
                default:
                    // Small volume-like params only; avoid treating junk ints as wave ptrs.
                    if (size >= 16 && size <= 32)
                    {
                        ApplyPortMatrixParam(ctx, voiceHandle, offset);
                    }

                    break;
            }

            var next = unchecked((short)nextRaw);
            if (next <= 0)
            {
                return;
            }

            offset += (ulong)next;
        }
    }

    // Setup: header + OrbisNgs2WaveformFormat {type, ch, rate, config, frameOffset, margin}.
    private static void ApplySetupParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset, ushort size)
    {
        if (size < 8 + 24)
        {
            return;
        }

        if (!Ngs2WaveformArmer.TryReadFormat(ctx.Memory, paramOffset + 8, out var format))
        {
            return;
        }

        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var voice))
            {
                voice.Format = format;
                if (format.SampleRate > 0)
                {
                    voice.SourceRate = (int)format.SampleRate;
                }
            }
        }

        if (ShouldTrace() && Interlocked.Increment(ref _setupDumps) <= 8)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.setup voice=0x{voiceHandle:X16} type=0x{format.WaveformType:X} ch={format.NumChannels} rate={format.SampleRate} cfg=0x{format.ConfigData:X}");
        }
    }

    // Max mono samples kept when appending stream chunks (~2s @ 48 kHz).
    private const int MaxStreamQueueSamples = 96_000;

    // Waveform blocks: header + data*, flags, numBlocks, aBlock*.
    // Only OrbisNgs2WaveformBlock.numRepeats is treated as an explicit loop request;
    // param flags are not guessed (0x11 appears on one-shots and streams alike and
    // false-looping short chunks produces a menu tick).
    private static void ApplyWaveformBlocksParam(
        CpuContext ctx, ulong voiceHandle, ulong paramOffset, ushort size)
    {
        if (size < 32)
        {
            return;
        }

        if (!ctx.TryReadUInt64(paramOffset + 8, out var dataAddr))
        {
            return;
        }

        // data == 0 stops the sampler — fade out instead of a hard cut (pop).
        if (dataAddr == 0)
        {
            lock (StateGate)
            {
                if (Voices.TryGetValue(voiceHandle, out var voice))
                {
                    voice.StreamWindowCount = 0;
                    if (voice.Playing && voice.Pcm is not null)
                    {
                        voice.FadeOutTotal = MixFadeOutFrames;
                        voice.FadeOutLeft = MixFadeOutFrames;
                    }
                }
            }

            return;
        }

        if (dataAddr <= 0x10000)
        {
            return;
        }

        if (!ctx.TryReadUInt32(paramOffset + 16, out var flags) ||
            !ctx.TryReadUInt32(paramOffset + 20, out var numBlocks) ||
            !ctx.TryReadUInt64(paramOffset + 24, out var blockAddr))
        {
            return;
        }

        // Cap blocks so a corrupt numBlocks cannot allocate forever.
        numBlocks = Math.Min(numBlocks, 16u);
        var blocks = new Ngs2WaveformArmer.WaveformBlock[Math.Max(1, (int)numBlocks)];
        var blockCount = 0;

        if (numBlocks > 0 && blockAddr > 0x10000)
        {
            for (uint i = 0; i < numBlocks; i++)
            {
                var addr = blockAddr + (i * 32);
                if (Ngs2WaveformArmer.TryReadBlock(ctx.Memory, addr, out var block))
                {
                    // Strip untrusted repeats — stack aBlock often carries junk
                    // that false-loops short stream windows (menu speaker ticks).
                    blocks[blockCount++] = block with { NumRepeats = 0 };
                    continue;
                }

                // Block header readable but dataSize==0 is common for streaming
                // fills: keep numSamples when present and synthesize a byte size
                // from the voice format (PCM16 bytes-per-frame).
                var rawBlock = new byte[32];
                if (!ctx.Memory.TryRead(addr, rawBlock))
                {
                    continue;
                }

                var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(rawBlock.AsSpan(0, 4));
                var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(rawBlock.AsSpan(4, 4));
                var numSamples = BinaryPrimitives.ReadUInt32LittleEndian(rawBlock.AsSpan(16, 4));
                var userData = BinaryPrimitives.ReadUInt64LittleEndian(rawBlock.AsSpan(24, 8));

                if (dataSize == 0 && numSamples > 0 && numSamples <= 8 * 1024 * 1024)
                {
                    var ch = 2u;
                    lock (StateGate)
                    {
                        if (Voices.TryGetValue(voiceHandle, out var v) && v.Format.NumChannels is > 0 and <= 8)
                        {
                            ch = v.Format.NumChannels;
                        }
                    }

                    // PCM16 bytes; AT9/VAG containers ignore this and use magic.
                    dataSize = numSamples * ch * 2;
                }

                if (dataSize == 0 || dataSize > 8 * 1024 * 1024)
                {
                    // Probe window when the block omits size; decoder rejects silence.
                    dataSize = 64 * 1024;
                }

                blocks[blockCount++] = new Ngs2WaveformArmer.WaveformBlock(
                    dataOffset,
                    dataSize,
                    NumRepeats: 0,
                    BinaryPrimitives.ReadUInt32LittleEndian(rawBlock.AsSpan(12, 4)),
                    numSamples,
                    userData);
            }
        }

        // Some titles leave aBlock null and put a single implicit full buffer.
        if (blockCount == 0)
        {
            blocks[0] = new Ngs2WaveformArmer.WaveformBlock(
                DataOffset: 0,
                DataSize: 64 * 1024,
                NumRepeats: 0,
                NumSkipSamples: 0,
                NumSamples: 0,
                UserData: 0);
            blockCount = 1;
        }

        Ngs2WaveformArmer.WaveformFormat format;
        var streamBytes = DefaultStreamProbeBytes;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return;
            }

            format = voice.Format;
            if (blockCount > 0 && blocks[0].DataSize is > 0 and <= 8 * 1024 * 1024)
            {
                streamBytes = (int)blocks[0].DataSize;
            }

            // Remember every ring half; SystemRender re-pulls when content changes.
            // Never restart an identical snapshot after it ends (that re-ticks).
            RememberStreamWindow(voice, dataAddr, streamBytes);
            voice.StreamFlags = flags;
        }

        if (ShouldTrace() && Interlocked.Increment(ref _blockDumps) <= 16)
        {
            var b0 = blockCount > 0 ? blocks[0] : default;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.blocks voice=0x{voiceHandle:X16} data=0x{dataAddr:X} flags=0x{flags:X} n={numBlocks} loop=False off={b0.DataOffset} size={b0.DataSize} samples={b0.NumSamples}");
        }

        if (TryArmStreamWindow(ctx, voiceHandle, dataAddr, format, streamBytes, forceLog: true))
        {
            return;
        }

        if (ShouldTrace() && Interlocked.Increment(ref _missDumps) <= 24)
        {
            Span<byte> head = stackalloc byte[32];
            var headHex = ctx.Memory.TryRead(dataAddr, head) ? Convert.ToHexString(head) : "unreadable";
            var b0 = blockCount > 0 ? blocks[0] : default;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.miss voice=0x{voiceHandle:X16} data=0x{dataAddr:X} off={b0.DataOffset} size={b0.DataSize} head={headHex}");
        }
    }

    private static void RememberStreamWindow(VoiceState voice, ulong dataAddr, int byteSize)
    {
        for (var i = 0; i < voice.StreamWindowCount; i++)
        {
            if (voice.StreamWindows[i].DataAddr != dataAddr)
            {
                continue;
            }

            var existing = voice.StreamWindows[i];
            existing.ByteSize = byteSize;
            // Move to front (most recent).
            for (var j = i; j > 0; j--)
            {
                voice.StreamWindows[j] = voice.StreamWindows[j - 1];
            }

            voice.StreamWindows[0] = existing;
            return;
        }

        var count = Math.Min(voice.StreamWindowCount + 1, voice.StreamWindows.Length);
        for (var i = count - 1; i > 0; i--)
        {
            voice.StreamWindows[i] = voice.StreamWindows[i - 1];
        }

        voice.StreamWindows[0] = new StreamWindow
        {
            DataAddr = dataAddr,
            ByteSize = byteSize,
            ContentKey = 0,
        };
        voice.StreamWindowCount = count;
    }

    // Decode a guest PCM/container window and arm only when content is NEW.
    // Restarting the same snapshot after it ends is what reintroduced the tick.
    private static bool TryArmStreamWindow(
        CpuContext ctx,
        ulong voiceHandle,
        ulong dataAddr,
        Ngs2WaveformArmer.WaveformFormat format,
        int byteSize,
        bool forceLog)
    {
        if (dataAddr <= 0x10000 || byteSize <= 0)
        {
            return false;
        }

        byteSize = Math.Clamp(byteSize, 256, 8 * 1024 * 1024);
        var block = new Ngs2WaveformArmer.WaveformBlock(0, (uint)byteSize, 0, 0, 0, 0);
        if (!Ngs2WaveformArmer.TryArmFromWaveformBlocks(
                ctx.Memory,
                dataAddr,
                format,
                new[] { block },
                loop: false,
                out var armed) &&
            !Ngs2WaveformArmer.TryArmFromGuestAddress(ctx.Memory, dataAddr, out armed))
        {
            return false;
        }

        long key = armed.Samples.Length;
        key = (key * 397) ^ armed.SampleRate;
        var step = Math.Max(1, armed.Samples.Length / 32);
        for (var i = 0; i < armed.Samples.Length; i += step)
        {
            key = (key * 31) ^ armed.Samples[i];
        }

        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return false;
            }

            // Update key for this window.
            for (var i = 0; i < voice.StreamWindowCount; i++)
            {
                if (voice.StreamWindows[i].DataAddr != dataAddr)
                {
                    continue;
                }

                var window = voice.StreamWindows[i];
                if (window.ContentKey == key)
                {
                    // Same snapshot already consumed — never restart it (tick).
                    return voice.Playing;
                }

                window.ContentKey = key;
                window.ByteSize = byteSize;
                voice.StreamWindows[i] = window;
                break;
            }
        }

        ApplyArmedWaveform(voiceHandle, dataAddr, armed);
        if (forceLog)
        {
            // ApplyArmedWaveform already logs on first arm path.
        }

        return true;
    }

    // Re-pull all remembered ring halves; only NEW content is appended/armed.
    private static void PullPendingStreams(CpuContext ctx, ulong systemHandle)
    {
        if ((Interlocked.Increment(ref _streamPullCount) & 1) != 0)
        {
            return;
        }

        List<(ulong voice, ulong addr, int bytes, Ngs2WaveformArmer.WaveformFormat format)> pending;
        lock (StateGate)
        {
            pending = new List<(ulong, ulong, int, Ngs2WaveformArmer.WaveformFormat)>();
            foreach (var pair in Voices)
            {
                var voice = pair.Value;
                if (voice.StreamWindowCount <= 0)
                {
                    continue;
                }

                if (!Racks.TryGetValue(voice.RackHandle, out var rack) ||
                    rack.SystemHandle != systemHandle)
                {
                    continue;
                }

                for (var i = 0; i < voice.StreamWindowCount; i++)
                {
                    var w = voice.StreamWindows[i];
                    if (w.DataAddr > 0x10000 && w.ByteSize > 0)
                    {
                        pending.Add((pair.Key, w.DataAddr, w.ByteSize, voice.Format));
                    }
                }
            }
        }

        foreach (var item in pending)
        {
            TryArmStreamWindow(ctx, item.voice, item.addr, item.format, item.bytes, forceLog: false);
        }
    }

    private static long _streamPullCount;

    // Waveform address param: {from, to} — arm from `from` using the last format.
    private static void ApplyWaveformAddressParam(
        CpuContext ctx, ulong voiceHandle, ulong paramOffset, ushort size)
    {
        if (size < 24 ||
            !ctx.TryReadUInt64(paramOffset + 8, out var from) ||
            from <= 0x10000)
        {
            return;
        }

        Ngs2WaveformArmer.WaveformFormat format;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return;
            }

            format = voice.Format;
        }

        var block = new Ngs2WaveformArmer.WaveformBlock(0, 64 * 1024, 0, 0, 0, 0);
        if (Ngs2WaveformArmer.TryArmFromWaveformBlocks(
                ctx.Memory, from, format, new[] { block }, loop: false, out var armed) ||
            Ngs2WaveformArmer.TryArmFromGuestAddress(ctx.Memory, from, out armed))
        {
            ApplyArmedWaveform(voiceHandle, from, armed);
        }
    }

    private static void ApplyPitchParam(
        CpuContext ctx, ulong voiceHandle, ulong paramOffset, ushort size)
    {
        if (size < 12 || !ctx.TryReadUInt32(paramOffset + 8, out var bits))
        {
            return;
        }

        var ratio = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(ratio) || ratio <= 0f || ratio > 8f)
        {
            return;
        }

        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var voice))
            {
                voice.PitchRatio = ratio;
            }
        }
    }

    private static long _missDumps;
    private static long _setupDumps;
    private static long _blockDumps;
    private static long _streamAppendDumps;

    private static void ApplyArmedWaveform(
        ulong voiceHandle, ulong dataAddr, Ngs2WaveformArmer.ArmedWaveform armed)
    {
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return;
            }

            // One-shot / stream-append only. Never loop a short ring snapshot.
            // Double-buffer rings re-submit near-identical grains under new
            // addresses — appending those is the remaining menu tick.
            var samples = armed.Samples;
            var rate = armed.SampleRate > 0 ? armed.SampleRate : 48000;
            var fingerprint = BedFingerprint(samples, rate);
            var nowMs = Environment.TickCount64;

            // Same short stream grain re-submitted with slight peak drift → double beep.
            if (IsRetriggerOfRecentBed(voice, samples.Length, rate, fingerprint, nowMs))
            {
                return;
            }

            // Already playing with enough buffer left — do not splice another grain.
            if (voice.Playing &&
                voice.Pcm is not null &&
                voice.Position < voice.Pcm.Length * 0.85)
            {
                return;
            }

            // Fresh start only. No stream-append of ring halves (that stitched the
            // beeps). Distinct SFX on other voices still arm independently.
            voice.Pcm = samples;
            voice.SourceAddr = dataAddr;
            voice.SourceRate = rate;
            voice.LoopStart = -1;
            voice.LoopEnd = samples.Length;
            voice.Position = 0;
            voice.Playing = true;
            voice.FadeOutLeft = 0;
            voice.FadeInLeft = MixFadeInFrames;
            voice.LastBedFingerprint = fingerprint;
            voice.LastBedSampleCount = samples.Length;
            voice.LastBedRate = rate;
            voice.LastBedArmTickMs = nowMs;
        }

        if (ShouldTrace())
        {
            var peak = 0;
            for (var i = 0; i < armed.Samples.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs((int)armed.Samples[i]));
            }

            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.arm voice=0x{voiceHandle:X16} addr=0x{dataAddr:X} fmt={armed.Format} rate={armed.SampleRate} samples={armed.Samples.Length} loop=-1 peak={peak}");
        }
    }

    // Very coarse bed identity. Peak is bucketed hard so slow peak climb on a
    // streaming ring (20561→24007) does not look like a new sound.
    private static long BedFingerprint(short[] samples, int rate)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var peak = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = Math.Abs((int)samples[i]);
            if (a > peak)
            {
                peak = a;
            }
        }

        long key = (samples.Length / 256) * 1_000_003L;
        key = (key * 397) ^ (rate / 1000);
        key = (key * 397) ^ (peak / 4096);
        return key;
    }

    private static bool IsRetriggerOfRecentBed(
        VoiceState voice, int sampleCount, int rate, long fingerprint, long nowMs)
    {
        if (voice.LastBedArmTickMs == 0)
        {
            return false;
        }

        if (nowMs - voice.LastBedArmTickMs > BedRetriggerCooldownMs)
        {
            return false;
        }

        if (voice.LastBedRate != 0 && voice.LastBedRate != rate)
        {
            return false;
        }

        if (voice.LastBedFingerprint != 0 && voice.LastBedFingerprint == fingerprint)
        {
            return true;
        }

        // Same length class within slack — typical stream grain re-fire.
        if (voice.LastBedSampleCount > 0 &&
            Math.Abs(voice.LastBedSampleCount - sampleCount) <= BedLengthMatchSlack)
        {
            return true;
        }

        return false;
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
        // Same floor as continuous control: 0 must not mute an armed voice.
        if (!float.IsFinite(level) || level < 0.01f || level > 8f)
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

    // Continuous-control payloads often carry level floats. Dead Cells' 0x40001300
    // body ends with padding zeros after a real level (~0.7–1.0). Treating 0 as a
    // valid level muted every voice right after arm — "no audio, no glitches".
    private static void ApplyContinuousControlParam(
        CpuContext ctx, ulong voiceHandle, ulong paramOffset, uint size)
    {
        if (size < 12)
        {
            return;
        }

        float? gain = null;
        for (var o = 8; o + 4 <= (int)size; o += 4)
        {
            if (!ctx.TryReadUInt32(paramOffset + (ulong)o, out var bits))
            {
                break;
            }

            var value = BitConverter.UInt32BitsToSingle(bits);
            // Ignore 0 / denorms / junk ints that decode as tiny floats.
            if (float.IsFinite(value) && value >= 0.01f && value <= 8f)
            {
                gain = value;
            }
        }

        if (gain is null)
        {
            return;
        }

        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var voice))
            {
                voice.Gain = gain.Value;
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

                // Titles often arm a stream pointer before the ring half is full.
                // Re-pull remembered windows each render so BGM can start without
                // false-looping an empty/short snapshot.
                PullPendingStreams(ctx, systemHandle);

                MixVoicesIntoGrain(ctx, systemHandle, bufferAddress, bufferSize, channels);

                if (ShouldTrace() && Interlocked.Increment(ref _renderInfoDumps) <= 4)
                {
                    Span<byte> rbi = stackalloc byte[RenderBufferInfoSize];
                    ctx.Memory.TryRead(entryAddress, rbi);
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] ngs2.renderbufinfo addr=0x{bufferAddress:X} size={bufferSize} ch={channels} raw={Convert.ToHexString(rbi)}");
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
            var skippedIdle = 0;
            var skippedRack = 0;
            var mixedVoices = 0;
            lock (StateGate)
            {
                foreach (var pair in Voices)
                {
                    var voice = pair.Value;
                    if (!voice.Playing || voice.Pcm is null || voice.Pcm.Length == 0)
                    {
                        if (voice.Pcm is not null)
                        {
                            skippedIdle++;
                        }

                        continue;
                    }

                    if (!Racks.TryGetValue(voice.RackHandle, out var rack) ||
                        rack.SystemHandle != systemHandle)
                    {
                        skippedRack++;
                        continue;
                    }

                    MixOneVoice(accum, capacityFrames, channels, voice);
                    mixedAnything = true;
                    mixedVoices++;
                }
            }

            if (mixedAnything)
            {
                WriteGrain(ctx, bufferAddress, accum, floatCount);
                if (ShouldTrace() && Interlocked.Increment(ref _mixPeakDumps) <= 8)
                {
                    var peak = 0f;
                    for (var i = 0; i < floatCount; i++)
                    {
                        var a = Math.Abs(accum[i]);
                        if (a > peak)
                        {
                            peak = a;
                        }
                    }

                    if (peak > 0.0001f)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] ngs2.mix peak={peak:F4} voices={mixedVoices} frames={capacityFrames} ch={channels}");
                    }
                }
            }
            else if (ShouldTrace() &&
                     (skippedIdle > 0 || skippedRack > 0) &&
                     Interlocked.Increment(ref _mixStatusDumps) <= 4)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] ngs2.mixstat mixed=0 idle={skippedIdle} rackmiss={skippedRack}");
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(accum);
        }
    }

    private static long _mixPeakDumps;
    private static long _mixStatusDumps;

    // Resample one voice from its source rate to 48 kHz (nearest-sample) and add
    // it to the front stereo pair. Advances the voice cursor; loops seamless beds
    // or ends one-shots with an optional fade-out. Must be called under StateGate.
    private static void MixOneVoice(float[] accum, int frames, int channels, VoiceState voice)
    {
        var pcm = voice.Pcm!;
        var end = voice.LoopEnd > 0 && voice.LoopEnd <= pcm.Length ? voice.LoopEnd : pcm.Length;
        var loopStart = voice.LoopStart;
        var pitch = voice.PitchRatio > 0f && float.IsFinite(voice.PitchRatio)
            ? voice.PitchRatio
            : 1f;
        // Headroom so full-scale PCM16 stays audible without constant peak=1.0 ticks.
        var step = (voice.SourceRate / OutputSampleRate) * pitch;
        var baseGain = (voice.Gain * 0.8f) / 32768f;
        var pos = voice.Position;
        for (var f = 0; f < frames; f++)
        {
            var idx = (int)pos;
            if (idx >= end || idx < 0)
            {
                if (loopStart >= 0 && loopStart < end)
                {
                    pos = loopStart + (pos - end);
                    while (pos >= end)
                    {
                        pos = loopStart + (pos - end);
                    }

                    if (pos < loopStart)
                    {
                        pos = loopStart;
                    }

                    idx = (int)pos;
                }
                else
                {
                    voice.Playing = false;
                    voice.FadeInLeft = 0;
                    voice.FadeOutLeft = 0;
                    break;
                }
            }

            if (idx < 0 || idx >= pcm.Length)
            {
                voice.Playing = false;
                break;
            }

            var env = 1f;
            if (voice.FadeInLeft > 0)
            {
                var done = MixFadeInFrames - voice.FadeInLeft;
                env *= (done + 1) / (float)MixFadeInFrames;
                voice.FadeInLeft--;
            }

            if (voice.FadeOutLeft > 0 && voice.FadeOutTotal > 0)
            {
                env *= voice.FadeOutLeft / (float)voice.FadeOutTotal;
                voice.FadeOutLeft--;
                if (voice.FadeOutLeft <= 0)
                {
                    voice.Playing = false;
                    voice.Pcm = null;
                    voice.SourceAddr = 0;
                    voice.Position = 0;
                }
            }

            var sample = pcm[idx] * baseGain * env;
            sample = SoftClip(sample);
            var baseIndex = f * channels;
            accum[baseIndex] += sample;
            if (channels > 1)
            {
                accum[baseIndex + 1] += sample;
            }

            if (!voice.Playing)
            {
                break;
            }

            pos += step;
        }

        voice.Position = pos;
    }

    private static float SoftClip(float x)
    {
        // Soft knee from ~0.7 so we rarely hard-rail at ±1 (harsh ticks/crust).
        const float knee = 0.7f;
        var ax = Math.Abs(x);
        if (ax <= knee)
        {
            return x;
        }

        // Asymptote toward ~0.95 instead of 1.0.
        var t = ax - knee;
        var y = knee + (t / (1f + t * 3f));
        if (y > 0.95f)
        {
            y = 0.95f;
        }

        return x < 0 ? -y : y;
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
