// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using Silk.NET.OpenAL;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Cross-platform host audio sink built on OpenAL Soft. This mirrors the
/// streaming approach used by touchHLE's OpenAL wrapper: a single source with
/// a rolling queue of small PCM buffers, reclaiming buffers as the source
/// consumes them. Unlike <see cref="WinMmAudioPort"/> (which is Windows-only),
/// this works on Windows, Linux, and macOS.
/// </summary>
internal sealed unsafe class OpenAlAudioPort : IAudioBackend
{
    // Keep roughly the same amount of audio queued as the WinMM backend so
    // latency and back-pressure behaviour stay comparable across platforms.
    private const int MaximumQueuedBuffers = 8;
    private const int OutputChannels = 2;
    private const int OutputBytesPerFrame = OutputChannels * sizeof(short);

    private readonly object _gate = new();
    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly uint _source;
    private readonly int _sampleRate;
    private bool _disposed;

    public OpenAlAudioPort(uint sampleRate)
    {
        _sampleRate = checked((int)sampleRate);
        _alc = ALContext.GetApi(soft: true);
        _al = AL.GetApi(soft: true);

        _device = _alc.OpenDevice(string.Empty);
        if (_device is null)
        {
            _alc.Dispose();
            _al.Dispose();
            throw new InvalidOperationException("alcOpenDevice failed (no OpenAL device).");
        }

        _context = _alc.CreateContext(_device, null);
        if (_context is null)
        {
            _alc.CloseDevice(_device);
            _alc.Dispose();
            _al.Dispose();
            throw new InvalidOperationException("alcCreateContext failed.");
        }

        if (!_alc.MakeContextCurrent(_context))
        {
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
            _alc.Dispose();
            _al.Dispose();
            throw new InvalidOperationException("alcMakeContextCurrent failed.");
        }

        _source = _al.GenSource();
        if (_al.GetError() != AudioError.NoError)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
            _alc.Dispose();
            _al.Dispose();
            throw new InvalidOperationException("alGenSources failed.");
        }
    }

    public bool Submit(
        ReadOnlySpan<byte> source,
        uint frames,
        int channels,
        int bytesPerSample,
        bool isFloat)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _alc.MakeContextCurrent(_context);

            var frameCount = checked((int)frames);
            var outputLength = checked(frameCount * OutputBytesPerFrame);

            // Back-pressure: don't run arbitrarily far ahead of playback.
            // Wait for the source to release queued buffers, then reclaim them.
            var spinWaits = 0;
            while (ReclaimProcessedBuffers() >= MaximumQueuedBuffers)
            {
                if (++spinWaits > 1000)
                {
                    return false;
                }

                Thread.Sleep(1);
            }

            var rented = ArrayPool<byte>.Shared.Rent(outputLength);
            try
            {
                var output = rented.AsSpan(0, outputLength);
                ConvertToStereoPcm16(source, output, frameCount, channels, bytesPerSample, isFloat);
                return QueueBuffer(output);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private bool QueueBuffer(ReadOnlySpan<byte> pcm)
    {
        var buffer = _al.GenBuffer();
        if (_al.GetError() != AudioError.NoError)
        {
            return false;
        }

        fixed (byte* data = pcm)
        {
            _al.BufferData(buffer, BufferFormat.Stereo16, data, pcm.Length, _sampleRate);
        }

        if (_al.GetError() != AudioError.NoError)
        {
            _al.DeleteBuffer(buffer);
            return false;
        }

        _al.SourceQueueBuffers(_source, new[] { buffer });
        if (_al.GetError() != AudioError.NoError)
        {
            _al.DeleteBuffer(buffer);
            return false;
        }

        // (Re)start playback if the source drained and stopped.
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
        if ((SourceState)state != SourceState.Playing)
        {
            _al.SourcePlay(_source);
        }

        return _al.GetError() == AudioError.NoError;
    }

    /// <summary>
    /// Unqueues and deletes buffers the source has finished with, returning the
    /// number of buffers still queued afterwards.
    /// </summary>
    private int ReclaimProcessedBuffers()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
        while (processed > 0)
        {
            uint buffer = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buffer);
            _al.DeleteBuffer(buffer);
            processed--;
        }

        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out var queued);
        return queued;
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

            _alc.MakeContextCurrent(_context);
            _al.SourceStop(_source);
            ReclaimProcessedBuffers();
            _al.DeleteSource(_source);

            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
            _al.Dispose();
            _alc.Dispose();
        }
    }

    private static void ConvertToStereoPcm16(
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
