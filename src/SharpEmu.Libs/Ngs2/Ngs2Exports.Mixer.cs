// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private sealed record RenderTarget(ulong Address, uint Type, int Channels, float[] Samples);
    private sealed record VoiceCallback(ulong Handler, ulong Data, ulong Voice, uint Flag, WaveBlock Block);

    private static int PcmSampleBytes(uint type) => type switch
    {
        0x10 or 0x11 => 1,
        0x12 or 0x13 => 2,
        0x14 or 0x15 => 3,
        0x16 or 0x17 or 0x18 or 0x19 => 4,
        0x1a or 0x1b => 8,
        _ => 0,
    };

    private static float ReadPcm(ReadOnlySpan<byte> data, uint type)
    {
        var sample = type switch
        {
            0x10 => unchecked((sbyte)data[0]) / 128f,
            0x11 => (data[0] - 128) / 128f,
            0x12 => BinaryPrimitives.ReadInt16LittleEndian(data) / 32768f,
            0x13 => BinaryPrimitives.ReadInt16BigEndian(data) / 32768f,
            0x14 => ((data[0] | data[1] << 8 | data[2] << 16) << 8 >> 8) / 8388608f,
            0x15 => ((data[2] | data[1] << 8 | data[0] << 16) << 8 >> 8) / 8388608f,
            0x16 => (float)(BinaryPrimitives.ReadInt32LittleEndian(data) / 2147483648.0),
            0x17 => (float)(BinaryPrimitives.ReadInt32BigEndian(data) / 2147483648.0),
            0x18 => BinaryPrimitives.ReadSingleLittleEndian(data),
            0x19 => BinaryPrimitives.ReadSingleBigEndian(data),
            0x1a => (float)BinaryPrimitives.ReadDoubleLittleEndian(data),
            0x1b => (float)BinaryPrimitives.ReadDoubleBigEndian(data),
            _ => 0f,
        };
        return float.IsFinite(sample) ? sample : 0f;
    }

    private static int RenderSystem(CpuContext ctx, ulong systemHandle, ulong infoAddress, uint count)
    {
        if (count > 32 || count != 0 && infoAddress == 0) return InvalidControlAddress;
        var targets = new List<RenderTarget>((int)count);
        SystemState system;
        try
        {
            lock (StateGate)
            {
                if (!Systems.TryGetValue(systemHandle, out system!)) return OrbisNgs2ErrorInvalidSystemHandle;
                Span<byte> info = stackalloc byte[RenderBufferInfoSize];
                for (uint index = 0; index < count; index++)
                {
                    if (infoAddress > ulong.MaxValue - RenderBufferInfoSize || !ctx.Memory.TryRead(infoAddress, info))
                        return InvalidControlAddress;
                    var address = BinaryPrimitives.ReadUInt64LittleEndian(info);
                    var size = BinaryPrimitives.ReadUInt64LittleEndian(info[8..]);
                    var type = BinaryPrimitives.ReadUInt32LittleEndian(info[16..]);
                    var channels = BinaryPrimitives.ReadUInt32LittleEndian(info[20..]);
                    if (type is not (0x12 or 0x13 or 0x18 or 0x19) || channels is < 1 or > 8) return InvalidWaveformFormat;
                    var required = (ulong)(system.GrainSamples * channels * PcmSampleBytes(type));
                    if (size < required || size > MaximumRenderBufferSize || address == 0 || address > ulong.MaxValue - required)
                        return InvalidControlSize;
                    var samples = ArrayPool<float>.Shared.Rent(system.GrainSamples * (int)channels);
                    samples.AsSpan(0, system.GrainSamples * (int)channels).Clear();
                    targets.Add(new RenderTarget(address, type, (int)channels, samples));
                    infoAddress += RenderBufferInfoSize;
                }

                foreach (var (handle, voice) in Voices)
                {
                    if (!voice.Playing || voice.Paused || PcmSampleBytes(voice.WaveformType) == 0 && voice.WaveformType is not (0x1c or 0x40) ||
                        !Racks.TryGetValue(voice.RackHandle, out var rack) || rack.SystemHandle != systemHandle) continue;
                    var samples = ArrayPool<float>.Shared.Rent(system.GrainSamples * voice.Channels);
                    try
                    {
                        samples.AsSpan(0, system.GrainSamples * voice.Channels).Clear();
                        var result = MixVoice(ctx, handle, voice, system, samples);
                        if (result != 0)
                        {
                            voice.ErrorCode = result;
                            voice.Playing = false;
                            voice.Stopped = true;
                            continue;
                        }
                        RouteVoice(systemHandle, voice, samples, voice.Channels, system.GrainSamples, targets, []);
                    }
                    finally { ArrayPool<float>.Shared.Return(samples); }
                }

                foreach (var target in targets)
                {
                    var result = WriteOutput(ctx, target, system.GrainSamples);
                    if (result != 0) return result;
                }
            }
            // Guest callbacks may enqueue more blocks or query voices. Never run them under StateGate.
            DeliverCallbacks(ctx, system);
            return 0;
        }
        finally
        {
            foreach (var target in targets) ArrayPool<float>.Shared.Return(target.Samples);
        }
    }

    private static int MixVoice(CpuContext ctx, ulong handle, VoiceState voice, SystemState system, float[] output)
    {
        var sampleBytes = voice.WaveformType is 0x1c or 0x40 ? 2 : PcmSampleBytes(voice.WaveformType);
        var frameBytes = sampleBytes * voice.Channels;
        // Guest streaming rings reuse buffers while their prefetched tail is still being rendered.
        if (voice.PcmCache.Length < 1025 * frameBytes)
        {
            voice.PcmCache = new byte[1025 * frameBytes];
            voice.PcmCacheBlock = null;
        }
        var cache = voice.PcmCache;
        var cachedBlock = voice.PcmCacheBlock;
        var cacheFirst = voice.PcmCacheFirst;
        var cacheFrames = voice.PcmCacheFrames;
        var step = voice.SourceRate / (double)system.SampleRate * voice.Pitch;
        var atrac9Config = voice.Atrac9?.Config;
        Span<byte> joinFrame = stackalloc byte[8 * sizeof(double)];
        try
        {
            for (var frame = 0; frame < system.GrainSamples; frame++)
            {
                AdvanceBlocks(voice, handle, system);
                if (voice.Blocks.Count == 0) break;
                var block = voice.Blocks.Peek();
                var sourceFrame = (long)voice.Position;
                if (atrac9Config is not null && !DecodeAtrac9Samples(voice, block, atrac9Config,
                    (uint)Math.Min(sourceFrame + 1, block.Frames - 1) + block.Skip))
                {
                    voice.Playing = false;
                    voice.Stopped = true;
                    return InvalidWaveformFormat;
                }
                if (!ReferenceEquals(block, cachedBlock) || sourceFrame < cacheFirst ||
                    Math.Min(sourceFrame + 1, block.Frames - 1) >= cacheFirst + cacheFrames)
                {
                    cacheFirst = sourceFrame;
                    cacheFrames = (int)Math.Min(1025L, block.Frames - sourceFrame);
                    if (block.Decoded is null && !ctx.Memory.TryRead(block.Address + (block.Skip + (ulong)sourceFrame) * (uint)frameBytes,
                        cache.AsSpan(0, cacheFrames * frameBytes)))
                    {
                        cachedBlock = null;
                        voice.Playing = false;
                        voice.Stopped = true;
                        break;
                    }
                    cachedBlock = block;
                }
                var relative = (int)(sourceFrame - cacheFirst);
                var next = Math.Min(relative + 1, cacheFrames - 1);
                var fraction = (float)(voice.Position - sourceFrame);
                WaveBlock? join = null;
                // A streaming block boundary must not clamp interpolation to the previous sample.
                if (fraction > 0 && sourceFrame + 1 == block.Frames)
                {
                    join = block.Repeats != 0 ? block : voice.Blocks.ElementAtOrDefault(1);
                    if (join is not null && (join.Frames == 0 || !ReferenceEquals(join, block) && !join.JoinPrevious))
                        join = null;
                    if (join is not null && atrac9Config is not null && !ReferenceEquals(join, block) &&
                        !DecodeAtrac9Samples(voice, join, atrac9Config, join.Skip))
                        return InvalidWaveformFormat;
                    if (join is not null && join.Decoded is null &&
                        !ctx.Memory.TryRead(join.Address + (ulong)join.Skip * (uint)frameBytes, joinFrame[..frameBytes]))
                        join = null;
                }
                for (var channel = 0; channel < voice.Channels; channel++)
                {
                    var a = block.Decoded is { } pcm
                        ? pcm[((long)block.Skip + sourceFrame - block.DecodedOffset) * voice.Channels + channel] / 32768f
                        : ReadPcm(cache.AsSpan(relative * frameBytes + channel * sampleBytes, sampleBytes), voice.WaveformType);
                    var b = block.Decoded is { } nextPcm
                        ? nextPcm[((long)block.Skip + Math.Min(sourceFrame + 1, block.Frames - 1) - block.DecodedOffset) * voice.Channels + channel] / 32768f
                        : ReadPcm(cache.AsSpan(next * frameBytes + channel * sampleBytes, sampleBytes), voice.WaveformType);
                    if (join is not null)
                        b = atrac9Config is not null && ReferenceEquals(join, block) && block.LoopFirstSample is { } firstSample
                            ? firstSample[channel] / 32768f
                            : join.Decoded is { } joinPcm
                            ? joinPcm[((long)join.Skip - join.DecodedOffset) * voice.Channels + channel] / 32768f
                            : ReadPcm(joinFrame.Slice(channel * sampleBytes, sampleBytes), voice.WaveformType);
                    output[frame * voice.Channels + channel] = a + (b - a) * fraction;
                }
                var advanced = (ulong)((long)(voice.Position + step) - sourceFrame);
                voice.DecodedSamples += advanced;
                if (voice.WaveformType != 0x40) voice.DecodedBytes += advanced * (uint)frameBytes;
                voice.Position += step;
            }
            AdvanceBlocks(voice, handle, system);
            return 0;
        }
        finally
        {
            voice.PcmCacheBlock = voice.Blocks.Count == 0 ? null : cachedBlock;
            voice.PcmCacheFirst = cacheFirst;
            voice.PcmCacheFrames = cacheFrames;
        }
    }

    private static void AdvanceBlocks(VoiceState voice, ulong handle, SystemState system)
    {
        while (voice.Blocks.TryPeek(out var block) && (block.Frames == 0 || voice.Position >= block.Frames))
        {
            if (block.Frames > 0 && block.Repeats != 0)
            {
                voice.Position -= block.Frames;
                if (block.Repeats != uint.MaxValue) block.Repeats--;
                block.Repeated++;
                if (voice.WaveformType == 0x40)
                {
                    block.EncodedCursor = 0;
                    block.DecodedOffset = 0;
                    block.DecodedFrames = 0;
                    block.LoopFirstSample = null;
                }
                if ((voice.CallbackFlags & 2) != 0 && voice.Callback != 0)
                    system.Callbacks.Enqueue(new VoiceCallback(voice.Callback, voice.CallbackData, handle, 2, Snapshot(block)));
            }
            else
            {
                voice.Position = Math.Max(0, voice.Position - block.Frames);
                voice.Blocks.Dequeue();
                if ((voice.CallbackFlags & 1) != 0 && voice.Callback != 0)
                    system.Callbacks.Enqueue(new VoiceCallback(voice.Callback, voice.CallbackData, handle, 1, Snapshot(block)));
            }
        }
        if (voice.Blocks.Count == 0 && (voice.Callback == 0 || (voice.CallbackFlags & 0x10000) != 0))
        {
            voice.Playing = voice.InUse = voice.Paused = voice.Stopped = false;
        }
    }

    private static WaveBlock Snapshot(WaveBlock block) => new()
    {
        Address = block.Address,
        ByteLength = block.ByteLength,
        UserData = block.UserData,
        Repeated = block.Repeated,
    };

    private static void RouteVoice(ulong systemHandle, VoiceState voice, float[] samples, int channels, int frames,
        List<RenderTarget> targets, HashSet<VoiceState> path)
    {
        if (!path.Add(voice)) return;
        try
        {
            if (Racks[voice.RackHandle].RackId is 0x3000 or 0x4003)
            {
                if (voice.Playing && !voice.Paused && voice.OutputId < targets.Count)
                {
                    var output = targets[(int)voice.OutputId];
                    AddChannels(samples, channels, output.Samples, output.Channels, frames, voice.Gain, null);
                }
                return;
            }
            foreach (var port in voice.Ports.Values)
            {
                if (port.Volume == 0 || port.PatchType == 0) continue;
                VoiceState? destination = null;
                if (port.PatchType == 1)
                {
                    if (!Voices.TryGetValue(port.Destination, out destination) && Racks.ContainsKey(port.Destination))
                        destination = Voices.Values.FirstOrDefault(v => v.RackHandle == port.Destination && v.VoiceIndex == 0);
                    if (destination is null || !destination.Playing || destination.Paused ||
                        !Racks.TryGetValue(destination.RackHandle, out var rack) || rack.SystemHandle != systemHandle) continue;
                }
                else if (port.PatchType != 2 || port.BufferId >= targets.Count) continue;
                var destChannels = destination?.Channels ?? targets[(int)port.BufferId].Channels;
                voice.Matrices.TryGetValue(port.MatrixId, out var levels);
                if (destination is null)
                {
                    AddChannels(samples, channels, targets[(int)port.BufferId].Samples, destChannels, frames, port.Volume, levels);
                    continue;
                }
                var routed = ArrayPool<float>.Shared.Rent(frames * destChannels);
                try
                {
                    routed.AsSpan(0, frames * destChannels).Clear();
                    AddChannels(samples, channels, routed, destChannels, frames, port.Volume, levels);
                    RouteVoice(systemHandle, destination, routed, destChannels, frames, targets, path);
                }
                finally { ArrayPool<float>.Shared.Return(routed); }
            }
        }
        finally { path.Remove(voice); }
    }

    private static void AddChannels(float[] source, int sourceChannels, float[] dest, int destChannels,
        int frames, float gain, float[]? levels)
    {
        for (var frame = 0; frame < frames; frame++)
            for (var output = 0; output < destChannels; output++)
            {
                float sample = 0;
                for (var input = 0; input < sourceChannels; input++)
                {
                    float level;
                    if (levels is not null && levels.Length >= sourceChannels * destChannels)
                        level = levels[input * destChannels + output];
                    else if (destChannels == 1)
                        level = 1f / sourceChannels;
                    else
                        level = sourceChannels == 1 && output < 2 || input == output ? 1f : 0f;
                    sample += source[frame * sourceChannels + input] * level;
                }
                dest[frame * destChannels + output] += sample * gain;
            }
    }

    private static int WriteOutput(CpuContext ctx, RenderTarget target, int frames)
    {
        var sampleBytes = PcmSampleBytes(target.Type);
        var count = frames * target.Channels;
        var buffer = ArrayPool<byte>.Shared.Rent(count * sampleBytes);
        var peak = 0f;
        try
        {
            var data = buffer.AsSpan(0, count * sampleBytes);
            for (var i = 0; i < count; i++)
            {
                var sample = Math.Clamp(target.Samples[i], -1f, 1f);
                peak = Math.Max(peak, Math.Abs(sample));
                var value = data[(i * sampleBytes)..];
                if (target.Type == 0x18) BinaryPrimitives.WriteSingleLittleEndian(value, sample);
                else if (target.Type == 0x19) BinaryPrimitives.WriteSingleBigEndian(value, sample);
                else
                {
                    var pcm = (short)Math.Clamp((int)MathF.Round(sample * 32768f), short.MinValue, short.MaxValue);
                    if (target.Type == 0x12) BinaryPrimitives.WriteInt16LittleEndian(value, pcm);
                    else BinaryPrimitives.WriteInt16BigEndian(value, pcm);
                }
            }
            if (ShouldTrace() && _renderCount % 200 == 0) Trace($"mix frames={frames} channels={target.Channels} type=0x{target.Type:X} peak={peak:F4}");
            return ctx.Memory.TryWrite(target.Address, data) ? 0 : InvalidControlAddress;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void DeliverCallbacks(CpuContext ctx, SystemState system)
    {
        Span<byte> info = stackalloc byte[64];
        while (true)
        {
            VoiceCallback callback;
            lock (StateGate)
            {
                if (!system.Callbacks.TryDequeue(out callback!)) return;
            }
            var scheduler = GuestThreadExecution.Scheduler;
            if (scheduler is null) continue;
            if (system.CallbackBuffer == 0)
            {
                if (!(ctx.Memory is IGuestMemoryAllocator allocator && allocator.TryAllocateGuestMemory(64, 16, out var allocated)) &&
                    !KernelMemoryCompatExports.TryAllocateHleData(ctx, 64, 16, out allocated)) return;
                system.CallbackBuffer = allocated;
            }
            info.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(info, callback.Data);
            BinaryPrimitives.WriteUInt64LittleEndian(info[8..], callback.Voice);
            BinaryPrimitives.WriteUInt32LittleEndian(info[16..], callback.Flag);
            BinaryPrimitives.WriteUInt64LittleEndian(info[24..], callback.Block.UserData);
            BinaryPrimitives.WriteUInt64LittleEndian(info[32..], callback.Block.Address);
            BinaryPrimitives.WriteUInt64LittleEndian(info[40..], callback.Block.ByteLength);
            BinaryPrimitives.WriteUInt32LittleEndian(info[48..], callback.Block.Repeated);
            if (!ctx.Memory.TryWrite(system.CallbackBuffer, info)) return;
            if (!scheduler.TryCallGuestFunction(ctx, callback.Handler, system.CallbackBuffer, 0, 0, 0, 0, 0,
                "ngs2_waveform_block", out _, out var error)) Trace($"callback failed: {error}");
        }
    }
}
