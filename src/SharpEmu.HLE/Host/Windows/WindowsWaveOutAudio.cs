// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

internal sealed partial class WindowsWaveOutAudio : IHostAudioOutput
{
    public string BackendName => "winmm";

    public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate) => new WaveOutStream(sampleRate);

    private sealed partial class WaveOutStream : IHostAudioStream
    {
        private const uint WaveMapper = uint.MaxValue;
        private const uint CallbackEvent = 0x0005_0000;
        private const ushort WaveFormatPcm = 1;
        private const uint WaveHeaderDone = 0x0000_0001;
        private const int MaximumQueuedPcmBytes = 32 * 1024;

        private readonly object _gate = new();
        private readonly AutoResetEvent _completion = new(false);
        private readonly Queue<NativeBuffer> _buffers = new();
        private IntPtr _device;
        private int _queuedPcmBytes;
        private bool _disposed;

        public WaveOutStream(uint sampleRate)
        {
            var format = new WaveFormat
            {
                FormatTag = WaveFormatPcm,
                Channels = 2,
                SamplesPerSecond = sampleRate,
                AverageBytesPerSecond = checked(sampleRate * 4),
                BlockAlign = 4,
                BitsPerSample = 16,
                ExtraSize = 0,
            };
            var result = WaveOutOpen(
                out _device,
                WaveMapper,
                ref format,
                _completion.SafeWaitHandle.DangerousGetHandle(),
                IntPtr.Zero,
                CallbackEvent);
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutOpen failed with MMRESULT {result}.");
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

                ReapCompletedBuffers();
                while (_queuedPcmBytes != 0 &&
                       _queuedPcmBytes + stereoPcm16.Length > MaximumQueuedPcmBytes)
                {
                    if (!_completion.WaitOne(TimeSpan.FromSeconds(1)))
                    {
                        return false;
                    }

                    ReapCompletedBuffers();
                }

                return QueueBuffer(stereoPcm16);
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
                if (_device != IntPtr.Zero)
                {
                    WaveOutReset(_device);
                    while (_buffers.TryDequeue(out var buffer))
                    {
                        ReleaseBuffer(buffer);
                    }

                    WaveOutClose(_device);
                    _device = IntPtr.Zero;
                }

                _completion.Dispose();
            }
        }

        private bool QueueBuffer(ReadOnlySpan<byte> data)
        {
            var dataAddress = Marshal.AllocHGlobal(data.Length);
            var headerAddress = IntPtr.Zero;
            try
            {
                unsafe
                {
                    data.CopyTo(new Span<byte>((void*)dataAddress, data.Length));
                }

                var header = new WaveHeader
                {
                    Data = dataAddress,
                    BufferLength = checked((uint)data.Length),
                };
                headerAddress = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(header, headerAddress, false);

                var result = WaveOutPrepareHeader(
                    _device,
                    headerAddress,
                    checked((uint)Marshal.SizeOf<WaveHeader>()));
                if (result != 0)
                {
                    return false;
                }

                result = WaveOutWrite(
                    _device,
                    headerAddress,
                    checked((uint)Marshal.SizeOf<WaveHeader>()));
                if (result != 0)
                {
                    WaveOutUnprepareHeader(
                        _device,
                        headerAddress,
                        checked((uint)Marshal.SizeOf<WaveHeader>()));
                    return false;
                }

                _buffers.Enqueue(new NativeBuffer(dataAddress, headerAddress, data.Length));
                _queuedPcmBytes += data.Length;
                dataAddress = IntPtr.Zero;
                headerAddress = IntPtr.Zero;
                return true;
            }
            finally
            {
                if (headerAddress != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(headerAddress);
                }

                if (dataAddress != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataAddress);
                }
            }
        }

        private void ReapCompletedBuffers()
        {
            while (_buffers.TryPeek(out var buffer))
            {
                var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
                if ((header.Flags & WaveHeaderDone) == 0)
                {
                    return;
                }

                _buffers.Dequeue();
                ReleaseBuffer(buffer);
            }
        }

        private void ReleaseBuffer(NativeBuffer buffer)
        {
            WaveOutUnprepareHeader(
                _device,
                buffer.Header,
                checked((uint)Marshal.SizeOf<WaveHeader>()));
            _queuedPcmBytes -= buffer.Length;
            Marshal.FreeHGlobal(buffer.Header);
            Marshal.FreeHGlobal(buffer.Data);
        }

        private readonly record struct NativeBuffer(IntPtr Data, IntPtr Header, int Length);

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
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
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public nuint User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public nuint Reserved;
        }

        [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen")]
        private static partial uint WaveOutOpen(
            out IntPtr device,
            uint deviceId,
            ref WaveFormat format,
            IntPtr callback,
            IntPtr instance,
            uint flags);

        [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
        private static partial uint WaveOutPrepareHeader(IntPtr device, IntPtr header, uint headerSize);

        [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite")]
        private static partial uint WaveOutWrite(IntPtr device, IntPtr header, uint headerSize);

        [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
        private static partial uint WaveOutUnprepareHeader(IntPtr device, IntPtr header, uint headerSize);

        [LibraryImport("winmm.dll", EntryPoint = "waveOutReset")]
        private static partial uint WaveOutReset(IntPtr device);

        [LibraryImport("winmm.dll", EntryPoint = "waveOutClose")]
        private static partial uint WaveOutClose(IntPtr device);
    }
}
