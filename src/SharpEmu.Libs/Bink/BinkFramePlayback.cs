// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Bink;

internal interface IBinkFrameDecoder : IDisposable
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
internal sealed class BinkFramePlayback : IDisposable
{
    private const int BufferCount = 5;

    private readonly object _gate = new();
    private readonly IBinkFrameDecoder _decoder;
    private readonly Queue<byte[]> _freeBuffers = new();
    private readonly Queue<DecodedFrame> _decodedFrames = new();
    private readonly Thread _decoderThread;
    private byte[]? _currentFrame;
    private byte[]? _retiredFrame;
    private long _currentFrameIndex = -1;
    private long _nextDecodedFrameIndex;
    private long _playbackStartTimestamp;
    private bool _playbackClockStarted;
    private bool _decoderCompleted;
    private bool _stopRequested;
    private bool _finished;
    private int _disposed;

    internal BinkFramePlayback(IBinkFrameDecoder decoder)
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

    internal bool TryGetFrame(
        bool advanceClock,
        out byte[] pixels,
        out bool advanced)
    {
        lock (_gate)
        {
            pixels = [];
            advanced = false;
            if (_finished)
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
                _playbackClockStarted = true;
            }

            var elapsedSeconds = _playbackClockStarted
                ? Stopwatch.GetElapsedTime(_playbackStartTimestamp).TotalSeconds
                : 0;
            var targetFrameIndex = (long)Math.Floor(
                elapsedSeconds * FramesPerSecondNumerator / FramesPerSecondDenominator);
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
                    _decodedFrames.Enqueue(new DecodedFrame(
                        _nextDecodedFrameIndex++, destination));
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
