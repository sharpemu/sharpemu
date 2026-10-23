// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using LibAtrac9;
using SDL;
using static SDL.SDL3;

namespace SharpEmu.GUI;

/// <summary>
/// Loops a game's sce_sys/snd0.at9 preview music while the game is selected
/// in the library, like the console home screen. The ATRAC9 stream is decoded
/// as SDL3 asks for audio (vendored LibAtrac9) on Windows and Linux.
/// </summary>
internal sealed class SndPreviewPlayer
{
    private readonly object _sync = new();
    private int _generation;
    private unsafe SDL_AudioStream* _stream;
    private int _targetQueuedBytes;
    private byte[] _fillBuffer = [];
    private bool _paused;
    private bool _muted;
    private double _volume = 1.0;
    private string? _cachedPath;
    private At9Track? _cachedTrack;

    public unsafe double Volume
    {
        set
        {
            lock (_sync)
            {
                _volume = double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 1.0;
                if (_stream is not null)
                {
                    SDL_SetAudioStreamGain(_stream, (float)_volume);
                }
            }
        }
    }

    /// <summary>
    /// Keeps the music silent while set, even across <see cref="Play"/> and
    /// <see cref="Resume"/>; the track stays ready to continue after unmuting.
    /// </summary>
    public unsafe bool Muted
    {
        get
        {
            lock (_sync)
            {
                return _muted;
            }
        }
        set
        {
            lock (_sync)
            {
                _muted = value;
                if (value && _stream is not null)
                {
                    SDL_PauseAudioStreamDevice(_stream);
                    _paused = true;
                }
            }
        }
    }

    /// <summary>Starts looping the given snd0.at9 after a short debounce.</summary>
    public void Play(string at9Path)
    {
        int generation;
        lock (_sync)
        {
            generation = ++_generation;
        }

        _ = Task.Run(async () =>
        {
            // Debounce so skimming through the library does not decode (or
            // start) a preview per tile.
            await Task.Delay(120).ConfigureAwait(false);

            At9Track? track;
            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                track = string.Equals(_cachedPath, at9Path, StringComparison.OrdinalIgnoreCase)
                    ? _cachedTrack
                    : null;
            }

            if (track is null)
            {
                try
                {
                    track = At9Track.Open(File.ReadAllBytes(at9Path));
                }
                catch (Exception)
                {
                    return; // corrupt or unsupported preview: stay silent
                }
            }

            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                _cachedPath = at9Path;
                _cachedTrack = track;
                StopLocked();
                try
                {
                    track.Rewind();
                    StartLocked(track);
                }
                catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"[GUI][WARN] Title music playback unavailable: {error.Message}");
                    StopLocked();
                }
            }
        });
    }

    public void Stop()
    {
        lock (_sync)
        {
            _generation++;
            StopLocked();
        }
    }

    /// <summary>
    /// Pauses playback while preserving the current position and queued audio.
    /// </summary>
    public unsafe void Pause()
    {
        lock (_sync)
        {
            if (_stream is null || _paused)
            {
                return;
            }
            SDL_PauseAudioStreamDevice(_stream);
            _paused = true;
        }
    }

    /// <summary>Continues the track silenced by <see cref="Pause"/>.</summary>
    public unsafe void Resume()
    {
        lock (_sync)
        {
            if (!_paused || _muted || _stream is null)
            {
                return;
            }
            SDL_ResumeAudioStreamDevice(_stream);
            _paused = false;
        }
    }

    private unsafe void StartLocked(At9Track track)
    {
        if ((SDL_WasInit(SDL_InitFlags.SDL_INIT_AUDIO) & SDL_InitFlags.SDL_INIT_AUDIO) == 0 &&
            !SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO))
        {
            throw new InvalidOperationException("SDL audio initialization failed.");
        }

        var spec = new SDL_AudioSpec
        {
            format = SDL_AudioFormat.SDL_AUDIO_S16LE,
            channels = checked((byte)track.Channels),
            freq = track.SampleRate,
        };
        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, null, IntPtr.Zero);
        if (_stream is null)
        {
            throw new InvalidOperationException("SDL audio stream creation failed.");
        }

        SDL_SetAudioStreamGain(_stream, (float)_volume);
        _targetQueuedBytes = spec.freq * spec.channels * sizeof(short) / 4; // 250 ms of buffered audio
        if (_fillBuffer.Length < _targetQueuedBytes)
        {
            _fillBuffer = new byte[_targetQueuedBytes];
        }

        FillStreamLocked(track);
        _paused = _muted;
        if (!_muted && !SDL_ResumeAudioStreamDevice(_stream))
        {
            throw new InvalidOperationException("SDL audio stream start failed.");
        }

        _ = PumpAsync(_generation, track);
    }

    private async Task PumpAsync(int generation, At9Track track)
    {
        while (true)
        {
            await Task.Delay(40).ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                FillStreamIfActiveLocked(track);
            }
        }
    }

    private unsafe void FillStreamIfActiveLocked(At9Track track)
    {
        if (_stream is not null && !_paused)
        {
            FillStreamLocked(track);
        }
    }

    private unsafe void FillStreamLocked(At9Track track)
    {
        var remaining = Math.Min(_targetQueuedBytes - SDL_GetAudioStreamQueued(_stream), _fillBuffer.Length);
        if (remaining <= 0)
        {
            return;
        }

        var bytes = track.Read(_fillBuffer.AsSpan(0, remaining));
        if (bytes == 0)
        {
            return;
        }

        fixed (byte* data = _fillBuffer)
        {
            SDL_PutAudioStreamData(_stream, (nint)data, bytes);
        }
    }

    private unsafe void StopLocked()
    {
        if (_stream is not null)
        {
            SDL_DestroyAudioStream(_stream);
            _stream = null;
        }

        _paused = false;
    }

    private static readonly Guid Atrac9SubFormat = new("47E142D2-36BA-4D8D-88FC-61654F8C836C");

    /// <summary>
    /// A Sony AT9 (RIFF-wrapped ATRAC9) file decoded one superframe at a time.
    /// Layout per Sony's container: an extensible fmt chunk whose extension
    /// carries the 4-byte codec config, a fact chunk with the sample count
    /// and encoder delay, and superframes in the data chunk.
    /// </summary>
    private sealed class At9Track
    {
        private readonly byte[] _file;
        private readonly byte[] _config;
        private readonly int _dataOffset;
        private readonly int _superframeCount;
        private readonly int _sampleCount;
        private readonly int _encoderDelay;
        private readonly Atrac9Decoder _decoder = new();
        private readonly short[][] _pcm;
        private readonly byte[] _superframe;
        private int _frame;
        private long _decoded;
        private int _written;
        private int _sampleInFrame;

        private At9Track(byte[] file, byte[] config, int dataOffset, int dataSize, int sampleCount, int encoderDelay)
        {
            _file = file;
            _config = config;
            _dataOffset = dataOffset;
            _sampleCount = sampleCount;
            _encoderDelay = encoderDelay;
            _decoder.Initialize(config);
            var decoderConfig = _decoder.Config;
            Channels = decoderConfig.ChannelCount;
            SampleRate = decoderConfig.SampleRate;
            _superframeCount = Math.Min(
                (sampleCount + encoderDelay + decoderConfig.SuperframeSamples - 1) / decoderConfig.SuperframeSamples,
                dataSize / decoderConfig.SuperframeBytes);
            _pcm = new short[Channels][];
            for (var i = 0; i < Channels; i++)
            {
                _pcm[i] = new short[decoderConfig.SuperframeSamples];
            }

            _superframe = new byte[decoderConfig.SuperframeBytes];
            _sampleInFrame = decoderConfig.SuperframeSamples;
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public static At9Track Open(byte[] file)
        {
            using var reader = new BinaryReader(new MemoryStream(file), Encoding.ASCII);
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            {
                throw new InvalidDataException("Not a RIFF file.");
            }

            reader.BaseStream.Position += 4; // riff size
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            {
                throw new InvalidDataException("Not a WAVE file.");
            }

            byte[]? configData = null;
            var sampleCount = 0;
            var encoderDelay = 0;
            var dataOffset = -1;
            var dataSize = 0;

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var chunkSize = reader.ReadInt32();
                var chunkStart = reader.BaseStream.Position;
                switch (chunkId)
                {
                    case "fmt ":
                        var formatTag = reader.ReadUInt16();
                        reader.BaseStream.Position = chunkStart + 24; // to SubFormat GUID
                        var subFormat = new Guid(reader.ReadBytes(16));
                        if (formatTag != 0xFFFE || subFormat != Atrac9SubFormat)
                        {
                            throw new InvalidDataException("Not an ATRAC9 stream.");
                        }

                        reader.BaseStream.Position += 4; // version info
                        configData = reader.ReadBytes(4);
                        break;
                    case "fact":
                        sampleCount = reader.ReadInt32();
                        reader.BaseStream.Position += 4; // input overlap delay
                        encoderDelay = reader.ReadInt32();
                        break;
                    case "data":
                        dataOffset = (int)chunkStart;
                        dataSize = chunkSize;
                        break;
                }

                reader.BaseStream.Position = chunkStart + chunkSize + (chunkSize & 1);
            }

            if (configData is null || sampleCount <= 0 || dataOffset < 0 || dataSize > file.Length - dataOffset)
            {
                throw new InvalidDataException("Missing fmt, fact, or data chunk.");
            }

            return new At9Track(file, configData, dataOffset, dataSize, sampleCount, encoderDelay);
        }

        public void Rewind()
        {
            _decoder.Initialize(_config);
            _frame = 0;
            _decoded = 0;
            _written = 0;
            _sampleInFrame = _pcm[0].Length;
        }

        public int Read(Span<byte> destination)
        {
            var frameBytes = Channels * sizeof(short);
            var bytes = 0;
            while (destination.Length - bytes >= frameBytes)
            {
                if (_written >= _sampleCount || (_sampleInFrame >= _pcm[0].Length && _frame >= _superframeCount))
                {
                    if (_written == 0)
                    {
                        break;
                    }

                    Rewind();
                }

                if (_sampleInFrame >= _pcm[0].Length)
                {
                    Buffer.BlockCopy(_file, _dataOffset + (_frame * _superframe.Length), _superframe, 0, _superframe.Length);
                    _decoder.Decode(_superframe, _pcm);
                    _frame++;
                    _sampleInFrame = 0;
                }

                var sample = _sampleInFrame++;
                if (_decoded++ < _encoderDelay)
                {
                    continue;
                }

                for (var ch = 0; ch < Channels; ch++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(destination[(bytes + (ch * sizeof(short)))..], _pcm[ch][sample]);
                }

                bytes += frameBytes;
                _written++;
            }

            return bytes;
        }
    }
}
