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

internal readonly record struct Atrac9StreamInfo(
    int Channels,
    int SampleRate,
    int SuperframeBytes,
    int FramesPerSuperframe,
    int FrameSamples,
    int BytesPerFrame);

internal sealed class Atrac9DecodeState
{
    internal const int ResultNotInitialized = 0x00000001;
    internal const int ResultInvalidData = 0x00000002;
    internal const int ResultInvalidParameter = 0x00000004;
    internal const int ResultPartialInput = 0x00000008;
    internal const int ResultNotEnoughRoom = 0x00000010;
    internal const int ResultCodecError = 0x40000000;

    private const int MaxContainerHeaderBytes = 8 * 1024;
    internal static readonly object DecoderInitGate = new();
    private static readonly int[] SampleRates =
    [
        11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000,
        44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000,
    ];
    private static readonly int[] FrameSamplePowers = [6, 6, 7, 7, 7, 8, 8, 8, 6, 6, 7, 7, 7, 8, 8, 8];
    private static readonly int[] StandardChannelCounts = [1, 2, 2, 6, 8, 4];

    private enum ContainerScan
    {
        NotContainer,
        NeedMoreData,
        Found,
    }

    private readonly object _gate = new();
    private Atrac9Decoder? _decoder;
    private Atrac9Decoder[]? _extendedDecoders;
    private byte[]? _decoderConfig;
    private byte[]? _extendedDecoderConfig;
    private Atrac9StreamInfo? _info;
    private byte[]? _configData;
    private byte[]? _compressed;
    private short[][]? _planarPcm;
    private byte[]? _containerHeader;
    private int _containerHeaderLength;
    private int _compressedLength;
    private ulong _totalDecodedSamples;
    private ulong _totalSampleLimit;
    private ulong _skippedSamples;
    private ushort _skipSamples;
    private bool _decodeErrorLogged;

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

    internal Atrac9StreamInfo? Info
    {
        get
        {
            lock (_gate)
            {
                return _info;
            }
        }
    }

    public bool TryInitialize(ReadOnlySpan<byte> configData, uint totalSamples = 0, ushort skipSamples = 0)
    {
        if (configData.Length < 4)
        {
            return false;
        }

        lock (_gate)
        {
            ClearNoLock();
            if (!TryParseConfig(
                    configData,
                    out var info,
                    out var decoderConfig,
                    out var extendedDecoderConfig))
            {
                return false;
            }

            _configData = configData[..4].ToArray();
            _info = info;
            _decoderConfig = decoderConfig;
            _extendedDecoderConfig = extendedDecoderConfig;
            _compressed = new byte[info.SuperframeBytes];
            _planarPcm = CreatePcmBuffer(info.Channels, info.FrameSamples * info.FramesPerSuperframe);
            _compressedLength = 0;
            _totalDecodedSamples = 0;
            _totalSampleLimit = totalSamples;
            _skipSamples = skipSamples;
            _skippedSamples = 0;

            try
            {
                lock (DecoderInitGate)
                {
                    InitializeDecodersNoLock();
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException)
            {
                _decoder = null;
                _extendedDecoders = null;
                Trace($"decoder_unavailable config={Convert.ToHexString(_configData)} error={exception.GetType().Name}");
            }

            _containerHeaderLength = 0;
            Trace(
                $"initialized config={Convert.ToHexString(_configData)} channels={info.Channels} " +
                $"rate={info.SampleRate} frame_samples={info.FrameSamples} " +
                $"superframe_samples={info.FrameSamples * info.FramesPerSuperframe} " +
                $"superframe_bytes={info.SuperframeBytes} frames_per_superframe={info.FramesPerSuperframe} " +
                $"decoder={(_decoder is not null || _extendedDecoders is not null ? "yes" : "silence")}");
            return true;
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

            lock (DecoderInitGate)
            {
                InitializeDecodersNoLock();
            }
            _compressedLength = 0;
            _totalDecodedSamples = 0;
            _skippedSamples = 0;
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
            if (_info is not { } info || _compressed is null || _planarPcm is null)
            {
                return new Atrac9DecodeResult(
                    ResultNotInitialized,
                    0,
                    0,
                    _totalDecodedSamples,
                    0);
            }

            var channels = requestedChannels > 0 ? requestedChannels : info.Channels;
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
            var outputBytesPerSuperframe = checked(info.FrameSamples * info.FramesPerSuperframe * channels * bytesPerSample);
            var consumed = 0;
            var written = 0;
            uint frames = 0;
            var status = 0;

            while (_compressedLength == info.SuperframeBytes ||
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

                if (_compressedLength < info.SuperframeBytes)
                {
                    var copied = Math.Min(info.SuperframeBytes - _compressedLength, input.Length - consumed);
                    input.Slice(consumed, copied).CopyTo(_compressed.AsSpan(_compressedLength));
                    _compressedLength += copied;
                    consumed += copied;
                }

                if (_compressedLength < info.SuperframeBytes)
                {
                    status |= ResultPartialInput;
                    break;
                }

                if (output.Length - written < outputBytesPerSuperframe)
                {
                    status |= ResultNotEnoughRoom;
                    break;
                }

                var decoded = TryDecodeSuperframeNoLock(channels, encoding, output.Slice(written, outputBytesPerSuperframe));
                if (!decoded)
                {
                    if (!_decodeErrorLogged)
                    {
                        Trace(
                            $"decode_failed superframe_bytes={info.SuperframeBytes} " +
                            $"config={Convert.ToHexString(_configData ?? [])} " +
                            $"head={Convert.ToHexString(_compressed.AsSpan(0, Math.Min(16, _compressed.Length)))} " +
                            "playing_silence");
                        _decodeErrorLogged = true;
                    }
                    output.Slice(written, outputBytesPerSuperframe).Clear();
                    lock (DecoderInitGate)
                    {
                        InitializeDecodersNoLock();
                    }
                }

                var fullSamples = info.FrameSamples * info.FramesPerSuperframe;
                var dropSamples = _skippedSamples < _skipSamples
                    ? Math.Min((ulong)fullSamples, _skipSamples - _skippedSamples)
                    : 0;
                _skippedSamples += dropSamples;
                var availableSamples = fullSamples - (int)dropSamples;
                var writableSamples = _totalSampleLimit == 0
                    ? availableSamples
                    : (int)Math.Min((ulong)availableSamples, _totalSampleLimit > _totalDecodedSamples
                        ? _totalSampleLimit - _totalDecodedSamples
                        : 0);
                var sampleBytes = checked(channels * bytesPerSample);
                if (dropSamples != 0 && writableSamples != 0)
                {
                    output.Slice(written + checked((int)dropSamples) * sampleBytes, writableSamples * sampleBytes)
                        .CopyTo(output.Slice(written, writableSamples * sampleBytes));
                }

                written += writableSamples * sampleBytes;
                _compressedLength = 0;
                _totalDecodedSamples += unchecked((uint)writableSamples);
                frames += unchecked((uint)info.FramesPerSuperframe);

                if (!multipleFrames || (_totalSampleLimit != 0 && _totalDecodedSamples >= _totalSampleLimit))
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

    internal static bool TryDescribeConfig(ReadOnlySpan<byte> configData, Span<byte> info)
    {
        lock (DecoderInitGate)
        {
            if (info.Length < 20 || !TryParseConfig(configData, out var streamInfo, out _, out _))
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(info[0..], unchecked((uint)streamInfo.SuperframeBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(info[4..], unchecked((uint)streamInfo.FramesPerSuperframe));
            BinaryPrimitives.WriteUInt32LittleEndian(info[8..], unchecked((uint)streamInfo.Channels));
            BinaryPrimitives.WriteUInt32LittleEndian(info[12..], unchecked((uint)streamInfo.FrameSamples));
            BinaryPrimitives.WriteUInt32LittleEndian(info[16..], unchecked((uint)streamInfo.BytesPerFrame));
            return true;
        }
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

    private void InitializeDecodersNoLock()
    {
        _decoder = null;
        _extendedDecoders = null;
        if (_decoderConfig is not null)
        {
            var decoder = new Atrac9Decoder();
            decoder.Initialize((byte[])_decoderConfig.Clone());
            _decoder = decoder;
        }
        else if (_extendedDecoderConfig is not null && _info is { } info)
        {
            var decoders = new Atrac9Decoder[info.Channels];
            for (var index = 0; index < decoders.Length; index++)
            {
                decoders[index] = new Atrac9Decoder();
                decoders[index].Initialize((byte[])_extendedDecoderConfig.Clone());
            }

            _extendedDecoders = decoders;
        }
    }

    private bool TryDecodeSuperframeNoLock(int requestedChannels, Atrac9PcmEncoding encoding, Span<byte> output)
    {
        if (_info is not { } info || _planarPcm is null || _compressed is null)
        {
            return false;
        }

        try
        {
            if (_decoder is not null)
            {
                _decoder.Decode(_compressed, _planarPcm);
            }
            else if (_extendedDecoders is not null)
            {
                if (!TryDecodeExtendedNoLock(info))
                {
                    return false;
                }
            }
            else
            {
                output.Clear();
                return true;
            }

            WriteInterleaved(
                _planarPcm,
                output,
                info.FrameSamples * info.FramesPerSuperframe,
                requestedChannels,
                encoding);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException)
        {
            Trace($"decode_exception error={exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private bool TryDecodeExtendedNoLock(Atrac9StreamInfo info)
    {
        // Multichannel block ordering and consumed-byte traversal follow
        // YoteiPC's at9_api.c (GPL-2.0), adapted to the stateful LibAtrac9 API.
        if (_extendedDecoders is null || _planarPcm is null || _compressed is null)
        {
            return false;
        }

        var frames = info.FramesPerSuperframe;
        var position = 0;

        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < info.Channels; channel++)
            {
                if (!TryBeginExtendedBlock(_compressed, frame, ref position))
                {
                    return false;
                }

                var remaining = _compressed.AsSpan(position).ToArray();
                var used = _extendedDecoders[channel].DecodeFrame(
                    remaining,
                    _planarPcm[channel..(channel + 1)],
                    frame * info.FrameSamples,
                    frame);
                if (used <= 0 || used > remaining.Length)
                {
                    return false;
                }

                position += used;
            }
        }

        return true;
    }

    internal static bool TryBeginExtendedBlock(ReadOnlySpan<byte> compressed, int frame, ref int position)
    {
        if (frame < 0 || position < 0 || position > compressed.Length)
        {
            return false;
        }

        if (frame != 0)
        {
            while (position < compressed.Length && (compressed[position] & 0x80) == 0)
            {
                position++;
            }
        }

        return position < compressed.Length;
    }

    private static bool TryParseConfig(
        ReadOnlySpan<byte> configData,
        out Atrac9StreamInfo info,
        out byte[]? decoderConfig,
        out byte[]? extendedDecoderConfig)
    {
        info = default;
        decoderConfig = null;
        extendedDecoderConfig = null;
        if (configData.Length < 4)
        {
            return false;
        }

        var c = configData[..4];
        var sampleRateIndex = c[1] >> 4;
        if (sampleRateIndex >= SampleRates.Length)
        {
            return false;
        }

        if (c[0] == 0xFE)
        {
            var frameBytes = (((c[2] << 8) | c[3]) >> 5) + 1;
            var frames = 1 << ((c[3] >> 3) & 3);
            var layout = (c[1] >> 1) & 7;
            var channels = layout <= 5 ? StandardChannelCounts[layout] : 2;
            var frameSamples = 1 << FrameSamplePowers[sampleRateIndex];
            var normalized = c.ToArray();
            if (layout == 7)
            {
                normalized[1] = (byte)((normalized[1] & ~0x0E) | (2 << 1));
            }
            else if (layout > 5)
            {
                normalized = null!;
            }

            info = new Atrac9StreamInfo(
                channels,
                SampleRates[sampleRateIndex],
                checked(frameBytes * frames),
                frames,
                frameSamples,
                frameBytes);
            decoderConfig = normalized;
            return true;
        }

        if (c[0] != 0x30 || (c[2] & 0xC0) != 0xC0)
        {
            return false;
        }

        var channelsExtended = ((c[1] & 0x0F) + 1) * 4;
        var frameBytesExtended = (((c[2] & 7) << 6) | (c[3] >> 2)) + 1;
        var sfIndex = c[3] & 3;
        var framesExtended = 1 << sfIndex;
        var frameSamplesExtended = 1 << FrameSamplePowers[sampleRateIndex];
        var field = ((frameBytesExtended - 1) << 5) | (sfIndex << 3);
        extendedDecoderConfig =
        [
            0xFE,
            (byte)(sampleRateIndex << 4),
            (byte)(field >> 8),
            (byte)field,
        ];
        info = new Atrac9StreamInfo(
            channelsExtended,
            SampleRates[sampleRateIndex],
            checked(channelsExtended * frameBytesExtended * framesExtended),
            framesExtended,
            frameSamplesExtended,
            frameBytesExtended);
        return true;
    }

    private void Clear()
    {
        _decoder = null;
        _extendedDecoders = null;
        _decoderConfig = null;
        _extendedDecoderConfig = null;
        _info = null;
        _configData = null;
        _compressed = null;
        _planarPcm = null;
        _containerHeader = null;
        _containerHeaderLength = 0;
        _compressedLength = 0;
        _totalDecodedSamples = 0;
        _totalSampleLimit = 0;
        _skippedSamples = 0;
        _skipSamples = 0;
        _decodeErrorLogged = false;
    }

    private void ClearNoLock() => Clear();
}
