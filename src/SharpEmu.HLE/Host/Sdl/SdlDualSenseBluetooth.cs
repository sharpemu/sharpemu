// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Concentus;
using Concentus.Enums;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.HLE.Host.Sdl;

internal sealed unsafe class SdlDualSenseBluetooth
{
    private const ushort SonyVendorId = 0x054C;
    private const ushort DualSenseProductId = 0x0CE6;
    private const ushort DualSenseEdgeProductId = 0x0DF2;
    private const int InputChannels = 4;
    private const int ReportLength = 547;
    private const byte ReportId = 0x39;
    private const int HapticRate = 3000;
    private const int HapticFrames = 64;
    private const int HapticBlockBytes = 64;
    private const int HapticOffset = 12;
    private const int SpeakerRate = 45000;
    private const int OpusSampleRate = 48000;
    private const int OpusFrames = 480;
    private const int OpusPacketBytes = 200;
    private const int SpeakerFrames = 2 * OpusFrames;
    private const int SpeakerHeaderOffset = 140;
    private const int SpeakerOffset = 142;
    private const byte AudioBufferLength = 48;
    private const int ReopenDelayMilliseconds = 1000;
    private static readonly long PeriodTicks = Stopwatch.Frequency * HapticFrames / HapticRate;

    private static readonly object RegistryGate = new();
    private static SdlDualSenseBluetooth? _shared;

    private readonly object _gate = new();
    private readonly List<Client> _clients = [];
    private readonly AutoResetEvent _signal = new(false);
    private readonly byte[] _path;
    private readonly Thread _sender;
    private SDL_hid_device* _device;
    private IOpusEncoder? _encoder;
    private bool _stopping;
    private byte _sequence;
    private byte _counter;
    private long _reopenAt;
    private bool _writeFailureLogged;
    private bool _encodeFailureLogged;

    private SdlDualSenseBluetooth(byte[] path, SDL_hid_device* device)
    {
        _path = path;
        _device = device;
        _sender = new Thread(Run)
        {
            IsBackground = true,
            Name = "DualSense Bluetooth audio",
            Priority = ThreadPriority.AboveNormal,
        };
        _sender.Start();
    }

    public static IHostAudioStream? TryOpen(SDL_JoystickID pad, uint sampleRate, int maxQueuedBytes, bool speaker)
    {
        lock (RegistryGate)
        {
            var link = _shared ?? Create(pad);
            if (link is null)
            {
                return null;
            }

            _shared = link;
            try
            {
                return link.Attach(sampleRate, maxQueuedBytes, speaker);
            }
            catch
            {
                if (link.ClientCount == 0)
                {
                    link.Shutdown();
                }

                throw;
            }
        }
    }

    private int ClientCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    private static SdlDualSenseBluetooth? Create(SDL_JoystickID pad)
    {
        if (SDL_hid_init() != 0)
        {
            return null;
        }

        var path = FindPath(pad);
        var device = path is null ? null : OpenPath(path);
        if (device is null)
        {
            SDL_hid_exit();
            return null;
        }

        return new SdlDualSenseBluetooth(path!, device);
    }

    private static byte[]? FindPath(SDL_JoystickID pad)
    {
        string? gamepadPath = null;
        SDL_LockJoysticks();
        try
        {
            var gamepad = SDL_GetGamepadFromID(pad);
            if (gamepad is not null)
            {
                gamepadPath = SDL_GetGamepadPath(gamepad);
            }
        }
        finally
        {
            SDL_UnlockJoysticks();
        }

        byte[]? match = null;
        byte[]? only = null;
        var count = 0;
        var devices = SDL_hid_enumerate(SonyVendorId, 0);
        try
        {
            for (var device = devices; device is not null; device = device->next)
            {
                if (device->bus_type != SDL_hid_bus_type.SDL_HID_API_BUS_BLUETOOTH ||
                    device->product_id is not (DualSenseProductId or DualSenseEdgeProductId) ||
                    device->path is null ||
                    (device->usage_page != 0 && (device->usage_page != 1 || device->usage != 5)))
                {
                    continue;
                }

                var path = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(device->path);
                var copy = new byte[path.Length + 1];
                path.CopyTo(copy);
                if (gamepadPath is not null &&
                    string.Equals(Encoding.UTF8.GetString(path), gamepadPath, StringComparison.OrdinalIgnoreCase))
                {
                    match = copy;
                }

                only = copy;
                count++;
            }
        }
        finally
        {
            SDL_hid_free_enumeration(devices);
        }

        return match ?? (count == 1 ? only : null);
    }

    private static SDL_hid_device* OpenPath(byte[] path)
    {
        fixed (byte* data = path)
        {
            return SDL_hid_open_path(data);
        }
    }

    private Client Attach(uint sampleRate, int maxQueuedBytes, bool speaker)
    {
        var queueMilliseconds = maxQueuedBytes * 1000.0 / ((double)sampleRate * InputChannels * sizeof(float));
        var client = new Client(this, sampleRate, queueMilliseconds, speaker);
        lock (_gate)
        {
            _clients.Add(client);
        }

        return client;
    }

    private void Detach(Client client)
    {
        lock (RegistryGate)
        {
            lock (_gate)
            {
                _clients.Remove(client);
                client.Release();
                if (_clients.Count > 0)
                {
                    return;
                }
            }

            Shutdown();
        }
    }

    private void Shutdown()
    {
        lock (_gate)
        {
            _stopping = true;
        }

        if (ReferenceEquals(_shared, this))
        {
            _shared = null;
        }

        _signal.Set();
        _sender.Join();
        if (_device is not null)
        {
            SDL_hid_close(_device);
            _device = null;
        }

        SDL_hid_exit();
    }

    private void Run()
    {
        var report = new byte[ReportLength];
        var haptics = new float[HapticFrames * 2];
        var speaker = new float[SpeakerFrames * 2];
        var next = 0L;
        while (true)
        {
            bool active;
            bool speakerOpen;
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                Array.Clear(haptics);
                Array.Clear(speaker);
                active = false;
                speakerOpen = false;
                foreach (var client in _clients)
                {
                    active |= client.Haptics.MixInto(haptics);
                    if (client.Speaker is { } lane)
                    {
                        speakerOpen = true;
                        active |= lane.MixInto(speaker);
                    }
                }
            }

            if (!active)
            {
                next = 0;
                _signal.WaitOne(250);
                continue;
            }

            BuildReport(report, haptics, speakerOpen ? speaker : null);
            Send(report);

            var now = Stopwatch.GetTimestamp();
            next = (next == 0 ? now : next) + PeriodTicks;
            if (now - next > PeriodTicks)
            {
                next = now;
            }

            var wait = next - Stopwatch.GetTimestamp();
            if (wait > 0)
            {
                SDL_DelayNS((ulong)(wait * 1_000_000_000.0 / Stopwatch.Frequency));
            }
        }
    }

    private void BuildReport(byte[] report, float[] haptics, float[]? speaker)
    {
        Array.Clear(report);
        report[0] = ReportId;
        report[1] = (byte)(_sequence << 4);
        _sequence = (byte)((_sequence + 1) & 0x0F);
        report[2] = 0x91;
        report[3] = 6;
        report[4] = 0x7E;
        report.AsSpan(5, 4).Fill(AudioBufferLength);
        _counter += 2;
        report[9] = _counter;
        report[10] = 0xD2;
        report[11] = HapticBlockBytes;
        for (var index = 0; index < haptics.Length; index++)
        {
            report[HapticOffset + index] = (byte)(sbyte)ToHapticSample(haptics[index]);
        }

        if (speaker is not null && EncodeSpeaker(speaker, report.AsSpan(SpeakerOffset, 2 * OpusPacketBytes)))
        {
            report[SpeakerHeaderOffset] = 0xD3;
            report[SpeakerHeaderOffset + 1] = OpusPacketBytes;
        }

        byte header = 0xA2;
        var crc = SDL_crc32(0, (nint)(&header), 1);
        fixed (byte* data = report)
        {
            crc = SDL_crc32(crc, (nint)data, (nuint)(report.Length - sizeof(uint)));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(report.Length - sizeof(uint)), crc);
    }

    private static int ToHapticSample(float value) =>
        float.IsFinite(value) ? (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 127f) : 0;

    private bool EncodeSpeaker(float[] speaker, Span<byte> destination)
    {
        for (var index = 0; index < speaker.Length; index++)
        {
            speaker[index] = float.IsFinite(speaker[index]) ? Math.Clamp(speaker[index], -1f, 1f) : 0f;
        }

        try
        {
            if (_encoder is null)
            {
                OpusCodecFactory.AttemptToUseNativeLibrary = false;
                var encoder = OpusCodecFactory.CreateEncoder(OpusSampleRate, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
                encoder.Bitrate = OpusPacketBytes * 8 * OpusSampleRate / OpusFrames;
                encoder.UseVBR = false;
                encoder.Complexity = 0;
                _encoder = encoder;
            }

            for (var packet = 0; packet < 2; packet++)
            {
                var encoded = _encoder.Encode(
                    speaker.AsSpan(packet * OpusFrames * 2, OpusFrames * 2),
                    OpusFrames,
                    destination.Slice(packet * OpusPacketBytes, OpusPacketBytes),
                    OpusPacketBytes);
                if (encoded != OpusPacketBytes)
                {
                    destination.Clear();
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            destination.Clear();
            if (!_encodeFailureLogged)
            {
                _encodeFailureLogged = true;
                Console.Error.WriteLine($"[LOADER][WARN] DualSense Bluetooth speaker encoding failed: {exception.Message}");
            }

            return false;
        }
    }

    private void Send(byte[] report)
    {
        if (_device is null)
        {
            if (Stopwatch.GetTimestamp() < _reopenAt)
            {
                return;
            }

            _device = OpenPath(_path);
            if (_device is null)
            {
                _reopenAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * ReopenDelayMilliseconds / 1000;
                return;
            }
        }

        int written;
        fixed (byte* data = report)
        {
            written = SDL_hid_write(_device, data, (nuint)report.Length);
        }

        if (written >= 0)
        {
            return;
        }

        if (!_writeFailureLogged)
        {
            _writeFailureLogged = true;
            Console.Error.WriteLine($"[LOADER][WARN] DualSense Bluetooth audio report refused: {SDL_GetError()}");
        }

        SDL_hid_close(_device);
        _device = null;
        _reopenAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * ReopenDelayMilliseconds / 1000;
    }

    private sealed class Client : IHostAudioStream
    {
        private readonly SdlDualSenseBluetooth _link;
        private int _disposed;

        public Client(SdlDualSenseBluetooth link, uint sampleRate, double queueMilliseconds, bool speaker)
        {
            _link = link;
            Haptics = new Lane(sampleRate, HapticRate, HapticFrames, queueMilliseconds);
            try
            {
                Speaker = speaker ? new Lane(sampleRate, SpeakerRate, SpeakerFrames, queueMilliseconds) : null;
            }
            catch
            {
                Haptics.Dispose();
                throw;
            }
        }

        public Lane Haptics { get; }

        public Lane? Speaker { get; }

        public int QueuedMilliseconds => Haptics.QueuedMilliseconds;

        public bool Submit(ReadOnlySpan<byte> pcm)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var samples = MemoryMarshal.Cast<byte, float>(pcm);
            var frames = samples.Length / InputChannels;
            if (frames == 0)
            {
                return true;
            }

            var pair = ArrayPool<float>.Shared.Rent(frames * 2);
            try
            {
                var stereo = pair.AsSpan(0, frames * 2);
                if (Speaker is not null)
                {
                    CopyPair(samples, stereo, 0);
                    Speaker.Put(stereo);
                }

                CopyPair(samples, stereo, 2);
                Haptics.Put(stereo);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(pair);
            }

            _link._signal.Set();
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _link.Detach(this);
            }
        }

        public void Release()
        {
            Haptics.Dispose();
            Speaker?.Dispose();
        }

        private static void CopyPair(ReadOnlySpan<float> samples, Span<float> stereo, int firstChannel)
        {
            for (var frame = 0; frame < stereo.Length / 2; frame++)
            {
                stereo[2 * frame] = samples[(frame * InputChannels) + firstChannel];
                stereo[(2 * frame) + 1] = samples[(frame * InputChannels) + firstChannel + 1];
            }
        }
    }

    private sealed class Lane : IDisposable
    {
        private const int FrameBytes = 2 * sizeof(float);
        private readonly object _gate = new();
        private readonly int _outputRate;
        private readonly int _primeBytes;
        private readonly int _capacityBytes;
        private SDL_AudioStream* _stream;
        private bool _primed;

        public Lane(uint inputRate, int outputRate, int reportFrames, double queueMilliseconds)
        {
            var source = new SDL_AudioSpec
            {
                format = SDL_AudioFormat.SDL_AUDIO_F32LE,
                channels = 2,
                freq = checked((int)inputRate),
            };
            var target = new SDL_AudioSpec
            {
                format = SDL_AudioFormat.SDL_AUDIO_F32LE,
                channels = 2,
                freq = outputRate,
            };
            _stream = SDL_CreateAudioStream(&source, &target);
            if (_stream is null)
            {
                throw new InvalidOperationException($"SDL audio stream creation failed: {SDL_GetError()}");
            }

            _outputRate = outputRate;
            _primeBytes = reportFrames * 3 / 2 * FrameBytes;
            _capacityBytes = Math.Max(2 * reportFrames, (int)(outputRate * queueMilliseconds / 1000)) * FrameBytes;
        }

        public int QueuedMilliseconds
        {
            get
            {
                lock (_gate)
                {
                    if (_stream is null)
                    {
                        return -1;
                    }

                    var available = SDL_GetAudioStreamAvailable(_stream);
                    return available < 0 ? -1 : (int)(available / FrameBytes * 1000L / _outputRate);
                }
            }
        }

        public void Put(ReadOnlySpan<float> stereo)
        {
            lock (_gate)
            {
                if (_stream is null)
                {
                    return;
                }

                fixed (float* data = stereo)
                {
                    SDL_PutAudioStreamData(_stream, (nint)data, stereo.Length * sizeof(float));
                }

                var excess = SDL_GetAudioStreamAvailable(_stream) - _capacityBytes;
                if (excess > 0)
                {
                    Discard(excess);
                }
            }
        }

        public bool MixInto(Span<float> destination)
        {
            lock (_gate)
            {
                if (_stream is null)
                {
                    return false;
                }

                if (!_primed && SDL_GetAudioStreamAvailable(_stream) < _primeBytes)
                {
                    return false;
                }

                var wanted = destination.Length * sizeof(float);
                var buffer = ArrayPool<float>.Shared.Rent(destination.Length);
                try
                {
                    int read;
                    fixed (float* data = buffer)
                    {
                        read = SDL_GetAudioStreamData(_stream, (nint)data, wanted);
                    }

                    _primed = read == wanted;
                    if (read <= 0)
                    {
                        return false;
                    }

                    var samples = read / sizeof(float);
                    for (var index = 0; index < samples; index++)
                    {
                        destination[index] += buffer[index];
                    }

                    return true;
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(buffer);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stream is null)
                {
                    return;
                }

                SDL_DestroyAudioStream(_stream);
                _stream = null;
            }
        }

        private void Discard(int bytes)
        {
            Span<byte> scratch = stackalloc byte[4096];
            bytes = (bytes + FrameBytes - 1) / FrameBytes * FrameBytes;
            fixed (byte* buffer = scratch)
            {
                while (bytes > 0)
                {
                    var read = SDL_GetAudioStreamData(_stream, (nint)buffer, Math.Min(bytes, scratch.Length));
                    if (read <= 0)
                    {
                        break;
                    }

                    bytes -= read;
                }
            }
        }
    }
}
