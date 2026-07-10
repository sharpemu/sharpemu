// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Host playback sink for one sceAudioOut port. Implementations accept guest
/// sample buffers, convert them to stereo PCM16, and provide pacing through
/// queue backpressure (Submit blocks while the device queue is full).
/// </summary>
internal interface IHostAudioPort : IDisposable
{
    bool Submit(
        ReadOnlySpan<byte> source,
        uint frames,
        int channels,
        int bytesPerSample,
        bool isFloat);
}

internal static class AudioSampleConverter
{
    /// <summary>Converts an interleaved guest buffer to stereo PCM16.</summary>
    public static void ConvertToStereoPcm16(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat)
    {
        var sourceFrameSize = checked(channels * bytesPerSample);
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceFrame = source.Slice(frame * sourceFrameSize, sourceFrameSize);
            var left = ReadSample(sourceFrame, 0, bytesPerSample, isFloat);
            var right = channels == 1
                ? left
                : ReadSample(sourceFrame, 1, bytesPerSample, isFloat);
            BinaryPrimitives.WriteInt16LittleEndian(destination[(frame * 4)..], left);
            BinaryPrimitives.WriteInt16LittleEndian(destination[((frame * 4) + 2)..], right);
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
        var value = Math.Clamp(BitConverter.Int32BitsToSingle(bits), -1.0f, 1.0f);
        return checked((short)MathF.Round(value * short.MaxValue));
    }
}
