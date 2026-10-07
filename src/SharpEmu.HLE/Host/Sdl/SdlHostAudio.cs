// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.HLE.Host.Sdl;

internal sealed unsafe class SdlHostAudio : IHostPcmAudioOutput
{
    /// <summary>
    /// Cap for streams this class paces itself (AudioOut). Blocking the guest
    /// here is that path's only pacing, so the device settles at this depth —
    /// it is the playback latency, and the floor under it is how much jitter the
    /// stream can absorb before it runs dry.
    /// </summary>
    private static readonly int TargetQueuedMilliseconds =
        int.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_AUDIO_LATENCY_MS"),
            out var latencyMs) && latencyMs > 0
            ? latencyMs
            : 60;

    private const int MaximumWaitMilliseconds = 250;
    private static readonly object InitGate = new();
    private static bool _initialized;

    public string BackendName => "sdl3";

    /// <summary>
    /// Stereo PCM16 stream with a caller-chosen backpressure cap. Callers that
    /// pace the guest themselves pass a deeper cap so this class's backpressure
    /// does not fight their pacing.
    /// </summary>
    public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate, int maxQueuedPcmBytes = 32 * 1024)
        => OpenStream(
            sampleRate,
            channels: 2,
            HostPcmFormat.Signed16,
            maxQueuedPcmBytes > 0 ? maxQueuedPcmBytes : 32 * 1024);

    /// <summary>
    /// Guest-format stream for AudioOut, which has no queue model of its own:
    /// blocking here is that path's only pacing, so the device settles at
    /// TargetQueuedMilliseconds and that depth is the playback latency.
    /// </summary>
    public IHostAudioStream OpenPcmStream(uint sampleRate, int channels, HostPcmFormat format)
    {
        var bytesPerSample = format == HostPcmFormat.Float32 ? sizeof(float) : sizeof(short);
        var cap = checked((int)((long)sampleRate * channels * bytesPerSample *
                                TargetQueuedMilliseconds / 1_000));
        return OpenStream(sampleRate, channels, format, cap);
    }

    private static IHostAudioStream OpenStream(
        uint sampleRate,
        int channels,
        HostPcmFormat format,
        int maximumQueuedBytes)
    {
        if (sampleRate is < 8_000 or > 384_000 || channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                sampleRate is < 8_000 or > 384_000 ? nameof(sampleRate) : nameof(channels));
        }

        EnsureInitialized();
        return new AudioStream(sampleRate, channels, format, maximumQueuedBytes);
    }

    private static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized)
            {
                return;
            }

            if ((SDL_WasInit(SDL_InitFlags.SDL_INIT_AUDIO) & SDL_InitFlags.SDL_INIT_AUDIO) == 0 &&
                !SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
            {
                throw new InvalidOperationException($"SDL audio initialization failed: {GetError()}");
            }

            _initialized = true;
        }
    }

    private static string GetError()
    {
        var error = Unsafe_SDL_GetError();
        return error is null ? "unknown SDL error" : Marshal.PtrToStringUTF8((nint)error) ?? "unknown SDL error";
    }

    private static readonly bool _traceQueue = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_AUDIO_QUEUE"),
        "1",
        StringComparison.Ordinal);

    private static int _nextStreamId;

    private sealed class AudioStream : IHostAudioStream
    {
        private readonly object _gate = new();
        private readonly int _maximumQueuedBytes;
        private readonly int _bytesPerFrame;
        private readonly uint _sampleRate;
        private readonly int _streamId = Interlocked.Increment(ref _nextStreamId);
        private SDL_AudioStream* _stream;
        private bool _disposed;
        private long _totalSubmittedBytes;

        // Queue diagnostics for the current report window.
        private long _windowStart = Stopwatch.GetTimestamp();
        private long _submissions;
        private long _submittedBytes;
        private long _blockedTicks;
        private long _drops;
        private long _emptyObservations;
        private int _minQueuedBytes = int.MaxValue;
        private int _maxQueuedBytes;
        private long _queuedByteSum;

        public AudioStream(
            uint sampleRate,
            int channels,
            HostPcmFormat format,
            int maximumQueuedBytes)
        {
            var bytesPerSample = format == HostPcmFormat.Float32 ? sizeof(float) : sizeof(short);
            _bytesPerFrame = channels * bytesPerSample;
            _sampleRate = sampleRate;
            var spec = new SDL_AudioSpec
            {
                format = format == HostPcmFormat.Float32
                    ? SDL_AudioFormat.SDL_AUDIO_F32LE
                    : SDL_AudioFormat.SDL_AUDIO_S16LE,
                channels = checked((byte)channels),
                freq = checked((int)sampleRate),
            };

            _stream = SDL_OpenAudioDeviceStream(
                SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK,
                &spec,
                null,
                IntPtr.Zero);
            if (_stream is null)
            {
                throw new InvalidOperationException($"SDL audio stream creation failed: {GetError()}");
            }

            if (!SDL_ResumeAudioStreamDevice(_stream))
            {
                SDL_DestroyAudioStream(_stream);
                _stream = null;
                throw new InvalidOperationException($"SDL audio stream start failed: {GetError()}");
            }

            _maximumQueuedBytes = maximumQueuedBytes;
        }

        public int QueuedMilliseconds
        {
            get
            {
                lock (_gate)
                {
                    if (_disposed || _stream is null)
                    {
                        return -1;
                    }

                    var bytesPerSecond = (double)_bytesPerFrame * _sampleRate;
                    return bytesPerSecond <= 0
                        ? -1
                        : (int)(SDL_GetAudioStreamQueued(_stream) / bytesPerSecond * 1000.0);
                }
            }
        }

        public bool Submit(ReadOnlySpan<byte> pcm)
        {
            if (pcm.IsEmpty)
            {
                return true;
            }

            lock (_gate)
            {
                if (_disposed || _stream is null)
                {
                    return false;
                }

                var blockStart = Stopwatch.GetTimestamp();
                var deadline = blockStart +
                               (Stopwatch.Frequency * MaximumWaitMilliseconds / 1_000);
                int queued;
                var overrun = false;
                while ((queued = SDL_GetAudioStreamQueued(_stream)) > _maximumQueuedBytes)
                {
                    if (Stopwatch.GetTimestamp() >= deadline)
                    {
                        // Enqueue anyway rather than discarding the buffer. A gap in
                        // the stream is an audible click; the extra latency of one
                        // over-deep submission is not, and the queue recovers as soon
                        // as the device drains back under the cap.
                        overrun = true;
                        break;
                    }

                    // Sleep until the device should have drained to the cap rather than polling every
                    // millisecond: this wait paces every audio grain, and 1 ms polls cost the audio
                    // thread several wakeups per grain.
                    var bytesPerMillisecond = _bytesPerFrame * (double)_sampleRate / 1_000;
                    var drainMilliseconds = bytesPerMillisecond > 0
                        ? (queued - _maximumQueuedBytes) / bytesPerMillisecond
                        : 1;
                    var remainingMilliseconds = (deadline - Stopwatch.GetTimestamp()) * 1_000.0 / Stopwatch.Frequency;
                    Thread.Sleep((int)Math.Clamp(Math.Ceiling(Math.Min(drainMilliseconds, remainingMilliseconds)), 1, MaximumWaitMilliseconds));
                }

                RecordSubmission(queued, blockStart, dropped: overrun, bytes: pcm.Length);
                bool submitted;
                fixed (byte* data = pcm)
                {
                    submitted = SDL_PutAudioStreamData(_stream, (nint)data, pcm.Length);
                }

                if (submitted)
                {
                    // Everything handed over minus what the device still holds is
                    // what the player has actually heard.
                    _totalSubmittedBytes += pcm.Length;
                    var bytesPerSecond = (double)_bytesPerFrame * _sampleRate;
                    if (bytesPerSecond > 0)
                    {
                        GuestAudioClock.Report(
                            Math.Max(0, _totalSubmittedBytes - queued - pcm.Length) / bytesPerSecond);
                    }
                }

                return submitted;
            }
        }

        /// <summary>
        /// Samples the queue depth at the moment the guest was allowed to write.
        /// That depth is the playback latency the guest's audio is subject to, so
        /// it is the number to look at when the sound is late; an observed depth
        /// of zero is a genuine underrun, which is what a crackle sounds like.
        /// Caller holds <see cref="_gate"/>.
        /// </summary>
        private void RecordSubmission(int queuedBytes, long blockStart, bool dropped, int bytes)
        {
            if (!_traceQueue)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            _submissions++;
            _submittedBytes += bytes;
            _blockedTicks += now - blockStart;
            _queuedByteSum += queuedBytes;
            _minQueuedBytes = Math.Min(_minQueuedBytes, queuedBytes);
            _maxQueuedBytes = Math.Max(_maxQueuedBytes, queuedBytes);
            if (dropped)
            {
                _drops++;
            }

            if (queuedBytes == 0)
            {
                _emptyObservations++;
            }

            var elapsedTicks = now - _windowStart;
            if (elapsedTicks < Stopwatch.Frequency)
            {
                return;
            }

            _windowStart = now;
            var seconds = elapsedTicks / (double)Stopwatch.Frequency;
            var bytesPerSecond = (double)_bytesPerFrame * _sampleRate;
            Console.Error.WriteLine(
                $"[PERF][AUDIO] stream#{_streamId} {seconds:F1}s " +
                $"queued_ms min={ToMilliseconds(_minQueuedBytes, bytesPerSecond):F0} " +
                $"avg={ToMilliseconds((int)(_queuedByteSum / Math.Max(1, _submissions)), bytesPerSecond):F0} " +
                $"max={ToMilliseconds(_maxQueuedBytes, bytesPerSecond):F0} " +
                $"cap={ToMilliseconds(_maximumQueuedBytes, bytesPerSecond):F0} " +
                $"submits/s={_submissions / seconds:F0} " +
                $"fill={_submittedBytes / seconds / bytesPerSecond * 100.0:F0}% " +
                $"blocked={_blockedTicks * 100.0 / elapsedTicks:F0}% " +
                $"empty={_emptyObservations} drops={_drops}");

            _submissions = 0;
            _submittedBytes = 0;
            _blockedTicks = 0;
            _drops = 0;
            _emptyObservations = 0;
            _minQueuedBytes = int.MaxValue;
            _maxQueuedBytes = 0;
            _queuedByteSum = 0;
        }

        private static double ToMilliseconds(int bytes, double bytesPerSecond) =>
            bytesPerSecond <= 0 ? 0 : bytes / bytesPerSecond * 1000.0;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (_stream is not null)
                {
                    SDL_ClearAudioStream(_stream);
                    SDL_DestroyAudioStream(_stream);
                    _stream = null;
                }
            }
        }
    }
}
