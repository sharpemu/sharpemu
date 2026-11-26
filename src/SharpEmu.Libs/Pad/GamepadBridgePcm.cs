// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The sandboxed child's controller audio stream over the bridge: the PCM goes into one slot's ring and the launcher
/// plays it on the Bluetooth DualSense. It never blocks (a full ring drops its oldest audio, like the in-process
/// controller stream).
/// </summary>
internal sealed class GamepadBridgePcmStream : IHostAudioStream
{
    private readonly GamepadBridgeControllerLink _link;
    private readonly GamepadBridgeMemory _memory;
    private readonly int _slot;
    private readonly double _bytesPerMillisecond;
    private int _disposed;

    public GamepadBridgePcmStream(
        GamepadBridgeControllerLink link, GamepadBridgeMemory memory, int slot, uint sampleRate)
    {
        _link = link;
        _memory = memory;
        _slot = slot;
        _bytesPerMillisecond = (double)GamepadBridgeMemory.PcmFrameBytes * sampleRate / 1000.0;
    }

    public int QueuedMilliseconds =>
        Volatile.Read(ref _disposed) != 0 || _link.IsClosed ? -1 : (int)(_memory.PcmQueuedBytes(_slot) / _bytesPerMillisecond);

    public bool Submit(ReadOnlySpan<byte> pcm)
    {
        if (Volatile.Read(ref _disposed) != 0 || _link.IsClosed)
        {
            return false;
        }

        _memory.WritePcm(_slot, pcm);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _link.ReleaseSlot(_slot);
        }
    }
}

/// <summary>
/// The launcher's end of one controller audio slot: follows the child's open and close of its stream and feeds what the
/// child writes to a stream the launcher opens on the Bluetooth DualSense. Poll it from the bridge thread.
/// </summary>
internal sealed class GamepadBridgePcmPump : IDisposable
{
    private const int ReopenDelayMilliseconds = 1000;
    private const int ScratchBytes = 16 * 1024;

    private readonly GamepadBridgeMemory _memory;
    private readonly int _slot;
    private readonly Func<uint, int, bool, IHostAudioStream?> _open;
    private readonly Func<long> _clock;
    private readonly byte[] _scratch = new byte[ScratchBytes];
    private PcmControl _control;
    private uint _seenGeneration;
    private IHostAudioStream? _stream;
    private ulong _readIndex;
    private long _retryAt;
    private bool _openFailureLogged;

    public GamepadBridgePcmPump(
        GamepadBridgeMemory memory,
        int slot,
        Func<uint, int, bool, IHostAudioStream?> open,
        Func<long>? clock = null)
    {
        _memory = memory;
        _slot = slot;
        _open = open;
        _clock = clock ?? (() => Stopwatch.GetTimestamp());
        // A stream the child opened before the pump existed is picked up on the first poll.
        _seenGeneration = unchecked(_memory.ReadPcmControl(slot).Generation - 1);
    }

    /// <summary>True while the launcher has a stream open on the pad for this slot.</summary>
    public bool IsPlaying => _stream is not null;

    /// <summary>Forwards what the child wrote; returns the bytes handed to the Bluetooth stream.</summary>
    public int Poll()
    {
        var control = _memory.ReadPcmControl(_slot);
        if (control.Generation != _seenGeneration)
        {
            _seenGeneration = control.Generation;
            _control = control;
            Release();
            // Audio from before the stream opened is an earlier stream's; this one's starts where it said.
            _readIndex = control.StartPosition;
            _memory.PublishPcmConsumed(_slot, _readIndex);
            _retryAt = 0;
            _openFailureLogged = false;
        }

        if (!_control.Active)
        {
            return 0;
        }

        if (_stream is null && _clock() >= _retryAt)
        {
            _stream = _open(_control.SampleRate, _control.MaxQueuedBytes, _control.Speaker);
            if (_stream is null)
            {
                _retryAt = _clock() + Stopwatch.Frequency * ReopenDelayMilliseconds / 1000;
                if (!_openFailureLogged)
                {
                    _openFailureLogged = true;
                    Console.Error.WriteLine(
                        "[LOADER][WARN] gamepad bridge: the Bluetooth DualSense stream could not be opened");
                }
            }
        }

        if (_stream is null)
        {
            // Nothing plays: do not let the audio pile up behind the stream that will open later.
            _readIndex = _memory.PcmWritten(_slot);
            _memory.PublishPcmConsumed(_slot, _readIndex);
            return 0;
        }

        var total = 0;
        int read;
        while ((read = _memory.ReadPcm(_slot, ref _readIndex, _scratch)) > 0)
        {
            _stream.Submit(_scratch.AsSpan(0, read));
            total += read;
            if (read < _scratch.Length)
            {
                break;
            }
        }

        _memory.PublishPcmConsumed(_slot, _readIndex);
        return total;
    }

    /// <summary>Closes the launcher's stream (the pad went away); the child's stream stays open and the next poll reopens it.</summary>
    public void Release()
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
        _retryAt = 0;
    }

    public void Dispose() => Release();
}
