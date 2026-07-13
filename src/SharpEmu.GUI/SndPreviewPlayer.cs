// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using LibAtrac9;

namespace SharpEmu.GUI;

/// <summary>
/// Loops a game's sce_sys/snd0.at9 preview music while the game is selected
/// in the library, like the console home screen. The ATRAC9 stream is decoded
/// to WAV on a background task (vendored LibAtrac9); playback uses winmm's
/// PlaySound, so this is Windows-only and a no-op elsewhere.
/// </summary>
internal sealed class SndPreviewPlayer
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_LOOP = 0x0008;

    private readonly object _sync = new();
    private int _generation;
    private GCHandle _pinnedWav;
    private bool _playing;
    private bool _paused;
    private string? _cachedPath;
    private byte[]? _cachedWav;

    /// <summary>Starts looping the given snd0.at9 after a short debounce.</summary>
    public void Play(string at9Path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int generation;
        lock (_sync)
        {
            generation = ++_generation;
        }

        _ = Task.Run(async () =>
        {
            // Debounce so skimming through the library does not decode (or
            // start) a preview per tile.
            await Task.Delay(300).ConfigureAwait(false);

            byte[]? wav;
            lock (_sync)
            {
                if (generation != _generation)
                {
                    return;
                }

                wav = string.Equals(_cachedPath, at9Path, StringComparison.OrdinalIgnoreCase)
                    ? _cachedWav
                    : null;
            }

            if (wav is null)
            {
                try
                {
                    wav = DecodeAt9ToWav(File.ReadAllBytes(at9Path));
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
                _cachedWav = wav;
                StopLocked();

                // The WAV image must stay pinned while winmm plays from it.
                _pinnedWav = GCHandle.Alloc(wav, GCHandleType.Pinned);
                _playing = PlaySound(_pinnedWav.AddrOfPinnedObject(), 0, SND_MEMORY | SND_ASYNC | SND_LOOP | SND_NODEFAULT);
                if (!_playing)
                {
                    _pinnedWav.Free();
                }
            }
        });
    }

    public void Stop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            _generation++;
            StopLocked();
        }
    }

    /// <summary>
    /// Silences playback but keeps the decoded track ready, so
    /// <see cref="Resume"/> can restart it (winmm cannot truly pause).
    /// </summary>
    public void Pause()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (!_playing)
            {
                return;
            }

            _ = PlaySound(0, 0, 0);
            _playing = false;
            _paused = true;
        }
    }

    /// <summary>Restarts the track silenced by <see cref="Pause"/>.</summary>
    public void Resume()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_sync)
        {
            if (!_paused || !_pinnedWav.IsAllocated)
            {
                return;
            }

            _paused = false;
            _playing = PlaySound(_pinnedWav.AddrOfPinnedObject(), 0, SND_MEMORY | SND_ASYNC | SND_LOOP | SND_NODEFAULT);
        }
    }

    private void StopLocked()
    {
        _ = PlaySound(0, 0, 0);
        _playing = false;
        _paused = false;
        if (_pinnedWav.IsAllocated)
        {
            _pinnedWav.Free();
        }
    }

    private static readonly Guid Atrac9SubFormat = new("47E142D2-36BA-4D8D-88FC-61654F8C836C");

    /// <summary>
    /// Decodes a Sony AT9 (RIFF-wrapped ATRAC9) file to a PCM16 WAV image.
    /// Layout per Sony's container: an extensible fmt chunk whose extension
    /// carries the 4-byte codec config, a fact chunk with the sample count
    /// and encoder delay, and superframes in the data chunk.
    /// </summary>
    private static byte[] DecodeAt9ToWav(byte[] file)
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

        if (configData is null || sampleCount <= 0 || dataOffset < 0)
        {
            throw new InvalidDataException("Missing fmt, fact, or data chunk.");
        }

        var decoder = new Atrac9Decoder();
        decoder.Initialize(configData);
        var config = decoder.Config;

        var superframeCount = (sampleCount + encoderDelay + config.SuperframeSamples - 1) / config.SuperframeSamples;
        superframeCount = Math.Min(superframeCount, dataSize / config.SuperframeBytes);

        var channels = config.ChannelCount;
        var pcmBuffer = new short[channels][];
        for (var i = 0; i < channels; i++)
        {
            pcmBuffer[i] = new short[config.SuperframeSamples];
        }

        var wav = new byte[44 + (sampleCount * channels * 2)];
        WriteWavHeader(wav, channels, config.SampleRate, sampleCount);

        var superframe = new byte[config.SuperframeBytes];
        var decodedIndex = 0L; // per-channel, includes the encoder delay
        var written = 0;
        for (var f = 0; f < superframeCount && written < sampleCount; f++)
        {
            Buffer.BlockCopy(file, dataOffset + (f * config.SuperframeBytes), superframe, 0, config.SuperframeBytes);
            decoder.Decode(superframe, pcmBuffer);
            for (var s = 0; s < config.SuperframeSamples && written < sampleCount; s++)
            {
                if (decodedIndex++ < encoderDelay)
                {
                    continue;
                }

                var sampleOffset = 44 + ((long)written * channels * 2);
                for (var ch = 0; ch < channels; ch++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        wav.AsSpan((int)(sampleOffset + (ch * 2))),
                        pcmBuffer[ch][s]);
                }

                written++;
            }
        }

        return wav;
    }

    private static void WriteWavHeader(byte[] wav, int channels, int sampleRate, int sampleCount)
    {
        var span = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16); // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], sampleCount * channels * 2);
    }

    [DllImport("winmm.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(nint sound, nint module, uint flags);
}
