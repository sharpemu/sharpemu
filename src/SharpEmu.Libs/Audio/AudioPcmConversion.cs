// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Converts guest AudioOut submissions (mono/stereo/7.1, s16 or float32) into the
/// interleaved stereo 16-bit PCM that host audio streams accept. Platform-neutral —
/// device specifics live behind IHostAudioStream.
/// </summary>
internal static class AudioPcmConversion
{
    /// <summary>Bytes per output frame: two 16-bit channels.</summary>
    public const int OutputFrameSize = 4;

    public static void ConvertToStereoPcm16(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float volume)
    {
        var sourceFrameSize = checked(channels * bytesPerSample);
        // Volume is constant for the whole submission, so clamp it once here
        // rather than per sample inside the loop (this runs on every real-time
        // audio buffer, hundreds of frames at a time).
        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceFrame = source.Slice(frame * sourceFrameSize, sourceFrameSize);
            var left = ReadSample(sourceFrame, 0, bytesPerSample, isFloat);
            var right = channels == 1
                ? left
                : ReadSample(sourceFrame, 1, bytesPerSample, isFloat);
            left = ApplyVolume(left, clampedVolume);
            right = ApplyVolume(right, clampedVolume);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(frame * OutputFrameSize)..], left);
            BinaryPrimitives.WriteInt16LittleEndian(destination[((frame * OutputFrameSize) + 2)..], right);
        }
    }

    private static short ReadSample(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (!isFloat)
        {
            return BinaryPrimitives.ReadInt16LittleEndian(sample);
        }

        var bits = BinaryPrimitives.ReadInt32LittleEndian(sample);
        return ConvertFloatSample(BitConverter.Int32BitsToSingle(bits));
    }

    private static short ConvertFloatSample(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        value = Math.Clamp(value, -1.0f, 1.0f);
        var scale = value < 0.0f ? 32768.0f : short.MaxValue;
        return unchecked((short)MathF.Round(value * scale));
    }

    // <paramref name="volume"/> is expected pre-clamped to [0, 1] by the caller.
    private static short ApplyVolume(short sample, float volume)
    {
        var scaled = MathF.Round(sample * volume);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
