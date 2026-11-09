// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using FFmpeg.AutoGen;
using SharpEmu.Libs.Media;
using static SharpEmu.Libs.Audio.Atrac9DecodeState;

namespace SharpEmu.Libs.Audio;

internal sealed unsafe class AjmOpusDecoder(int maxChannels, Atrac9PcmEncoding encoding) : IDisposable
{
    private const int MaximumPcmBytes = 64 * 1024 * 1024;
    private readonly object _gate = new();
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _resampler;
    private byte[] _pcm = [];
    private int _pcmOffset;
    private int _pcmLength;
    private float _ratio = 1;
    private bool _end;
    private bool _resampling;
    private bool _disposed;
    private Atrac9Gapless _gaplessInitial;
    private Atrac9Gapless _gapless;
    private ulong _totalSamples;

    public int Channels { get; private set; }
    public int SampleRate { get; private set; }
    public uint FramesPerPacket { get; private set; }
    public (float Ratio, int RemainingSamples) ResampleInfo
    {
        get
        {
            lock (_gate)
            {
                var buffered = Channels == 0 ? 0 : (_pcmLength - _pcmOffset) / (Channels * SampleBytes);
                var delay = _resampler == null ? 0 : Math.Max(0, ffmpeg.swr_get_delay(_resampler, SampleRate));
                return (_ratio, (int)Math.Min(int.MaxValue, buffered + delay));
            }
        }
    }
    public Atrac9Gapless Gapless { get { lock (_gate) return _gapless; } }
    private bool AtEnd => _gaplessInitial.TotalSamples is not 0 and not uint.MaxValue && _gapless.TotalSamples == 0;
    private int SampleBytes => encoding == Atrac9PcmEncoding.Signed16 ? 2 : 4;

    public bool TryInitialize(ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length < 12)
            return false;
        var channels = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(parameters[4..]);
        var mapping = BinaryPrimitives.ReadUInt32LittleEndian(parameters[8..]);
        // shortcut: Only mapping-family 0 (mono/stereo) is supported; add multistream decoding for surround.
        if (channels is < 1 or > 2 || channels > maxChannels || sampleRate != 48000 || mapping != 0)
            return false;

        lock (_gate)
        {
            if (_disposed)
                return false;
            ReleaseCodec();
            try
            {
                FfmpegRuntime.EnsureInitialized();
                var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_OPUS);
                if (codec == null)
                    return false;
                _context = ffmpeg.avcodec_alloc_context3(codec);
                _packet = ffmpeg.av_packet_alloc();
                _frame = ffmpeg.av_frame_alloc();
                if (_context == null || _packet == null || _frame == null)
                {
                    ReleaseCodec();
                    return false;
                }
                ffmpeg.av_channel_layout_default(&_context->ch_layout, (int)channels);
                _context->sample_rate = (int)sampleRate;
                if (ffmpeg.avcodec_open2(_context, codec, null) < 0)
                {
                    ReleaseCodec();
                    return false;
                }
                Channels = (int)channels;
                SampleRate = (int)sampleRate;
                Reset();
                return true;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
            {
                ReleaseCodec();
                return false;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_context != null)
                ffmpeg.avcodec_flush_buffers(_context);
            ResetResampler();
            _totalSamples = 0;
            FramesPerPacket = 0;
            _gapless = _gaplessInitial;
            _end = false;
        }
    }

    public void SetGapless(uint totalSamples, ushort skipSamples, bool reset)
    {
        lock (_gate)
        {
            _gaplessInitial = new Atrac9Gapless(totalSamples, skipSamples, 0);
            if (reset || _gapless == default)
                _gapless = _gaplessInitial;
        }
    }

    public void SetResampleParameters(float ratio, uint flags)
    {
        lock (_gate)
        {
            if ((flags & 1) != 0)
                ResetResampler();
            if ((flags & 8) == 0 && _ratio != ratio)
            {
                _ratio = ratio;
            }
            _end = (flags & 2) != 0;
        }
    }

    public Atrac9DecodeResult Decode(ReadOnlySpan<byte> input, Span<byte> output, bool multipleFrames)
    {
        lock (_gate)
        {
            if (_context == null || _disposed)
                return new(ResultNotInitialized, 0, 0, _totalSamples, 0);
            var consumed = 0;
            var written = 0;
            var status = 0;
            var frames = 0u;
            var blockBytes = Channels * SampleBytes;
            output = output[..(output.Length / blockBytes * blockBytes)];
            if (output.IsEmpty)
                return new(ResultNotEnoughRoom, 0, 0, _totalSamples, 0);
            if (_ratio == 0)
                return new(0, 0, 0, _totalSamples, 0);

            var planes = stackalloc byte*[2];
            while (written < output.Length)
            {
                var copy = Math.Min(_pcmLength - _pcmOffset, output.Length - written);
                _pcm.AsSpan(_pcmOffset, copy).CopyTo(output[written..]);
                written += copy;
                _pcmOffset += copy;
                if (_pcmOffset < _pcmLength || (!multipleFrames && frames != 0))
                    break;
                _pcmLength = _pcmOffset = 0;
                if (consumed == input.Length || AtEnd)
                {
                    if (_end && _resampler != null && ConvertPcm(null, 0))
                        continue;
                    if (consumed == 0 && written == 0 && !AtEnd)
                        status = ResultPartialInput;
                    break;
                }
                status = ReadPacket(input[consumed..], out var size, out var packetFrames);
                if (status != 0)
                    break;
                ffmpeg.av_packet_unref(_packet);
                ffmpeg.av_frame_unref(_frame);
                if (ffmpeg.av_new_packet(_packet, size) < 0)
                {
                    status = ResultCodecError;
                    break;
                }
                input.Slice(consumed + 2, size).CopyTo(new Span<byte>(_packet->data, size));
                if (ffmpeg.avcodec_send_packet(_context, _packet) < 0 ||
                    ffmpeg.avcodec_receive_frame(_context, _frame) < 0 ||
                    _frame->ch_layout.nb_channels != Channels || !PrepareResampler())
                {
                    status = ResultCodecError | ResultInvalidData;
                    break;
                }

                var skipped = Math.Min((int)_gapless.SkipSamples, _frame->nb_samples);
                var count = _frame->nb_samples - skipped;
                if (_gaplessInitial.TotalSamples is not 0 and not uint.MaxValue)
                    count = (int)Math.Min((uint)count, _gapless.TotalSamples);
                var planar = ffmpeg.av_sample_fmt_is_planar((AVSampleFormat)_frame->format) != 0;
                var sampleBytes = ffmpeg.av_get_bytes_per_sample((AVSampleFormat)_frame->format);
                for (var plane = 0; plane < (planar ? Channels : 1); plane++)
                    planes[plane] = _frame->extended_data[plane] + skipped * sampleBytes * (planar ? 1 : Channels);
                if (count != 0 && !ConvertPcm(planes, count))
                {
                    status = ResultCodecError;
                    break;
                }
                consumed += size + 2;
                frames += packetFrames;
                FramesPerPacket = packetFrames;
                _totalSamples += (uint)count;
                _gapless = _gapless with
                {
                    SkipSamples = (ushort)(_gapless.SkipSamples - skipped),
                    SkippedSamples = (ushort)Math.Min(ushort.MaxValue, _gapless.SkippedSamples + skipped),
                    TotalSamples = _gaplessInitial.TotalSamples is not 0 and not uint.MaxValue
                        ? _gapless.TotalSamples - (uint)count : _gapless.TotalSamples,
                };
            }
            return new(status, consumed, written, _totalSamples, frames);
        }
    }

    internal static int ReadPacket(ReadOnlySpan<byte> input, out int size, out uint frames)
    {
        size = 0;
        frames = 0;
        if (input.Length < 2)
            return ResultPartialInput;
        size = BinaryPrimitives.ReadUInt16LittleEndian(input);
        if (size == 0)
            return ResultCodecError | ResultInvalidData;
        if (size > input.Length - 2)
            return ResultPartialInput;
        var packet = input.Slice(2, size);
        if ((packet[0] & 3) == 3 && size < 2)
            return ResultCodecError | ResultInvalidData;
        frames = (packet[0] & 3) switch { 0 => 1, 3 => (uint)(packet[1] & 63), _ => 2 };
        var config = packet[0] >> 3;
        var samples = config >= 16 ? 120 << (config & 3)
            : config >= 12 ? 480 << (config & 1)
            : (config & 3) == 3 ? 2880 : 480 << (config & 3);
        return frames is < 1 or > 6 || samples > 960
            ? ResultCodecError | ResultInvalidData : 0;
    }

    private bool PrepareResampler()
    {
        if (_resampler != null)
            return ApplyRatio();
        var layout = _frame->ch_layout;
        SwrContext* resampler = null;
        var format = encoding switch
        {
            Atrac9PcmEncoding.Signed16 => AVSampleFormat.AV_SAMPLE_FMT_S16,
            Atrac9PcmEncoding.Signed32 => AVSampleFormat.AV_SAMPLE_FMT_S32,
            _ => AVSampleFormat.AV_SAMPLE_FMT_FLT,
        };
        if (ffmpeg.swr_alloc_set_opts2(&resampler, &layout, format, SampleRate,
            &layout, (AVSampleFormat)_frame->format, SampleRate, 0, null) < 0 || resampler == null)
        {
            if (resampler != null) ffmpeg.swr_free(&resampler);
            return false;
        }
        if (ffmpeg.swr_init(resampler) < 0)
        {
            ffmpeg.swr_free(&resampler);
            return false;
        }
        _resampler = resampler;
        return ApplyRatio();
    }

    private bool ApplyRatio()
    {
        if (_ratio != 1 || _resampling)
        {
            // Renew before each packet so a long voice never outlives the compensation window.
            const int distance = 1 << 20;
            _resampling = true;
            return ffmpeg.swr_set_compensation(_resampler, (int)Math.Round(distance * (1 - _ratio)), distance) >= 0;
        }
        return true;
    }

    private bool ConvertPcm(byte** input, int samples)
    {
        var capacity = ffmpeg.swr_get_out_samples(_resampler, samples);
        var blockBytes = Channels * SampleBytes;
        if (capacity <= 0 || capacity > MaximumPcmBytes / blockBytes)
            return false;
        if (_pcm.Length < capacity * blockBytes)
            Array.Resize(ref _pcm, capacity * blockBytes);
        fixed (byte* pcm = _pcm)
        {
            var plane = pcm;
            var converted = ffmpeg.swr_convert(_resampler, &plane, capacity, input, samples);
            _pcmOffset = 0;
            _pcmLength = Math.Max(converted, 0) * blockBytes;
            return converted >= 0 && (input != null || converted > 0);
        }
    }

    private void ResetResampler()
    {
        var resampler = _resampler;
        if (resampler != null)
            ffmpeg.swr_free(&resampler);
        _resampler = null;
        _resampling = false;
        _pcmOffset = _pcmLength = 0;
    }

    private void ReleaseCodec()
    {
        ResetResampler();
        var context = _context;
        var frame = _frame;
        var packet = _packet;
        if (context != null) ffmpeg.avcodec_free_context(&context);
        if (frame != null) ffmpeg.av_frame_free(&frame);
        if (packet != null) ffmpeg.av_packet_free(&packet);
        _context = null;
        _frame = null;
        _packet = null;
        Channels = SampleRate = 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ReleaseCodec();
            _disposed = true;
        }
    }
}
