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

internal sealed class Atrac9DecodeState
{
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
                Trace(
                    $"initialized config={Convert.ToHexString(normalizedConfig)} channels={config.ChannelCount} " +
                    $"rate={config.SampleRate} frame_samples={config.FrameSamples} " +
                    $"superframe_samples={config.SuperframeSamples} superframe_bytes={config.SuperframeBytes} " +
                    $"frames_per_superframe={config.FramesPerSuperframe}");
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException)
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
            var outputBytesPerSuperframe = checked(config.SuperframeSamples * channels * bytesPerSample);
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
                // rewinds to the file header without reinitialising.
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
                        continue;
                    }
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

                if (output.Length - written < outputBytesPerSuperframe)
                {
                    status |= ResultNotEnoughRoom;
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
                    _compressedLength = 0;
                    return new Atrac9DecodeResult(
                        status | ResultInvalidData | ResultCodecError,
                        consumed,
                        written,
                        _totalDecodedSamples,
                        frames);
                }

                WriteInterleaved(
                    _planarPcm,
                    output.Slice(written, outputBytesPerSuperframe),
                    config.SuperframeSamples,
                    channels,
                    encoding);

                written += outputBytesPerSuperframe;
                _compressedLength = 0;
                _totalDecodedSamples += unchecked((uint)config.SuperframeSamples);
                frames += unchecked((uint)config.FramesPerSuperframe);

                if (!multipleFrames)
                {
                    break;
                }
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

        if (!TryFindRiffDataOffset(_containerHeader.AsSpan(0, totalLength), out var dataOffset))
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
        Trace($"container_header_skipped bytes={dataOffset} from_this_input={inputHeaderBytes}");
        return ContainerScan.Found;
    }

    private static bool TryFindRiffDataOffset(ReadOnlySpan<byte> header, out int dataOffset)
    {
        dataOffset = 0;
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
        int samples,
        int channels,
        Atrac9PcmEncoding encoding)
    {
        var offset = 0;
        for (var sample = 0; sample < samples; sample++)
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
    }
}
