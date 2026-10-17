// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

internal static class TrophySound
{
    public const double ImpactDelaySeconds = 1.0;

    private const int SampleRate = 48000;

    private static readonly Dictionary<int, byte[]> Rendered = [];

    public static bool Enabled { get; set; } = true;

    public static void Prepare()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() =>
            {
                for (var grade = 1; grade <= 4; grade++)
                {
                    Get(grade);
                }
            });
        }
    }

    public static void Play(int grade)
    {
        if (!Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        _ = Task.Run(() => PlayPcm(Get(grade)));
    }

    private static byte[] Get(int grade)
    {
        lock (Rendered)
        {
            if (!Rendered.TryGetValue(grade, out var pcm))
            {
                pcm = Render(Math.Clamp(grade, 1, 4));
                Rendered[grade] = pcm;
            }

            return pcm;
        }
    }

    internal static byte[] Render(int grade)
    {
        var tail = grade switch { 1 => 3.2, 2 => 2.6, _ => 2.2 };
        var length = (int)((ImpactDelaySeconds + tail) * SampleRate);
        var left = new double[length];
        var right = new double[length];
        var random = new Random(1234 + grade);
        var impact = (int)(ImpactDelaySeconds * SampleRate);

        AddWhoosh(left, right, impact, random, grade == 1 ? 0.22 : 0.16);
        AddThump(left, right, impact, grade == 1 ? 0.9 : grade == 2 ? 0.6 : 0.45);

        double[] chord = grade switch
        {
            1 => [164.81, 246.94, 329.63, 493.88, 659.25, 830.61, 987.77, 1318.51],
            2 => [329.63, 493.88, 659.25, 830.61, 987.77, 1318.51],
            3 => [329.63, 493.88, 659.25, 830.61, 987.77],
            _ => [329.63, 493.88, 659.25, 830.61],
        };
        for (var i = 0; i < chord.Length; i++)
        {
            var start = impact + (int)(i * 0.016 * SampleRate);
            var gain = 0.34 * Math.Pow(0.86, i);
            var decay = 1.6 - i * 0.12;
            var pan = chord.Length == 1 ? 0 : -0.45 + 0.9 * i / (chord.Length - 1);
            AddMallet(left, right, start, chord[i], gain, decay, pan);
        }

        var sparkles = grade switch { 1 => 18, 2 => 10, 3 => 5, _ => 0 };
        double[] pentatonic = [1975.53, 2217.46, 2489.02, 2959.96, 3322.44, 3951.07];
        for (var i = 0; i < sparkles; i++)
        {
            var start = impact + (int)((0.05 + random.NextDouble() * (grade == 1 ? 1.1 : 0.6)) * SampleRate);
            AddMallet(left, right, start, pentatonic[random.Next(pentatonic.Length)], 0.05 + random.NextDouble() * 0.04, 6.5,
                random.NextDouble() * 1.6 - 0.8);
        }

        if (grade == 1)
        {
            double[] climb = [1318.51, 1661.22, 1975.53, 2637.02];
            for (var i = 0; i < climb.Length; i++)
            {
                AddMallet(left, right, impact + (int)((0.38 + i * 0.075) * SampleRate), climb[i], 0.12, 3.0, -0.3 + i * 0.2);
            }
        }

        Reverb(left, right, grade == 1 ? 0.34 : 0.26);
        return ToPcm(left, right);
    }

    private static void AddWhoosh(double[] left, double[] right, int impact, Random random, double gain)
    {
        var start = (int)(impact * 0.08);
        var state = 0.0;
        for (var i = start; i < impact; i++)
        {
            var progress = (i - start) / (double)(impact - start);
            var cutoff = 300 * Math.Pow(30, progress);
            var alpha = 1 - Math.Exp(-2 * Math.PI * cutoff / SampleRate);
            state += alpha * (random.NextDouble() * 2 - 1 - state);
            var envelope = progress * progress * Math.Min(1, (1 - progress) * 14);
            var sample = state * envelope * gain * 2.2;
            var pan = -0.7 + 1.4 * progress;
            left[i] += sample * (1 - pan) / 2;
            right[i] += sample * (1 + pan) / 2;
        }
    }

    private static void AddThump(double[] left, double[] right, int start, double gain)
    {
        var phase = 0.0;
        for (var i = start; i < Math.Min(left.Length, start + SampleRate / 2); i++)
        {
            var t = (i - start) / (double)SampleRate;
            phase += 2 * Math.PI * (55 + 75 * Math.Exp(-t * 28)) / SampleRate;
            var sample = gain * Math.Min(1, t / 0.002) * Math.Exp(-t * 11) * Math.Sin(phase);
            left[i] += sample;
            right[i] += sample;
        }
    }

    private static void AddMallet(double[] left, double[] right, int start, double frequency, double gain, double decay, double pan)
    {
        ReadOnlySpan<(double Ratio, double Amplitude, double Damping)> partials = [(1.0, 1.0, 1.0), (2.0, 0.28, 2.2), (3.0, 0.08, 3.5), (4.0, 0.03, 5.0)];
        const double detune = 0.0018;
        for (var i = start; i < left.Length; i++)
        {
            var t = (i - start) / (double)SampleRate;
            var envelope = Math.Min(1, t / 0.003);
            if (t > 0.02 && Math.Exp(-t * decay) < 0.0005)
            {
                break;
            }

            double l = 0, r = 0;
            foreach (var (ratio, amplitude, damping) in partials)
            {
                var level = amplitude * Math.Exp(-t * decay * damping);
                var w = 2 * Math.PI * frequency * ratio * t;
                l += level * Math.Sin(w * (1 - detune));
                r += level * Math.Sin(w * (1 + detune));
            }

            left[i] += gain * envelope * l * (1 - pan * 0.6);
            right[i] += gain * envelope * r * (1 + pan * 0.6);
        }
    }

    private static void Reverb(double[] left, double[] right, double wet)
    {
        int[] combs = [1557, 1617, 1491, 1422];
        int[] allpasses = [556, 441];
        var wetLeft = Room(left, combs, allpasses, 0);
        var wetRight = Room(right, combs, allpasses, 23);
        for (var i = 0; i < left.Length; i++)
        {
            left[i] += wet * wetLeft[i];
            right[i] += wet * wetRight[i];
        }
    }

    private static double[] Room(double[] input, int[] combs, int[] allpasses, int spread)
    {
        var output = new double[input.Length];
        foreach (var length in combs)
        {
            var buffer = new double[length + spread];
            var index = 0;
            var filter = 0.0;
            for (var i = 0; i < input.Length; i++)
            {
                var delayed = buffer[index];
                filter = delayed * 0.75 + filter * 0.25;
                buffer[index] = input[i] * 0.25 + filter * 0.82;
                output[i] += delayed;
                index = (index + 1) % buffer.Length;
            }
        }

        foreach (var length in allpasses)
        {
            var buffer = new double[length + spread];
            var index = 0;
            for (var i = 0; i < output.Length; i++)
            {
                var delayed = buffer[index];
                var value = output[i] + delayed * 0.5;
                buffer[index] = value;
                output[i] = delayed - value * 0.5;
                index = (index + 1) % buffer.Length;
            }
        }

        return output;
    }

    private static byte[] ToPcm(double[] left, double[] right)
    {
        var peak = 1e-9;
        for (var i = 0; i < left.Length; i++)
        {
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        }

        var scale = 0.62 / peak;
        var pcm = new byte[left.Length * 4];
        for (var i = 0; i < left.Length; i++)
        {
            var fade = Math.Min(1.0, (left.Length - i) / (0.08 * SampleRate));
            WriteSample(pcm, i * 4, Math.Tanh(left[i] * scale * 1.15) * fade);
            WriteSample(pcm, i * 4 + 2, Math.Tanh(right[i] * scale * 1.15) * fade);
        }

        return pcm;
    }

    private static void WriteSample(byte[] pcm, int offset, double value)
    {
        var sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
        pcm[offset] = (byte)sample;
        pcm[offset + 1] = (byte)(sample >> 8);
    }

    private static void PlayPcm(byte[] pcm)
    {
        var format = new WaveFormat
        {
            FormatTag = 1,
            Channels = 2,
            SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = SampleRate * 4,
            BlockAlign = 4,
            BitsPerSample = 16,
        };
        if (waveOutOpen(out var device, WaveMapper, ref format, 0, 0, 0) != 0)
        {
            return;
        }

        var data = Marshal.AllocHGlobal(pcm.Length);
        var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        try
        {
            Marshal.Copy(pcm, 0, data, pcm.Length);
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)pcm.Length }, header, false);
            var size = (uint)Marshal.SizeOf<WaveHeader>();
            if (waveOutPrepareHeader(device, header, size) != 0)
            {
                return;
            }

            if (waveOutWrite(device, header, size) == 0)
            {
                var deadline = Environment.TickCount64 + pcm.Length * 1000L / (SampleRate * 4) + 2000;
                while ((Marshal.PtrToStructure<WaveHeader>(header).Flags & WaveHeaderDone) == 0 &&
                       Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(50);
                }
            }

            _ = waveOutReset(device);
            _ = waveOutUnprepareHeader(device, header, size);
        }
        finally
        {
            _ = waveOutClose(device);
            Marshal.FreeHGlobal(header);
            Marshal.FreeHGlobal(data);
        }
    }

    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WaveHeaderDone = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutOpen(out nint device, uint deviceId, ref WaveFormat format, nint callback, nint instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutWrite(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutReset(nint device);

    [DllImport("winmm.dll")]
    private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);

    [DllImport("winmm.dll")]
    private static extern uint waveOutClose(nint device);
}
