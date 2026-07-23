// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using LibAtrac9;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

/// <summary>
/// Arms an NGS2 voice from guest memory using public OrbisNgs2Waveform* layouts.
/// Supports classic sampler param ids and the parallel custom-sampler set
/// (same body structs). Data may be VAGp, RIFF/WAVE, or raw PCM/AT9 described
/// by OrbisNgs2WaveformFormat.
/// </summary>
internal static class Ngs2WaveformArmer
{
    private static readonly Guid Atrac9SubFormat = new("47E142D2-36BA-4D8D-88FC-61654F8C836C");
    private const int MaxWaveformBytes = 8 * 1024 * 1024;
    private const int NestedPointerScanBytes = 0x80;

    // Waveform type values used with OrbisNgs2WaveformFormat. Type 0x12 is seen
    // on custom-sampler setups with configData=0 (raw interleaved PCM). AT9
    // titles put the 4-byte config into WaveformFormat.configData.
    public const uint WaveformTypePcmI16 = 0x01;
    public const uint WaveformTypePcmF32 = 0x02;
    public const uint WaveformTypeVag = 0x03;
    public const uint WaveformTypeAtrac9 = 0x04;
    public const uint WaveformTypePcmI16Alt = 0x12;

    public readonly record struct WaveformFormat(
        uint WaveformType,
        uint NumChannels,
        uint SampleRate,
        uint ConfigData,
        uint FrameOffset,
        uint FrameMargin);

    public readonly record struct WaveformBlock(
        uint DataOffset,
        uint DataSize,
        uint NumRepeats,
        uint NumSkipSamples,
        uint NumSamples,
        ulong UserData);

    public readonly record struct ArmedWaveform(
        short[] Samples,
        int SampleRate,
        int LoopStart,
        int LoopEnd,
        string Format);

    public static bool TryReadFormat(ICpuMemory memory, ulong address, out WaveformFormat format)
    {
        format = default;
        Span<byte> raw = stackalloc byte[24];
        if (!memory.TryRead(address, raw))
        {
            return false;
        }

        format = new WaveformFormat(
            BinaryPrimitives.ReadUInt32LittleEndian(raw[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]));
        return format.NumChannels is > 0 and <= 8 && format.SampleRate is > 0 and <= 192000;
    }

    public static bool TryReadBlock(ICpuMemory memory, ulong address, out WaveformBlock block)
    {
        block = default;
        Span<byte> raw = stackalloc byte[32];
        if (!memory.TryRead(address, raw))
        {
            return false;
        }

        block = new WaveformBlock(
            BinaryPrimitives.ReadUInt32LittleEndian(raw[0..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(raw[24..]));
        return block.DataSize > 0 && block.DataSize <= MaxWaveformBytes;
    }

    /// <summary>
    /// Arm from OrbisNgs2SamplerVoiceWaveformBlocksParam fields: base data pointer
    /// plus an array of OrbisNgs2WaveformBlock describing slices inside it.
    /// </summary>
    public static bool TryArmFromWaveformBlocks(
        ICpuMemory memory,
        ulong dataAddress,
        WaveformFormat format,
        ReadOnlySpan<WaveformBlock> blocks,
        out ArmedWaveform waveform) =>
        TryArmFromWaveformBlocks(memory, dataAddress, format, blocks, loop: false, out waveform);

    public static bool TryArmFromWaveformBlocks(
        ICpuMemory memory,
        ulong dataAddress,
        WaveformFormat format,
        ReadOnlySpan<WaveformBlock> blocks,
        bool loop,
        out ArmedWaveform waveform)
    {
        waveform = default;
        if (dataAddress <= 0x10000 || blocks.Length == 0)
        {
            return false;
        }

        var rate = format.SampleRate > 0 ? (int)format.SampleRate : 48000;
        var channels = format.NumChannels is > 0 and <= 8 ? (int)format.NumChannels : 1;

        // Concatenate decoded mono PCM from every block (most titles send one).
        short[]? combined = null;
        var totalWritten = 0;
        var usedFormat = "raw";

        for (var i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i];
            if (block.DataSize == 0 || block.DataSize > MaxWaveformBytes)
            {
                continue;
            }

            var addr = dataAddress + block.DataOffset;
            var raw = new byte[block.DataSize];
            if (!memory.TryRead(addr, raw))
            {
                continue;
            }

            if (!TryDecodePayload(raw, format, channels, rate, block, out var piece))
            {
                continue;
            }

            usedFormat = piece.Format;
            if (combined is null)
            {
                combined = piece.Samples;
                totalWritten = piece.Samples.Length;
            }
            else
            {
                var next = new short[totalWritten + piece.Samples.Length];
                combined.AsSpan(0, totalWritten).CopyTo(next);
                piece.Samples.CopyTo(next.AsSpan(totalWritten));
                combined = next;
                totalWritten = next.Length;
            }
        }

        if (combined is null || totalWritten <= 0)
        {
            // Fallback: whole buffer at dataAddress may be a self-describing container
            // (some titles omit usable blocks and just point at VAGp/RIFF).
            if (!TryArmFromGuestAddress(memory, dataAddress, out waveform))
            {
                return false;
            }

            if (loop && waveform.LoopStart < 0)
            {
                waveform = waveform with { LoopStart = 0, LoopEnd = waveform.Samples.Length };
            }

            return true;
        }

        if (totalWritten != combined.Length)
        {
            Array.Resize(ref combined, totalWritten);
        }

        // Trim to the active region so 64 KiB probe windows full of leading/trailing
        // zeros (or one short SFX in a large buffer) do not click at the edges.
        // Short buffers (unit tests / tiny SFX) keep as-is; only trim long probes.
        if (totalWritten >= 256)
        {
            if (!TryTrimActiveRegion(combined.AsSpan(0, totalWritten), out var trimmed) ||
                trimmed.Length < 32)
            {
                return false;
            }

            combined = trimmed;
        }
        else if (!HasNonZeroSample(combined.AsSpan(0, totalWritten)))
        {
            return false;
        }
        else if (totalWritten != combined.Length)
        {
            Array.Resize(ref combined, totalWritten);
        }

        var loopStart = loop ? 0 : -1;
        waveform = new ArmedWaveform(combined, rate, loopStart, combined.Length, usedFormat);
        return true;
    }

    /// <summary>
    /// Bake a seamless loop: blend the tail into the head, then drop the tail
    /// so Mix can wrap without a DC step (menu speaker ticks).
    /// </summary>
    public static short[] MakeSeamlessLoop(short[] samples, int crossfadeSamples)
    {
        if (samples.Length < crossfadeSamples * 2 + 64)
        {
            // Too short to crossfade usefully — mild edge fades only.
            var copy = (short[])samples.Clone();
            var edge = Math.Min(64, copy.Length / 4);
            for (var i = 0; i < edge; i++)
            {
                var t = (i + 1) / (float)(edge + 1);
                copy[i] = (short)(copy[i] * t);
                copy[copy.Length - 1 - i] = (short)(copy[copy.Length - 1 - i] * t);
            }

            return copy;
        }

        var xf = Math.Min(crossfadeSamples, samples.Length / 4);
        var loopLen = samples.Length - xf;
        var dest = new short[loopLen];
        Array.Copy(samples, 0, dest, 0, loopLen);
        for (var i = 0; i < xf; i++)
        {
            var t = (i + 1) / (float)(xf + 1);
            var head = samples[i];
            var tail = samples[loopLen + i];
            // Equal-power-ish linear blend of tail→head at the wrap point.
            dest[i] = (short)((tail * (1f - t)) + (head * t));
        }

        return dest;
    }

    /// <summary>
    /// Join two PCM runs with a short linear crossfade so stream refills do not
    /// produce a speaker "power-plug" tick at the splice.
    /// </summary>
    public static short[] CrossfadeConcat(
        ReadOnlySpan<short> remaining,
        ReadOnlySpan<short> incoming,
        int crossfadeSamples,
        int maxTotalSamples)
    {
        if (remaining.IsEmpty)
        {
            return incoming.Length <= maxTotalSamples
                ? incoming.ToArray()
                : incoming[^maxTotalSamples..].ToArray();
        }

        if (incoming.IsEmpty)
        {
            return remaining.Length <= maxTotalSamples
                ? remaining.ToArray()
                : remaining[^maxTotalSamples..].ToArray();
        }

        var xf = Math.Min(crossfadeSamples, Math.Min(remaining.Length, incoming.Length));
        var total = remaining.Length + incoming.Length - xf;
        if (total > maxTotalSamples)
        {
            // Drop oldest remaining samples first.
            var drop = total - maxTotalSamples;
            if (drop >= remaining.Length)
            {
                var fromNew = drop - remaining.Length;
                return incoming[fromNew..].ToArray();
            }

            remaining = remaining[drop..];
            total = remaining.Length + incoming.Length - xf;
        }

        var dest = new short[total];
        var head = remaining.Length - xf;
        if (head > 0)
        {
            remaining[..head].CopyTo(dest);
        }

        for (var i = 0; i < xf; i++)
        {
            var t = (i + 1) / (float)(xf + 1);
            var a = remaining[head + i];
            var b = incoming[i];
            dest[head + i] = (short)((a * (1f - t)) + (b * t));
        }

        incoming[xf..].CopyTo(dest.AsSpan(head + xf));
        return dest;
    }

    /// <summary>
    /// Keep only the non-silent body of a buffer (with small pads). Rejects pure
    /// silence and near-constant DC that would pop on speakers.
    /// </summary>
    public static bool TryTrimActiveRegion(ReadOnlySpan<short> samples, out short[] trimmed)
    {
        trimmed = [];
        if (samples.IsEmpty)
        {
            return false;
        }

        const int threshold = 48; // ignore tiny dither / DC
        var first = -1;
        var last = -1;
        for (var i = 0; i < samples.Length; i++)
        {
            if (Math.Abs(samples[i]) >= threshold)
            {
                if (first < 0)
                {
                    first = i;
                }

                last = i;
            }
        }

        if (first < 0 || last < first)
        {
            return false;
        }

        // Pad slightly so envelope fades are not clipped mid-sample.
        first = Math.Max(0, first - 16);
        last = Math.Min(samples.Length - 1, last + 16);
        var len = last - first + 1;
        if (len < 32)
        {
            return false;
        }

        // Reject near-DC (constant) regions that sound like electrical ticks.
        var min = samples[first];
        var max = samples[first];
        for (var i = first; i <= last; i++)
        {
            var s = samples[i];
            if (s < min)
            {
                min = s;
            }

            if (s > max)
            {
                max = s;
            }
        }

        if (max - min < threshold * 2)
        {
            return false;
        }

        trimmed = samples.Slice(first, len).ToArray();
        return true;
    }

    public static bool TryArmFromGuestAddress(
        ICpuMemory memory,
        ulong address,
        out ArmedWaveform waveform,
        int depth = 0)
    {
        waveform = default;
        if (address <= 0x10000 || depth > 2)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[64];
        if (!memory.TryRead(address, head))
        {
            return false;
        }

        if (Ngs2VagDecoder.IsVag(head))
        {
            var declaredSize = (int)BinaryPrimitives.ReadUInt32BigEndian(head[0x0C..]);
            var totalBytes = Ngs2VagDecoder.VagHeaderSize + Math.Clamp(declaredSize, 0, MaxWaveformBytes);
            var raw = new byte[totalBytes];
            if (!memory.TryRead(address, raw) ||
                !Ngs2VagDecoder.TryDecode(raw, out var vag))
            {
                return false;
            }

            waveform = new ArmedWaveform(
                vag.Samples,
                vag.SampleRate,
                vag.LoopStart,
                vag.LoopEnd > 0 ? vag.LoopEnd : vag.Samples.Length,
                "VAGp");
            return true;
        }

        if (head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F')
        {
            var riffSize = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
            var total = Math.Clamp(8 + riffSize, 12, MaxWaveformBytes);
            var raw = new byte[total];
            if (!memory.TryRead(address, raw))
            {
                return false;
            }

            if (TryDecodeRiffWave(raw, out waveform))
            {
                return true;
            }
        }

        if (depth < 2)
        {
            var table = new byte[NestedPointerScanBytes];
            if (memory.TryRead(address, table))
            {
                for (var o = 0; o + 8 <= table.Length; o += 8)
                {
                    var nested = BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(o, 8));
                    if (nested > 0x10000 &&
                        nested != address &&
                        TryArmFromGuestAddress(memory, nested, out waveform, depth + 1))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryDecodePayload(
        byte[] raw,
        WaveformFormat format,
        int channels,
        int sampleRate,
        WaveformBlock block,
        out ArmedWaveform waveform)
    {
        waveform = default;

        // Self-describing containers win when present.
        if (Ngs2VagDecoder.IsVag(raw) && Ngs2VagDecoder.TryDecode(raw, out var vag))
        {
            waveform = new ArmedWaveform(
                vag.Samples,
                vag.SampleRate > 0 ? vag.SampleRate : sampleRate,
                vag.LoopStart,
                vag.LoopEnd > 0 ? vag.LoopEnd : vag.Samples.Length,
                "VAGp");
            return true;
        }

        if (raw.Length >= 12 &&
            raw[0] == (byte)'R' && raw[1] == (byte)'I' && raw[2] == (byte)'F' && raw[3] == (byte)'F' &&
            TryDecodeRiffWave(raw, out waveform))
        {
            return true;
        }

        // ATRAC9: either explicit type or configData holds the 4-byte AT9 config.
        if (format.WaveformType == WaveformTypeAtrac9 || format.ConfigData != 0)
        {
            var config = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(config, format.ConfigData);

            var sampleCount = block.NumSamples > 0
                ? (int)block.NumSamples
                : Math.Max(1, raw.Length); // decoder clamps via superframes
            if (TryDecodeAt9Raw(raw, config, sampleCount, encoderDelay: 0, out waveform))
            {
                return true;
            }
        }

        // Raw PCM16 (common when type is PCM / 0x12 and configData is zero).
        if (TryDecodeRawPcm16(raw, channels, sampleRate, block.NumSamples, out waveform))
        {
            return true;
        }

        // Raw float32 interleaved.
        if (format.WaveformType is WaveformTypePcmF32 ||
            (block.NumSamples > 0 && raw.Length >= block.NumSamples * channels * 4))
        {
            if (TryDecodeRawFloat32(raw, channels, sampleRate, block.NumSamples, out waveform))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryDecodeRawPcm16(
        ReadOnlySpan<byte> raw,
        int channels,
        int sampleRate,
        uint numSamplesHint,
        out ArmedWaveform waveform)
    {
        waveform = default;
        if (channels is <= 0 or > 8 || sampleRate <= 0 || raw.Length < channels * 2)
        {
            return false;
        }

        var frameBytes = channels * 2;
        var frames = raw.Length / frameBytes;
        if (numSamplesHint > 0)
        {
            frames = Math.Min(frames, (int)numSamplesHint);
        }

        if (frames <= 0)
        {
            return false;
        }

        var mono = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            var o = i * frameBytes;
            var sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += BinaryPrimitives.ReadInt16LittleEndian(raw.Slice(o + (c * 2), 2));
            }

            mono[i] = (short)(sum / channels);
        }

        if (!HasNonZeroSample(mono))
        {
            return false;
        }

        waveform = new ArmedWaveform(mono, sampleRate, -1, mono.Length, "PCM16");
        return true;
    }

    public static bool TryDecodeRawFloat32(
        ReadOnlySpan<byte> raw,
        int channels,
        int sampleRate,
        uint numSamplesHint,
        out ArmedWaveform waveform)
    {
        waveform = default;
        if (channels is <= 0 or > 8 || sampleRate <= 0 || raw.Length < channels * 4)
        {
            return false;
        }

        var frameBytes = channels * 4;
        var frames = raw.Length / frameBytes;
        if (numSamplesHint > 0)
        {
            frames = Math.Min(frames, (int)numSamplesHint);
        }

        if (frames <= 0)
        {
            return false;
        }

        var mono = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            var o = i * frameBytes;
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                sum += BinaryPrimitives.ReadSingleLittleEndian(raw.Slice(o + (c * 4), 4));
            }

            var avg = sum / channels;
            mono[i] = (short)Math.Clamp((int)(avg * 32767f), short.MinValue, short.MaxValue);
        }

        if (!HasNonZeroSample(mono))
        {
            return false;
        }

        waveform = new ArmedWaveform(mono, sampleRate, -1, mono.Length, "F32");
        return true;
    }

    private static bool HasNonZeroSample(ReadOnlySpan<short> samples)
    {
        var n = Math.Min(samples.Length, 4096);
        for (var i = 0; i < n; i++)
        {
            if (samples[i] != 0)
            {
                return true;
            }
        }

        // Long buffers: spot-check the tail too.
        if (samples.Length > n)
        {
            for (var i = samples.Length - Math.Min(256, samples.Length - n); i < samples.Length; i++)
            {
                if (samples[i] != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryDecodeRiffWave(byte[] file, out ArmedWaveform waveform)
    {
        waveform = default;
        if (file.Length < 12 ||
            Encoding.ASCII.GetString(file, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(file, 8, 4) != "WAVE")
        {
            return false;
        }

        byte[]? configData = null;
        var sampleCount = 0;
        var encoderDelay = 0;
        var dataOffset = -1;
        var dataSize = 0;
        var channels = 0;
        var sampleRate = 0;
        var bitsPerSample = 0;
        var formatTag = 0;
        var isAt9 = false;

        var pos = 12;
        while (pos + 8 <= file.Length)
        {
            var chunkId = Encoding.ASCII.GetString(file, pos, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos + 4, 4));
            var chunkStart = pos + 8;
            if (chunkSize < 0 || chunkStart + chunkSize > file.Length)
            {
                break;
            }

            switch (chunkId)
            {
                case "fmt ":
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(chunkStart, 2));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(chunkStart + 2, 2));
                    sampleRate = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(chunkStart + 4, 4));
                    bitsPerSample = chunkSize >= 16
                        ? BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(chunkStart + 14, 2))
                        : 0;
                    if (formatTag == 0xFFFE && chunkSize >= 40)
                    {
                        var sub = new Guid(file.AsSpan(chunkStart + 24, 16));
                        if (sub == Atrac9SubFormat)
                        {
                            isAt9 = true;
                            if (chunkSize >= 48)
                            {
                                configData = file.AsSpan(chunkStart + 44, 4).ToArray();
                            }
                        }
                    }

                    break;
                case "fact":
                    if (chunkSize >= 4)
                    {
                        sampleCount = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(chunkStart, 4));
                    }

                    if (chunkSize >= 12)
                    {
                        encoderDelay = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(chunkStart + 8, 4));
                    }

                    break;
                case "data":
                    dataOffset = chunkStart;
                    dataSize = chunkSize;
                    break;
            }

            pos = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0 || dataSize <= 0)
        {
            return false;
        }

        if (isAt9 && configData is not null && sampleCount > 0)
        {
            var slice = file.AsSpan(dataOffset, dataSize).ToArray();
            return TryDecodeAt9Raw(slice, configData, sampleCount, encoderDelay, out waveform);
        }

        if (formatTag is 1 or 0xFFFE && bitsPerSample == 16 && channels is > 0 and <= 8 && sampleRate > 0)
        {
            return TryDecodeRawPcm16(
                file.AsSpan(dataOffset, dataSize),
                channels,
                sampleRate,
                numSamplesHint: 0,
                out waveform);
        }

        return false;
    }

    private static bool TryDecodeAt9Raw(
        byte[] data,
        byte[] configData,
        int sampleCount,
        int encoderDelay,
        out ArmedWaveform waveform)
    {
        waveform = default;
        if (sampleCount <= 0 || configData.Length < 4)
        {
            return false;
        }

        try
        {
            var decoder = new Atrac9Decoder();
            decoder.Initialize(configData);
            var config = decoder.Config;
            if (config.SuperframeBytes <= 0 || config.ChannelCount <= 0)
            {
                return false;
            }

            var superframeCount = (sampleCount + encoderDelay + config.SuperframeSamples - 1) / config.SuperframeSamples;
            superframeCount = Math.Min(superframeCount, data.Length / config.SuperframeBytes);
            if (superframeCount <= 0)
            {
                return false;
            }

            var channels = config.ChannelCount;
            var pcmBuffer = new short[channels][];
            for (var i = 0; i < channels; i++)
            {
                pcmBuffer[i] = new short[config.SuperframeSamples];
            }

            var mono = new short[sampleCount];
            var superframe = new byte[config.SuperframeBytes];
            var decodedIndex = 0L;
            var written = 0;
            for (var f = 0; f < superframeCount && written < sampleCount; f++)
            {
                Buffer.BlockCopy(data, f * config.SuperframeBytes, superframe, 0, config.SuperframeBytes);
                decoder.Decode(superframe, pcmBuffer);
                for (var s = 0; s < config.SuperframeSamples && written < sampleCount; s++)
                {
                    if (decodedIndex >= encoderDelay)
                    {
                        var sum = 0;
                        for (var c = 0; c < channels; c++)
                        {
                            sum += pcmBuffer[c][s];
                        }

                        mono[written++] = (short)(sum / channels);
                    }

                    decodedIndex++;
                }
            }

            if (written <= 0 || !HasNonZeroSample(mono.AsSpan(0, written)))
            {
                return false;
            }

            if (written != mono.Length)
            {
                Array.Resize(ref mono, written);
            }

            waveform = new ArmedWaveform(mono, config.SampleRate, -1, mono.Length, "AT9");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
