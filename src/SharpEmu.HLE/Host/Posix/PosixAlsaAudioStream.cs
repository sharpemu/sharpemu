// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// ALSA-based playback for Linux. The PCM device is opened in blocking mode
/// with a device buffer sized to match the 32KB queue the other backends
/// keep, so snd_pcm_writei itself provides the backpressure pacing. The
/// "default" device routes through PulseAudio/PipeWire on desktops and to
/// the hardware on bare ALSA setups; SHARPEMU_ALSA_DEVICE overrides it.
/// </summary>
internal sealed unsafe class PosixAlsaAudioStream : IHostAudioStream
{
    private const int StreamPlayback = 0;
    private const int FormatS16LittleEndian = 2;
    private const int AccessReadWriteInterleaved = 3;
    private const int ErrorPipe = -32; // -EPIPE, underrun
    private const int ErrorStreamPipe = -86; // -ESTRPIPE, suspended

    private readonly object _gate = new();
    private nint _pcm;
    private bool _disposed;

    public PosixAlsaAudioStream(uint sampleRate, int maxQueuedPcmBytes = 32 * 1024)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("ALSA audio is only available on Linux.");
        }

        var device = Environment.GetEnvironmentVariable("SHARPEMU_ALSA_DEVICE");
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "default";
        }

        var status = snd_pcm_open(out _pcm, device, StreamPlayback, 0);
        if (status != 0)
        {
            throw new InvalidOperationException(
                $"snd_pcm_open(\"{device}\") failed: {DescribeError(status)}.");
        }

        // Match WinMM/CoreAudio soft queue depth: 32 KiB stereo PCM16 @ 48 kHz
        // is ~170 ms. AudioOut2 may request a deeper bed.
        var queuedBytes = Math.Max(maxQueuedPcmBytes, 4 * 1024);
        var latencyMicroseconds = (uint)Math.Clamp(
            (long)queuedBytes * 1_000_000L / Math.Max(sampleRate * 4u, 1u),
            20_000L,
            2_000_000L);

        status = snd_pcm_set_params(
            _pcm,
            FormatS16LittleEndian,
            AccessReadWriteInterleaved,
            2,
            sampleRate,
            1,
            latencyMicroseconds);
        if (status != 0)
        {
            _ = snd_pcm_close(_pcm);
            _pcm = 0;
            throw new InvalidOperationException(
                $"snd_pcm_set_params({sampleRate} Hz) failed: {DescribeError(status)}.");
        }
    }

    public bool Submit(ReadOnlySpan<byte> stereoPcm16)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            return WritePcm(stereoPcm16, (uint)(stereoPcm16.Length / 4));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pcm != 0)
            {
                _ = snd_pcm_drop(_pcm);
                _ = snd_pcm_close(_pcm);
                _pcm = 0;
            }
        }
    }

    private bool WritePcm(ReadOnlySpan<byte> pcm, uint frames)
    {
        var recovered = false;
        fixed (byte* data = pcm)
        {
            var offset = 0L;
            while (offset < frames)
            {
                var written = snd_pcm_writei(
                    _pcm,
                    data + (offset * 4),
                    (nuint)(frames - offset));
                if (written >= 0)
                {
                    offset += written;
                    continue;
                }

                // One recovery attempt per submit covers underruns (-EPIPE)
                // and suspend/resume (-ESTRPIPE); anything else, or a second
                // failure, drops the buffer rather than stalling the guest.
                if (recovered ||
                    (written != ErrorPipe && written != ErrorStreamPipe) ||
                    snd_pcm_recover(_pcm, (int)written, 1) != 0)
                {
                    return false;
                }

                recovered = true;
            }
        }

        return true;
    }

    private static string DescribeError(long status)
    {
        var message = Marshal.PtrToStringUTF8(snd_strerror((int)status));
        return $"{message ?? "unknown error"} ({status})";
    }

    private const string Alsa = "libasound.so.2";

    [DllImport(Alsa)]
    private static extern int snd_pcm_open(
        out nint pcm,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int stream,
        int mode);

    [DllImport(Alsa)]
    private static extern int snd_pcm_set_params(
        nint pcm,
        int format,
        int access,
        uint channels,
        uint rate,
        int softResample,
        uint latencyUs);

    [DllImport(Alsa)]
    private static extern long snd_pcm_writei(nint pcm, byte* buffer, nuint frames);

    [DllImport(Alsa)]
    private static extern int snd_pcm_recover(nint pcm, int error, int silent);

    [DllImport(Alsa)]
    private static extern int snd_pcm_drop(nint pcm);

    [DllImport(Alsa)]
    private static extern int snd_pcm_close(nint pcm);

    [DllImport(Alsa)]
    private static extern nint snd_strerror(int error);
}
