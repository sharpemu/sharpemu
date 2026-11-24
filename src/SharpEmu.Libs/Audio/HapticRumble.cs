// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Audio;

internal static class HapticRumble
{
    private const float NoiseFloor = 0.02f;
    private const float FullScale = 0.7f;
    private const float Curve = 0.75f;
    private const double ReleaseSeconds = 0.08;
    private const long MinimumIntervalMs = 16;
    private const long RefreshIntervalMs = 100;
    private const uint DurationMs = 250;
    private const int ChangeThreshold = 6;

    private static readonly float Gain = ReadGain();
    private static readonly object Gate = new();
    private static float _left;
    private static float _right;
    private static byte _sentLarge;
    private static byte _sentSmall;
    private static long _sentAt;

    internal static bool Enabled => Gain > 0f;

    internal static void MeasurePeaks(
        ReadOnlySpan<byte> source,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float gain,
        ref float left,
        ref float right)
    {
        var frameSize = channels * bytesPerSample;
        if (frameSize <= 0)
        {
            return;
        }

        frames = Math.Min(frames, source.Length / frameSize);
        for (var frame = 0; frame < frames; frame++)
        {
            var frameBytes = source.Slice(frame * frameSize, frameSize);
            var first = MathF.Abs(AudioOut2Exports.ReadNormalizedSample(frameBytes, 0, bytesPerSample, isFloat) * gain);
            var second = channels == 1
                ? first
                : MathF.Abs(AudioOut2Exports.ReadNormalizedSample(frameBytes, 1, bytesPerSample, isFloat) * gain);
            left = MathF.Max(left, first);
            right = MathF.Max(right, second);
        }
    }

    internal static void Update(float leftPeak, float rightPeak, double seconds)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            var decay = (float)Math.Exp(-Math.Max(0, seconds) / ReleaseSeconds);
            _left = MathF.Max(leftPeak, _left * decay);
            _right = MathF.Max(rightPeak, _right * decay);
            var large = ToMotor(_left);
            var small = ToMotor(_right);
            var now = Environment.TickCount64;
            if (!ShouldSend(large, small, now - _sentAt))
            {
                return;
            }

            _sentLarge = large;
            _sentSmall = small;
            _sentAt = now;
            HostPlatform.Current.Input.SetHapticRumble(large, small, DurationMs);
        }
    }

    internal static void Stop()
    {
        lock (Gate)
        {
            _left = 0f;
            _right = 0f;
            if ((_sentLarge | _sentSmall) == 0)
            {
                return;
            }

            _sentLarge = 0;
            _sentSmall = 0;
            _sentAt = Environment.TickCount64;
            HostPlatform.Current.Input.SetHapticRumble(0, 0, DurationMs);
        }
    }

    internal static byte ToMotor(float level)
    {
        var scaled = level * Gain;
        if (scaled <= NoiseFloor)
        {
            return 0;
        }

        var normalized = MathF.Min(1f, (scaled - NoiseFloor) / (FullScale - NoiseFloor));
        return (byte)MathF.Round(MathF.Pow(normalized, Curve) * 255f);
    }

    private static bool ShouldSend(byte large, byte small, long elapsedMs)
    {
        var wasRunning = (_sentLarge | _sentSmall) != 0;
        if ((large | small) == 0)
        {
            return wasRunning;
        }

        if (!wasRunning || elapsedMs >= RefreshIntervalMs)
        {
            return true;
        }

        return elapsedMs >= MinimumIntervalMs &&
               (Math.Abs(large - _sentLarge) >= ChangeThreshold || Math.Abs(small - _sentSmall) >= ChangeThreshold);
    }

    private static float ReadGain()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_HAPTIC_RUMBLE_GAIN");
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) && float.IsFinite(gain)
            ? Math.Max(0f, gain)
            : 0.5f;
    }
}
