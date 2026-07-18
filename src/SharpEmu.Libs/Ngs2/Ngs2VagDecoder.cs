// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Ngs2;

// Clean-room PS-ADPCM ("VAG") decoder. NGS2 sampler voices point at waveforms
// wrapped in the classic Sony "VAGp" container: a 48-byte big-endian header
// followed by 16-byte ADPCM frames (2-byte predictor/shift + flags, then 14
// bytes = 28 nibbles = 28 samples). The predictor coefficient table and the
// nibble decode are the publicly documented PSX SPU ADPCM algorithm.
public static class Ngs2VagDecoder
{
    // Standard PS-ADPCM predictor filters (scaled by 1/64).
    private static readonly int[] Coeff0 = { 0, 60, 115, 98, 122 };
    private static readonly int[] Coeff1 = { 0, 0, -52, -55, -60 };

    public const int VagHeaderSize = 0x30;
    private const uint VagMagic = 0x56414770; // "VAGp"

    public readonly struct Waveform
    {
        public Waveform(short[] samples, int sampleRate, int loopStart, int loopEnd)
        {
            Samples = samples;
            SampleRate = sampleRate;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
        }

        public short[] Samples { get; }
        public int SampleRate { get; }
        public int LoopStart { get; } // -1 when the waveform does not loop
        public int LoopEnd { get; }
    }

    // True when the buffer begins with a recognizable "VAGp" container header.
    public static bool IsVag(ReadOnlySpan<byte> data) =>
        data.Length >= VagHeaderSize &&
        BinaryPrimitives.ReadUInt32BigEndian(data) == VagMagic;

    // Decode a full "VAGp" container into mono PCM16. Returns false when the
    // header is missing/short so callers can skip unsupported formats safely.
    public static bool TryDecode(ReadOnlySpan<byte> data, out Waveform waveform)
    {
        waveform = default;
        if (!IsVag(data))
        {
            return false;
        }

        // Header (big-endian): +0x0C dataSize, +0x10 sampleRate.
        var declaredSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]);
        var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
        if (sampleRate <= 0)
        {
            sampleRate = 48000;
        }

        var body = data[VagHeaderSize..];
        // Trust the declared payload size when it fits; otherwise decode what we
        // actually have (some tools pad or under-report).
        var available = body.Length - (body.Length % 16);
        var frameBytes = declaredSize > 0 && declaredSize <= available ? declaredSize - (declaredSize % 16) : available;
        if (frameBytes <= 0)
        {
            return false;
        }

        waveform = Decode(body[..frameBytes], sampleRate);
        return waveform.Samples.Length > 0;
    }

    // Decode raw 16-byte-framed PS-ADPCM (no container header) into PCM16 and
    // resolve loop points from the per-frame flag bytes.
    public static Waveform Decode(ReadOnlySpan<byte> frames, int sampleRate)
    {
        var frameCount = frames.Length / 16;
        var samples = new short[frameCount * 28];
        var loopStart = -1;
        var loopEnd = -1;

        var hist1 = 0;
        var hist2 = 0;
        var outIndex = 0;
        var ended = false;
        for (var frame = 0; frame < frameCount && !ended; frame++)
        {
            var offset = frame * 16;
            var header = frames[offset];
            var shift = header & 0x0F;
            var filter = (header >> 4) & 0x0F;
            if (filter > 4)
            {
                filter = 0;
            }

            // Per-frame loop marker (exact PS-ADPCM values, not bit masks):
            //   3 = loop start, 6 = loop end + jump back, 1/7 = one-shot end.
            var flags = frames[offset + 1];
            var blockStart = outIndex;
            if (flags == 0x03)
            {
                loopStart = blockStart;
            }

            var f0 = Coeff0[filter];
            var f1 = Coeff1[filter];
            for (var i = 0; i < 14; i++)
            {
                var d = frames[offset + 2 + i];
                for (var nibble = 0; nibble < 2; nibble++)
                {
                    var raw = nibble == 0 ? d & 0x0F : d >> 4;
                    // Sign-extend the 4-bit sample into the top nibble, then scale.
                    var s = (short)(raw << 12) >> shift;
                    var predicted = (hist1 * f0 + hist2 * f1) >> 6;
                    var sample = Math.Clamp(s + predicted, short.MinValue, short.MaxValue);
                    samples[outIndex++] = (short)sample;
                    hist2 = hist1;
                    hist1 = sample;
                }
            }

            if (flags == 0x06)
            {
                loopEnd = outIndex;
            }
            else if (flags == 0x01 || flags == 0x07)
            {
                ended = true;
            }
        }

        // Trim to the samples we actually decoded (a one-shot end marker can stop
        // us before the declared frame count).
        if (outIndex != samples.Length)
        {
            Array.Resize(ref samples, outIndex);
        }

        if (loopStart >= 0 && loopEnd <= loopStart)
        {
            loopEnd = outIndex;
        }

        return new Waveform(samples, sampleRate, loopStart, loopEnd);
    }
}
