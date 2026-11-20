// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using LibAtrac9;

namespace SharpEmu.Libs.Audio;

internal enum Atrac9PcmEncoding
{
    Signed16,
    Signed32,
    Float,
}

internal readonly record struct Atrac9DecodeResult(
    int Status,
    int InputConsumed,
    int OutputWritten,
    ulong TotalDecodedSamples,
    uint Frames);

internal readonly record struct Atrac9Gapless(uint TotalSamples, ushort SkipSamples, ushort SkippedSamples);

internal sealed class Atrac9DecodeState
{
    // Set by AJM, whose titles retry a failed job forever: an undecodable superframe plays as silence instead.
    public bool ConcealsUndecodableSuperframes { get; init; }

    internal const int ResultNotInitialized = 0x00000001;
    internal const int ResultInvalidData = 0x00000002;
    internal const int ResultInvalidParameter = 0x00000004;
    internal const int ResultPartialInput = 0x00000008;
    internal const int ResultNotEnoughRoom = 0x00000010;
    internal const int ResultCodecError = 0x40000000;

    private const int MaxContainerHeaderBytes = 8 * 1024;

    private enum ContainerScan
    {
        NotContainer,
        NeedMoreData,
        Found,
    }

    private readonly object _gate = new();
    private Atrac9Decoder? _decoder;
    private byte[]? _configData;
    private byte[]? _compressed;
    private short[][]? _planarPcm;
    private byte[]? _containerHeader;
    private int _containerHeaderLength;
    private int _compressedLength;
    private ulong _totalDecodedSamples;
    private Atrac9Gapless _gaplessInit;
    private Atrac9Gapless _gapless;

    // Samples written and lead-in samples discarded since the last reset: the gapless window counts from there.
    private uint _producedSinceReset;
    private uint _skippedSinceReset;

    public Atrac9Gapless Gapless
    {
        get
        {
            lock (_gate)
            {
                return _gapless;
            }
        }
    }

    // The window counts from the last reset. A set without a reset moves its limits and keeps the progress made against
    // them: a title that sends the same window with every job keeps its place, and one that sends a new window after
    // initializing the instance for the next sound gets that window.
    public void SetGapless(uint totalSamples, ushort skipSamples, bool reset)
    {
        lock (_gate)
        {
            _gaplessInit = new Atrac9Gapless(totalSamples, skipSamples, 0);
            if (reset)
            {
                _producedSinceReset = 0;
                _skippedSinceReset = 0;
            }

            _gapless = new Atrac9Gapless(
                HasSampleLimit ? totalSamples - Math.Min(totalSamples, _producedSinceReset) : totalSamples,
                (ushort)(skipSamples - Math.Min(skipSamples, _skippedSinceReset)),
                reset ? (ushort)0 : _gapless.SkippedSamples);
        }
    }

    private bool HasSampleLimit => _gaplessInit.TotalSamples is not 0 and not uint.MaxValue;

    public Atrac9Config? Config
    {
        get
        {
            lock (_gate)
            {
                return _decoder?.Config;
            }
        }
    }

    public bool TryInitialize(ReadOnlySpan<byte> configData)
    {
        if (configData.Length < 4)
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                var normalizedConfig = configData[..4].ToArray();
                var decoder = new Atrac9Decoder();
                decoder.Initialize(normalizedConfig);
                var config = decoder.Config;

                _decoder = decoder;
                _configData = normalizedConfig;
                _compressed = new byte[config.SuperframeBytes];
                _planarPcm = CreatePcmBuffer(config.ChannelCount, config.SuperframeSamples);
                _compressedLength = 0;
                _totalDecodedSamples = 0;
                _containerHeaderLength = 0;
                _gapless = _gaplessInit;
                _producedSinceReset = 0;
                _skippedSinceReset = 0;
                Trace(
                    $"initialized config={Convert.ToHexString(normalizedConfig)} channels={config.ChannelCount} " +
                    $"rate={config.SampleRate} frame_samples={config.FrameSamples} " +
                    $"superframe_samples={config.SuperframeSamples} superframe_bytes={config.SuperframeBytes} " +
                    $"frames_per_superframe={config.FramesPerSuperframe}");
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or InvalidOperationException)
            {
                Clear();
                return false;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_configData is null)
            {
                Clear();
                return;
            }

            var decoder = new Atrac9Decoder();
            decoder.Initialize((byte[])_configData.Clone());
            _decoder = decoder;
            _compressedLength = 0;
            _totalDecodedSamples = 0;
            _containerHeaderLength = 0;
            _gapless = _gaplessInit;
            _producedSinceReset = 0;
            _skippedSinceReset = 0;
            if (_compressed is not null)
            {
                Array.Clear(_compressed);
            }
        }
    }

    public Atrac9DecodeResult Decode(
        ReadOnlySpan<byte> input,
        Span<byte> output,
        Atrac9PcmEncoding encoding,
        int requestedChannels,
        bool multipleFrames)
    {
        lock (_gate)
        {
            if (_decoder is null || _compressed is null || _planarPcm is null)
            {
                return new Atrac9DecodeResult(
                    ResultNotInitialized,
                    0,
                    0,
                    _totalDecodedSamples,
                    0);
            }

            var config = _decoder.Config;
            var channels = requestedChannels > 0 ? requestedChannels : config.ChannelCount;
            if (channels is < 1 or > 16)
            {
                return new Atrac9DecodeResult(
                    ResultInvalidParameter,
                    0,
                    0,
                    _totalDecodedSamples,
                    0);
            }

            var bytesPerSample = GetBytesPerSample(encoding);
            var consumed = 0;
            var written = 0;
            uint frames = 0;
            var status = 0;

            while (_compressedLength == config.SuperframeBytes ||
                   consumed < input.Length)
            {
                // Titles that stream whole .at9 files hand AJM the RIFF/WAVE
                // container rather than a pointer into its `data` chunk, so the
                // stream has to be advanced past the header before the first
                // superframe — otherwise every job fails with invalid data and
                // the title drops the voice. This is checked at every superframe
                // boundary, not just after initialize, because a looping voice
                // rewinds to the file header without reinitialising. It comes before
                // the window check: the header of the next pass restarts a window the
                // last pass used up.
                if (_compressedLength == 0 && (_containerHeaderLength != 0 || consumed < input.Length))
                {
                    var scan = ScanContainerHeader(input[consumed..], out var headerBytes);
                    if (scan == ContainerScan.NeedMoreData)
                    {
                        consumed = input.Length;
                        status |= ResultPartialInput;
                        break;
                    }

                    if (scan == ContainerScan.Found)
                    {
                        _containerHeader = null;
                        _containerHeaderLength = 0;
                        consumed += headerBytes;
                        // The header may have brought another stream's codec configuration.
                        config = _decoder!.Config;
                        channels = requestedChannels > 0 ? requestedChannels : config.ChannelCount;
                        continue;
                    }
                }

                if (HasSampleLimit && _gapless.TotalSamples == 0)
                {
                    break;
                }

                // The room check comes before the superframe's bytes are taken: a superframe the output has no room for
                // stays in the input, as the codec leaves it, instead of being reported consumed with nothing produced.
                var skip = Math.Min(config.SuperframeSamples, (int)_gapless.SkipSamples);
                var samples = config.SuperframeSamples - skip;
                if (HasSampleLimit)
                {
                    samples = (int)Math.Min((uint)samples, _gapless.TotalSamples);
                }

                var outputBytes = checked(samples * channels * bytesPerSample);
                if (output.Length - written < outputBytes)
                {
                    status |= ResultNotEnoughRoom;
                    break;
                }

                if (_compressedLength < config.SuperframeBytes)
                {
                    var copied = Math.Min(config.SuperframeBytes - _compressedLength, input.Length - consumed);
                    input.Slice(consumed, copied).CopyTo(_compressed.AsSpan(_compressedLength));
                    _compressedLength += copied;
                    consumed += copied;
                }

                if (_compressedLength < config.SuperframeBytes)
                {
                    status |= ResultPartialInput;
                    break;
                }

                try
                {
                    _decoder.Decode(_compressed, _planarPcm);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException)
                {
                    Trace(
                        $"decode_failed superframe_bytes={config.SuperframeBytes} " +
                        $"config={Convert.ToHexString(_configData ?? [])} " +
                        $"head={Convert.ToHexString(_compressed.AsSpan(0, Math.Min(16, _compressed.Length)))} " +
                        $"error={exception.GetType().Name}: {exception.Message}");

                    // AJM plays a superframe it cannot decode as silence and goes on: a title that expects its
                    // stream to decode retries a failed job without advancing, so an error left the voice stuck on
                    // that superframe for good. Superframes decode independently, so the codec starts fresh for the
                    // next one. Other users report the error.
                    if (!ConcealsUndecodableSuperframes)
                    {
                        _compressedLength = 0;
                        return new Atrac9DecodeResult(
                            status | ResultInvalidData | ResultCodecError,
                            consumed,
                            written,
                            _totalDecodedSamples,
                            frames);
                    }

                    foreach (var plane in _planarPcm)
                    {
                        Array.Clear(plane);
                    }

                    var fresh = new Atrac9Decoder();
                    fresh.Initialize((byte[])_configData!.Clone());
                    _decoder = fresh;
                }

                WriteInterleaved(
                    _planarPcm,
                    output.Slice(written, outputBytes),
                    skip,
                    samples,
                    channels,
                    encoding);

                _gapless = new Atrac9Gapless(
                    HasSampleLimit ? _gapless.TotalSamples - (uint)samples : _gapless.TotalSamples,
                    (ushort)(_gapless.SkipSamples - skip),
                    (ushort)Math.Min(ushort.MaxValue, _gapless.SkippedSamples + config.SuperframeSamples - samples));
                written += outputBytes;
                _compressedLength = 0;
                _totalDecodedSamples += unchecked((uint)samples);
                _producedSinceReset = unchecked(_producedSinceReset + (uint)samples);
                _skippedSinceReset = unchecked(_skippedSinceReset + (uint)skip);
                frames += unchecked((uint)config.FramesPerSuperframe);

                if (!multipleFrames)
                {
                    break;
                }
            }

            // A job without input reports that it needs some, as for a partial superframe.
            if (input.IsEmpty && written == 0 && _compressedLength < config.SuperframeBytes)
            {
                status |= ResultPartialInput;
            }

            return new Atrac9DecodeResult(
                status,
                consumed,
                written,
                _totalDecodedSamples,
                frames);
        }
    }

    private ContainerScan ScanContainerHeader(ReadOnlySpan<byte> input, out int inputHeaderBytes)
    {
        inputHeaderBytes = 0;

        if (_containerHeaderLength == 0 &&
            (input.Length < 4 || !input[..4].SequenceEqual("RIFF"u8)))
        {
            return ContainerScan.NotContainer;
        }

        _containerHeader ??= new byte[MaxContainerHeaderBytes];
        var previousLength = _containerHeaderLength;
        var copied = Math.Min(input.Length, MaxContainerHeaderBytes - previousLength);
        input[..copied].CopyTo(_containerHeader.AsSpan(previousLength));
        var totalLength = previousLength + copied;

        if (!TryFindRiffDataOffset(_containerHeader.AsSpan(0, totalLength), out var dataOffset, out var fact, out var headerConfig))
        {
            if (totalLength >= MaxContainerHeaderBytes)
            {
                // Not a shape we understand — fall back to treating the stream
                // as raw superframes rather than swallowing it forever. The
                // scratch is dropped without advancing the caller's cursor, so
                // the bytes are still decoded normally.
                Trace($"container_scan_gave_up bytes={totalLength}");
                _containerHeader = null;
                _containerHeaderLength = 0;
                return ContainerScan.NotContainer;
            }

            _containerHeaderLength = totalLength;
            return ContainerScan.NeedMoreData;
        }

        inputHeaderBytes = dataOffset - previousLength;
        if (headerConfig is not null)
        {
            RestartCodec(headerConfig);
        }

        if (fact is { } window)
        {
            RestartWindow(window.Total, window.Skip);
        }

        Trace($"container_header_skipped bytes={dataOffset} from_this_input={inputHeaderBytes} fact={fact}");
        return ContainerScan.Found;
    }

    // A stream that reaches a RIFF header starts over as the hardware's header parse does: a looping voice rewinds to its
    // file header, and a title may feed the next file through the same instance, without initializing it. The format
    // chunk's codec configuration starts the codec fresh (taking that stream's configuration), and the fact chunk's
    // sample count and lead-in are the new window. Without it, a window the previous pass used up kept the voice silent
    // for good while the title went on feeding it.
    private void RestartCodec(byte[] headerConfig)
    {
        try
        {
            var decoder = new Atrac9Decoder();
            decoder.Initialize((byte[])headerConfig.Clone());
            var config = decoder.Config;
            if (_configData is null || !_configData.AsSpan().SequenceEqual(headerConfig))
            {
                _configData = headerConfig;
                _compressed = new byte[config.SuperframeBytes];
                _planarPcm = CreatePcmBuffer(config.ChannelCount, config.SuperframeSamples);
            }

            _decoder = decoder;
            _compressedLength = 0;
            _totalDecodedSamples = 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            Trace($"header_config_rejected config={Convert.ToHexString(headerConfig)} error={exception.Message}");
        }
    }

    private void RestartWindow(uint totalSamples, uint skipSamples)
    {
        _gaplessInit = new Atrac9Gapless(totalSamples, (ushort)Math.Min(skipSamples, ushort.MaxValue), 0);
        _gapless = _gaplessInit;
        _producedSinceReset = 0;
        _skippedSinceReset = 0;
    }

    // The ATRAC9 subformat of a WAVE_FORMAT_EXTENSIBLE format chunk; the codec configuration follows at byte 44.
    private static ReadOnlySpan<byte> Atrac9SubFormat =>
        [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D, 0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C];

    private static bool TryFindRiffDataOffset(
        ReadOnlySpan<byte> header, out int dataOffset, out (uint Total, uint Skip)? fact, out byte[]? config)
    {
        dataOffset = 0;
        fact = null;
        config = null;
        if (header.Length < 12 ||
            !header[..4].SequenceEqual("RIFF"u8) ||
            !header.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var offset = 12;
        while (offset + 8 <= header.Length)
        {
            var chunkId = header.Slice(offset, 4);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset + 4, 4));
            offset += 8;
            if (chunkId.SequenceEqual("data"u8))
            {
                dataOffset = offset;
                return true;
            }

            if (chunkId.SequenceEqual("fact"u8) && chunkSize >= 8 && offset + 8 <= header.Length)
            {
                fact = (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(offset + 4, 4)));
            }

            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 48 && offset + 48 <= header.Length &&
                BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(offset, 2)) == 0xFFFE &&
                header.Slice(offset + 24, 16).SequenceEqual(Atrac9SubFormat))
            {
                config = header.Slice(offset + 44, 4).ToArray();
            }

            // RIFF chunks are word aligned.
            var advance = chunkSize + (chunkSize & 1);
            if (advance > (ulong)(int.MaxValue - offset))
            {
                return false;
            }

            offset += (int)advance;
        }

        return false;
    }

    private static short[][] CreatePcmBuffer(int channels, int samples)
    {
        var result = new short[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            result[channel] = new short[samples];
        }

        return result;
    }

    private static int GetBytesPerSample(Atrac9PcmEncoding encoding) =>
        encoding switch
        {
            Atrac9PcmEncoding.Signed16 => sizeof(short),
            Atrac9PcmEncoding.Signed32 => sizeof(int),
            Atrac9PcmEncoding.Float => sizeof(float),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

    private static void WriteInterleaved(
        short[][] source,
        Span<byte> destination,
        int firstSample,
        int samples,
        int channels,
        Atrac9PcmEncoding encoding)
    {
        var offset = 0;
        for (var sample = firstSample; sample < firstSample + samples; sample++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var sourceChannel = Math.Min(channel, source.Length - 1);
                var value = source[sourceChannel][sample];
                switch (encoding)
                {
                    case Atrac9PcmEncoding.Signed16:
                        BinaryPrimitives.WriteInt16LittleEndian(destination[offset..], value);
                        offset += sizeof(short);
                        break;
                    case Atrac9PcmEncoding.Signed32:
                        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value << 16);
                        offset += sizeof(int);
                        break;
                    case Atrac9PcmEncoding.Float:
                        BinaryPrimitives.WriteInt32LittleEndian(
                            destination[offset..],
                            BitConverter.SingleToInt32Bits(value / 32768.0f));
                        offset += sizeof(float);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(encoding));
                }
            }
        }
    }

    private static void Trace(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AJM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] ajm.at9.{message}");
        }
    }

    private void Clear()
    {
        _decoder = null;
        _configData = null;
        _compressed = null;
        _planarPcm = null;
        _containerHeader = null;
        _containerHeaderLength = 0;
        _compressedLength = 0;
        _totalDecodedSamples = 0;
        _producedSinceReset = 0;
        _skippedSinceReset = 0;
    }
}
