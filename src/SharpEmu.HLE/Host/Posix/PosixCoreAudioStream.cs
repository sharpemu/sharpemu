// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// AudioQueue-based playback for macOS. Buffers are enqueued as stereo PCM16
/// and returned by the queue's internal thread through the output callback;
/// Submit applies the same 32KB backpressure the WinMM backend uses so guest
/// pacing works identically.
/// </summary>
internal sealed unsafe class PosixCoreAudioStream : IHostAudioStream
{
    private const int MaximumQueuedPcmBytes = 32 * 1024;
    private const uint FormatLinearPcm = 0x6C70636D; // 'lpcm'
    private const uint FlagIsSignedInteger = 0x4;
    private const uint FlagIsPacked = 0x8;

    private readonly object _gate = new();
    private readonly AutoResetEvent _completion = new(false);
    private readonly Queue<nint> _freeBuffers = new();
    private GCHandle _selfHandle;
    private nint _queue;
    private int _queuedPcmBytes;
    private bool _started;
    private bool _disposed;

    public PosixCoreAudioStream(uint sampleRate)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("CoreAudio is only available on macOS.");
        }

        var format = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatId = FormatLinearPcm,
            FormatFlags = FlagIsSignedInteger | FlagIsPacked,
            BytesPerPacket = 4,
            FramesPerPacket = 1,
            BytesPerFrame = 4,
            ChannelsPerFrame = 2,
            BitsPerChannel = 16,
        };

        _selfHandle = GCHandle.Alloc(this);
        var status = AudioQueueNewOutput(
            &format,
            &OutputCallback,
            GCHandle.ToIntPtr(_selfHandle),
            0,
            0,
            0,
            out _queue);
        if (status != 0)
        {
            _selfHandle.Free();
            throw new InvalidOperationException($"AudioQueueNewOutput failed with OSStatus {status}.");
        }
    }

    public bool Submit(ReadOnlySpan<byte> stereoPcm16)
    {
        lock (_gate)
        {
            if (_disposed || _queue == 0)
            {
                return false;
            }

            var outputLength = stereoPcm16.Length;
            while (_queuedPcmBytes != 0 &&
                   _queuedPcmBytes + outputLength > MaximumQueuedPcmBytes)
            {
                Monitor.Exit(_gate);
                try
                {
                    // Dispose can free the event while this thread waits
                    // outside the gate; treat that like a timed-out wait.
                    if (!_completion.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        return false;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
                finally
                {
                    Monitor.Enter(_gate);
                }

                if (_disposed)
                {
                    return false;
                }
            }

            if (!TryTakeBuffer(outputLength, out var buffer))
            {
                return false;
            }

            var audioData = ((AudioQueueBuffer*)buffer)->AudioData;
            stereoPcm16.CopyTo(new Span<byte>(audioData, outputLength));

            ((AudioQueueBuffer*)buffer)->AudioDataByteSize = (uint)outputLength;
            if (AudioQueueEnqueueBuffer(_queue, buffer, 0, 0) != 0)
            {
                _freeBuffers.Enqueue(buffer);
                return false;
            }

            _queuedPcmBytes += outputLength;
            if (!_started)
            {
                if (AudioQueueStart(_queue, 0) != 0)
                {
                    // A queue that never starts never drains, so later
                    // submits would block on backpressure until their
                    // timeout. Tear the queue down and fail fast instead.
                    _ = AudioQueueDispose(_queue, true);
                    _queue = 0;
                    _queuedPcmBytes = 0;
                    _freeBuffers.Clear();
                    return false;
                }

                _started = true;
            }

            return true;
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
            if (_queue != 0)
            {
                // Synchronous dispose stops the queue, frees its buffers, and
                // guarantees no further callbacks reference this instance.
                _ = AudioQueueDispose(_queue, true);
                _queue = 0;
            }

            _freeBuffers.Clear();
            // Wake any submitter waiting on backpressure before the event
            // goes away; a late waiter observes ObjectDisposedException and
            // bails out in Submit.
            _completion.Set();
            _completion.Dispose();
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
        }
    }

    private bool TryTakeBuffer(int length, out nint buffer)
    {
        while (_freeBuffers.TryDequeue(out buffer))
        {
            if (((AudioQueueBuffer*)buffer)->AudioDataBytesCapacity >= (uint)length)
            {
                return true;
            }

            _ = AudioQueueFreeBuffer(_queue, buffer);
        }

        return AudioQueueAllocateBuffer(_queue, (uint)length, out buffer) == 0;
    }

    [UnmanagedCallersOnly]
    private static void OutputCallback(nint userData, nint queue, nint buffer)
    {
        if (GCHandle.FromIntPtr(userData).Target is not PosixCoreAudioStream port)
        {
            return;
        }

        lock (port._gate)
        {
            if (port._disposed)
            {
                return;
            }

            port._queuedPcmBytes -= checked((int)((AudioQueueBuffer*)buffer)->AudioDataByteSize);
            port._freeBuffers.Enqueue(buffer);
        }

        port._completion.Set();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint AudioDataBytesCapacity;
        public void* AudioData;
        public uint AudioDataByteSize;
        public nint UserData;
        public uint PacketDescriptionCapacity;
        public nint PacketDescriptions;
        public uint PacketDescriptionCount;
    }

    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(
        AudioStreamBasicDescription* format,
        delegate* unmanaged<nint, nint, nint, void> callback,
        nint userData,
        nint callbackRunLoop,
        nint runLoopMode,
        uint flags,
        out nint queue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(nint queue, uint bufferByteSize, out nint buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueFreeBuffer(nint queue, nint buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(nint queue, nint buffer, uint packetDescriptionCount, nint packetDescriptions);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(nint queue, nint startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(nint queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
}
