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

    public IHostAudioStream OpenPcmStream(uint sampleRate, int channels, HostPcmFormat format, int maxQueuedBytes) =>
        maxQueuedBytes > 0 ? OpenStream(sampleRate, channels, format, maxQueuedBytes) : OpenPcmStream(sampleRate, channels, format);

    /// <summary>
    /// The controller's output device: exactly one wired DualSense and exactly one four-channel playback device named for
    /// it ("DualSense", or "Wireless Controller" as Linux lists it). SDL ties no gamepad to its audio device, so anything
    /// else (no pad, two pads, two candidate devices) gives no stream rather than a guess. While the
    /// stream is open the pad's speaker plays the front pair; the back pair drives its actuators.
    /// </summary>
    public IHostAudioStream? TryOpenControllerStream(uint sampleRate, int maxQueuedBytes, out int channels, bool routeSpeaker = true)
    {
        channels = 0;
        EnsureInitialized();
        IHostAudioStream stream;
        string outputName;
        if (TryFindDualSense(SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRED, out var pad) &&
            TryFindControllerDevice(out var device, out var deviceName))
        {
            stream = new AudioStream(device, sampleRate, 4, HostPcmFormat.Float32, maxQueuedBytes, reportsClock: false, dropOldest: true);
            outputName = $"\"{deviceName}\"";
        }
        else if (TryFindDualSense(SDL_JoystickConnectionState.SDL_JOYSTICK_CONNECTION_WIRELESS, out pad) &&
                 SdlDualSenseBluetooth.TryOpen(pad, sampleRate, maxQueuedBytes, routeSpeaker) is { } bluetooth)
        {
            stream = bluetooth;
            outputName = "DualSense over Bluetooth";
        }
        else
        {
            return null;
        }

        var routed = routeSpeaker && SendControllerEffect(pad, ControllerAudioRoute(speaker: true));
        channels = 4;
        Console.Error.WriteLine(
            $"[LOADER][INFO] Controller audio: {outputName} (speaker {(routed ? "routed" : "not routed")})");
        return new ControllerAudioStream(stream, pad, routed);
    }

    private static bool TryFindDualSense(SDL_JoystickConnectionState connection, out SDL_JoystickID pad)
    {
        pad = default;
        var found = 0;
        SDL_LockJoysticks();
        try
        {
            using var gamepads = SDL_GetGamepads();
            for (var index = 0; gamepads is not null && index < gamepads.Count; index++)
            {
                var id = gamepads[index];
                var gamepad = SDL_GetGamepadFromID(id);
                if (gamepad is not null &&
                    SDL_GetGamepadTypeForID(id) == SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5 &&
                    SDL_GetGamepadConnectionState(gamepad) == connection)
                {
                    pad = id;
                    found++;
                }
            }
        }
        finally
        {
            SDL_UnlockJoysticks();
        }

        return found == 1;
    }

    private static bool TryFindControllerDevice(out SDL_AudioDeviceID device, out string? deviceName)
    {
        device = default;
        deviceName = null;
        var found = 0;
        using var devices = SDL_GetAudioPlaybackDevices();
        for (var index = 0; devices is not null && index < devices.Count; index++)
        {
            var name = SDL_GetAudioDeviceName(devices[index]);
            SDL_AudioSpec spec;
            int frames;
            if (name is not null &&
                (name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)) &&
                SDL_GetAudioDeviceFormat(devices[index], &spec, &frames) &&
                spec.channels == 4)
            {
                (device, deviceName) = (devices[index], name);
                found++;
            }
        }

        return found == 1;
    }

    // The pad's audio routing, in its output report: with the speaker, the right channel of the front pair plays on the
    // speaker and the headphone jack is muted (audio control and speaker volume valid, speaker volume 0x64, output path
    // 0x30, speaker preamp +6 dB: the values Linux's driver uses without headphones); without it, the front pair goes
    // back to the headphone jack. Plugged-in headphones are not detected.
    private static byte[] ControllerAudioRoute(bool speaker)
    {
        var report = new byte[38];
        report[0] = 0x80;
        if (speaker)
        {
            report[0] |= 0x20;
            report[1] = 0x80;
            report[5] = 0x64;
            report[7] = 0x30;
            report[37] = 0x02;
        }

        return report;
    }

    private static bool SendControllerEffect(SDL_JoystickID pad, byte[] report)
    {
        SDL_LockJoysticks();
        try
        {
            var gamepad = SDL_GetGamepadFromID(pad);
            if (gamepad is null)
            {
                return false;
            }

            fixed (byte* data = report)
            {
                return SDL_SendGamepadEffect(gamepad, (nint)data, report.Length);
            }
        }
        finally
        {
            SDL_UnlockJoysticks();
        }
    }

    private sealed class ControllerAudioStream(IHostAudioStream inner, SDL_JoystickID pad, bool speakerRouted) : IHostAudioStream
    {
        private int _disposed;

        public int QueuedMilliseconds => inner.QueuedMilliseconds;

        public bool Submit(ReadOnlySpan<byte> pcm) => inner.Submit(pcm);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            inner.Dispose();
            if (speakerRouted)
            {
                SendControllerEffect(pad, ControllerAudioRoute(speaker: false));
            }
        }
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
        return new AudioStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, sampleRate, channels, format, maximumQueuedBytes, reportsClock: true, dropOldest: false);
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
        private readonly bool _reportsClock;
        private readonly bool _dropOldest;
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
            SDL_AudioDeviceID device,
            uint sampleRate,
            int channels,
            HostPcmFormat format,
            int maximumQueuedBytes,
            bool reportsClock,
            bool dropOldest)
        {
            var bytesPerSample = format == HostPcmFormat.Float32 ? sizeof(float) : sizeof(short);
            _bytesPerFrame = channels * bytesPerSample;
            _sampleRate = sampleRate;
            _reportsClock = reportsClock;
            _dropOldest = dropOldest;
            var spec = new SDL_AudioSpec
            {
                format = format == HostPcmFormat.Float32
                    ? SDL_AudioFormat.SDL_AUDIO_F32LE
                    : SDL_AudioFormat.SDL_AUDIO_S16LE,
                channels = checked((byte)channels),
                freq = checked((int)sampleRate),
            };

            _stream = SDL_OpenAudioDeviceStream(
                device,
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
                    if (bytesPerSecond <= 0)
                    {
                        return -1;
                    }

                    // SDL reports a failed query as -1, which would otherwise truncate to an empty queue.
                    var queuedBytes = SDL_GetAudioStreamQueued(_stream);
                    return queuedBytes < 0 ? -1 : (int)(queuedBytes / bytesPerSecond * 1000.0);
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
                var overrun = false;
                var queued = _dropOldest ? DropOldest(pcm.Length, out overrun) : 0;
                while (!_dropOldest && (queued = SDL_GetAudioStreamQueued(_stream)) > _maximumQueuedBytes)
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
                    if (_reportsClock && bytesPerSecond > 0)
                    {
                        GuestAudioClock.Report(
                            Math.Max(0, _totalSubmittedBytes - queued - pcm.Length) / bytesPerSecond);
                    }
                }

                return submitted;
            }
        }

        /// <summary>
        /// For a stream the guest is not paced by: when the new audio would take the queue past its cap, the oldest
        /// queued audio goes, a chunk at a time, so the gap after a device stall stays near one block (clearing the
        /// whole queue leaves a long one) and the caller never waits. Caller holds <see cref="_gate"/>.
        /// </summary>
        private int DropOldest(int incomingBytes, out bool dropped)
        {
            var queued = Math.Max(0, SDL_GetAudioStreamQueued(_stream));
            dropped = queued + incomingBytes > _maximumQueuedBytes;
            if (!dropped)
            {
                return queued;
            }

            Span<byte> discard = stackalloc byte[4096];
            fixed (byte* buffer = discard)
            {
                for (var chunk = 0; chunk < 64 && queued + incomingBytes > _maximumQueuedBytes; chunk++)
                {
                    if (SDL_GetAudioStreamData(_stream, (nint)buffer, discard.Length) <= 0)
                    {
                        break;
                    }

                    queued = Math.Max(0, SDL_GetAudioStreamQueued(_stream));
                }
            }

            if (queued + incomingBytes > _maximumQueuedBytes)
            {
                SDL_ClearAudioStream(_stream);
                queued = 0;
            }

            return queued;
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
