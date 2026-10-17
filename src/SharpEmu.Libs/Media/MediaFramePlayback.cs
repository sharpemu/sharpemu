// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Media;

internal interface IMediaFrameDecoder : IDisposable
{
    uint Width { get; }

    uint Height { get; }

    uint FramesPerSecondNumerator { get; }

    uint FramesPerSecondDenominator { get; }

    bool TryDecodeNextFrame(Span<byte> destination);
}

/// <summary>
/// Keeps blocking codec work away from the Vulkan presentation thread and
/// releases decoded frames according to the movie time base.
/// </summary>
internal sealed class MediaFramePlayback : IDisposable
{
    private const int BufferCount = 5;

    private readonly object _gate = new();
    private readonly IMediaFrameDecoder _decoder;
    private readonly Queue<byte[]> _freeBuffers = new();
    private readonly Queue<DecodedFrame> _decodedFrames = new();
    private readonly Thread _decoderThread;
    private byte[]? _currentFrame;
    private byte[]? _retiredFrame;
    private long _currentFrameIndex = -1;
    private long _nextDecodedFrameIndex;
    private long _playbackStartTimestamp;
    private double _audioStartSeconds;
    private long _pausedWallTicks;
    private double _pausedAudioSeconds;
    private long _pauseStartTimestamp;
    private double _pauseAudioStartSeconds;
    private long _lastSkewTraceTimestamp;
    private bool _playbackClockStarted;
    private bool _paused;
    private bool _decoderCompleted;
    private bool _stopRequested;
    private bool _finished;
    private int _disposed;

    internal MediaFramePlayback(IMediaFrameDecoder decoder)
    {
        _decoder = decoder;
        Width = decoder.Width;
        Height = decoder.Height;
        FramesPerSecondNumerator = decoder.FramesPerSecondNumerator;
        FramesPerSecondDenominator = decoder.FramesPerSecondDenominator;

        var frameBytes = checked((int)((ulong)Width * Height * 4));
        for (var index = 0; index < BufferCount; index++)
        {
            _freeBuffers.Enqueue(GC.AllocateUninitializedArray<byte>(frameBytes));
        }

        _decoderThread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "SharpEmu Bink video decoder",
        };
        _decoderThread.Start();
    }

    internal uint Width { get; }

    internal uint Height { get; }

    internal uint FramesPerSecondNumerator { get; }

    internal uint FramesPerSecondDenominator { get; }

    internal bool IsFinished
    {
        get
        {
            lock (_gate)
            {
                return _finished;
            }
        }
    }

    /// <summary>
    /// Wall-clock seconds since the first frame was presented, and the index of
    /// the last frame shown. Playback is on its own time base when these agree
    /// with the movie's frame rate.
    /// </summary>
    internal (double Seconds, long FrameIndex) PlaybackProgress
    {
        get
        {
            lock (_gate)
            {
                return (
                    _playbackClockStarted
                        ? CurrentWallPlaybackSecondsLocked()
                        : 0,
                    _currentFrameIndex);
            }
        }
    }

    internal void Pause()
    {
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            _paused = true;
            if (_playbackClockStarted)
            {
                _pauseStartTimestamp = Stopwatch.GetTimestamp();
                _pauseAudioStartSeconds = GuestAudioClock.PlayedSeconds;
            }
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            if (!_paused)
            {
                return;
            }

            if (_playbackClockStarted)
            {
                var now = Stopwatch.GetTimestamp();
                _pausedWallTicks += Math.Max(0, now - _pauseStartTimestamp);
                _pausedAudioSeconds += Math.Max(
                    0,
                    GuestAudioClock.PlayedSeconds - _pauseAudioStartSeconds);
            }

            _paused = false;
            _pauseStartTimestamp = 0;
            _pauseAudioStartSeconds = 0;
        }
    }

    internal bool TryGetFrame(
        bool advanceClock,
        out byte[] pixels,
        out bool advanced)
    {
        lock (_gate)
        {
            pixels = [];
            advanced = false;
            if (_finished || _paused)
            {
                return false;
            }

            if (_currentFrame is null)
            {
                if (_decodedFrames.Count == 0)
                {
                    if (_decoderCompleted)
                    {
                        _finished = true;
                    }
                    return false;
                }

                var first = _decodedFrames.Dequeue();
                _currentFrame = first.Pixels;
                _currentFrameIndex = first.Index;
                advanced = true;
                Monitor.PulseAll(_gate);
            }

            if (advanceClock && !_playbackClockStarted)
            {
                _playbackStartTimestamp = Stopwatch.GetTimestamp();
                _audioStartSeconds = GuestAudioClock.PlayedSeconds;
                _pausedWallTicks = 0;
                _pausedAudioSeconds = 0;
                _playbackClockStarted = true;
            }

            var elapsedSeconds = CurrentPlaybackSecondsLocked();
            TraceClockSkewLocked();
            var targetFrameIndex = CurrentTargetFrameIndexLocked();
            DecodedFrame? replacement = null;
            while (_decodedFrames.Count > 0 &&
                   _decodedFrames.Peek().Index <= targetFrameIndex)
            {
                if (replacement is { } skipped)
                {
                    _freeBuffers.Enqueue(skipped.Pixels);
                }
                replacement = _decodedFrames.Dequeue();
            }

            if (replacement is { } next)
            {
                if (_retiredFrame is not null)
                {
                    _freeBuffers.Enqueue(_retiredFrame);
                }
                _retiredFrame = _currentFrame;
                _currentFrame = next.Pixels;
                _currentFrameIndex = next.Index;
                advanced = true;
                Monitor.PulseAll(_gate);
            }

            var frameDurationSeconds =
                (double)FramesPerSecondDenominator / FramesPerSecondNumerator;
            if (_playbackClockStarted &&
                _decoderCompleted &&
                _decodedFrames.Count == 0 &&
                elapsedSeconds >= (_currentFrameIndex + 1) * frameDurationSeconds)
            {
                _finished = true;
                return false;
            }

            pixels = _currentFrame;
            return true;
        }
    }

    /// <summary>
    /// Returns the newest decoded frame that does not exceed the frame that
    /// the guest received. Decode can run ahead, but it cannot make a frame
    /// visible before the AvPlayer client receives that frame.
    /// </summary>
    internal bool TryGetFrameAtOrBeforeIndex(
        long targetFrameIndex,
        out byte[] pixels,
        out long frameIndex,
        out bool advanced)
    {
        lock (_gate)
        {
            pixels = [];
            frameIndex = -1;
            advanced = false;
            if (_finished || _paused || targetFrameIndex < 0)
            {
                return false;
            }

            if (_currentFrame is null)
            {
                if (_decodedFrames.Count == 0)
                {
                    if (_decoderCompleted)
                    {
                        _finished = true;
                    }
                    return false;
                }

                var first = _decodedFrames.Dequeue();
                _currentFrame = first.Pixels;
                _currentFrameIndex = first.Index;
                advanced = true;
                Monitor.PulseAll(_gate);
            }

            if (_currentFrameIndex > targetFrameIndex)
            {
                return false;
            }

            DecodedFrame? replacement = null;
            while (_decodedFrames.Count > 0 &&
                   _decodedFrames.Peek().Index <= targetFrameIndex)
            {
                if (replacement is { } skipped)
                {
                    _freeBuffers.Enqueue(skipped.Pixels);
                }
                replacement = _decodedFrames.Dequeue();
            }

            if (replacement is { } next)
            {
                if (_retiredFrame is not null)
                {
                    _freeBuffers.Enqueue(_retiredFrame);
                }
                _retiredFrame = _currentFrame;
                _currentFrame = next.Pixels;
                _currentFrameIndex = next.Index;
                advanced = true;
                Monitor.PulseAll(_gate);
            }

            if (_decoderCompleted &&
                _decodedFrames.Count == 0 &&
                _currentFrameIndex < targetFrameIndex &&
                !advanced)
            {
                _finished = true;
                return false;
            }

            pixels = _currentFrame;
            frameIndex = _currentFrameIndex;
            return true;
        }
    }

    /// <summary>
    /// Time base for playback. A host-decoded movie runs on whatever clock it is
    /// given, but the audio that belongs to it comes from the guest, which does
    /// not advance at wall-clock rate on a slow frame. Following the audio keeps
    /// the two together; SHARPEMU_MOVIE_CLOCK=wall restores the old behaviour.
    /// </summary>
    private static readonly bool _followGuestAudioClock = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_MOVIE_CLOCK"),
        "wall",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Seconds of playback elapsed on the movie's time base. Falls back to wall
    /// clock whenever guest audio is not flowing: a movie whose audio never
    /// starts — or stops early — must still finish rather than hang on a clock
    /// that will never advance again.
    /// </summary>
    private double CurrentPlaybackSecondsLocked()
    {
        if (!_playbackClockStarted)
        {
            return 0;
        }

        var wallSeconds = CurrentWallPlaybackSecondsLocked();
        if (!_followGuestAudioClock || !GuestAudioClock.IsRunning)
        {
            return wallSeconds;
        }

        return Math.Clamp(CurrentAudioPlaybackSecondsLocked(), 0, wallSeconds);
    }

    private double CurrentAudioPlaybackSecondsLocked()
    {
        var pausedAudioSeconds = _pausedAudioSeconds;
        if (_paused)
        {
            pausedAudioSeconds += Math.Max(
                0,
                GuestAudioClock.PlayedSeconds - _pauseAudioStartSeconds);
        }

        return Math.Max(
            0,
            GuestAudioClock.PlayedSeconds - _audioStartSeconds - pausedAudioSeconds);
    }

    private double CurrentWallPlaybackSecondsLocked()
    {
        var now = Stopwatch.GetTimestamp();
        var pausedTicks = _pausedWallTicks;
        if (_paused)
        {
            pausedTicks += Math.Max(0, now - _pauseStartTimestamp);
        }

        return Math.Max(
            0,
            (now - _playbackStartTimestamp - pausedTicks) /
            (double)Stopwatch.Frequency);
    }

    private static readonly bool _traceClockSkew = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_MOVIE_SYNC"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Logs how far the movie's wall clock has drifted from the guest audio the
    /// movie is supposed to be in step with. A skew that is flat across playback
    /// is a late audio start; one that grows is a rate mismatch, and the two need
    /// different fixes. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void TraceClockSkewLocked()
    {
        if (!_traceClockSkew || !_playbackClockStarted)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastSkewTraceTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastSkewTraceTimestamp) < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastSkewTraceTimestamp = now;
        var wallSeconds = CurrentWallPlaybackSecondsLocked();
        var audioSeconds = CurrentAudioPlaybackSecondsLocked();
        var playbackSeconds = CurrentPlaybackSecondsLocked();
        Console.Error.WriteLine(
            $"[PERF][MOVIE] wall_s={wallSeconds:F2} audio_s={audioSeconds:F2} " +
            $"playback_s={playbackSeconds:F2} " +
            $"skew_s={wallSeconds - audioSeconds:F2} frame={_currentFrameIndex} " +
            $"audio_running={GuestAudioClock.IsRunning}");
    }

    /// <summary>
    /// The frame the movie's own time base says should be on screen right now.
    /// Returns -1 until the first frame is presented, so the queue prefills
    /// instead of instantly declaring everything late.
    /// </summary>
    private long CurrentTargetFrameIndexLocked() =>
        _playbackClockStarted
            ? (long)Math.Floor(
                CurrentPlaybackSecondsLocked() *
                FramesPerSecondNumerator / FramesPerSecondDenominator)
            : -1;

    private void DecodeLoop()
    {
        try
        {
            while (true)
            {
                byte[] destination;
                lock (_gate)
                {
                    while (!_stopRequested && _freeBuffers.Count == 0)
                    {
                        Monitor.Wait(_gate);
                    }
                    if (_stopRequested)
                    {
                        return;
                    }
                    destination = _freeBuffers.Dequeue();
                }

                if (!_decoder.TryDecodeNextFrame(destination))
                {
                    lock (_gate)
                    {
                        _freeBuffers.Enqueue(destination);
                        _decoderCompleted = true;
                        Monitor.PulseAll(_gate);
                    }
                    return;
                }

                lock (_gate)
                {
                    var frameIndex = _nextDecodedFrameIndex++;

                    // Frames are pulled once per guest flip, so a title running
                    // well under the movie's frame rate cannot drain a queue
                    // this shallow fast enough and the movie stretches past its
                    // real duration — audio finishes while the last picture sits
                    // on screen and the next movie starts late. Once the clock
                    // has passed a queued frame it can never be shown, so retire
                    // it in favour of this newer one. Only superseded frames are
                    // dropped, never the newest, so a decoder that cannot keep
                    // up still advances the picture instead of freezing it.
                    var targetFrameIndex = CurrentTargetFrameIndexLocked();
                    if (frameIndex <= targetFrameIndex)
                    {
                        while (_decodedFrames.Count > 0 &&
                               _decodedFrames.Peek().Index <= targetFrameIndex)
                        {
                            _freeBuffers.Enqueue(_decodedFrames.Dequeue().Pixels);
                        }
                    }

                    _decodedFrames.Enqueue(new DecodedFrame(frameIndex, destination));
                    Monitor.PulseAll(_gate);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or
                                             InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Bink decoder stopped: {exception.Message}");
            lock (_gate)
            {
                _decoderCompleted = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _stopRequested = true;
            Monitor.PulseAll(_gate);
        }
        if (Thread.CurrentThread != _decoderThread &&
            !_decoderThread.Join(TimeSpan.FromMilliseconds(100)))
        {
            _decoder.Dispose();
            _decoderThread.Join(TimeSpan.FromSeconds(2));
        }
        else
        {
            _decoder.Dispose();
        }
    }

    private readonly record struct DecodedFrame(long Index, byte[] Pixels);
}
