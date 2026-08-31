// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.IO;
using System.Threading;
using FFmpeg.AutoGen;

namespace SharpEmu.Libs.Media;
internal sealed unsafe class FfmpegMediaStream : Stream
{
    internal const int AudioSampleRate = 48000;
    internal const int AudioChannels = 2;

    private readonly object _decodeGate = new();
    private readonly bool _isVideo;
    private readonly int _videoWidth;
    private readonly int _videoHeight;

    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private SwrContext* _swrContext;
    private int _streamIndex;

    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _draining;
    private bool _finished;
    private int _disposed;

    private FfmpegMediaStream(bool isVideo, int width, int height)
    {
        _isVideo = isVideo;
        _videoWidth = width;
        _videoHeight = height;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    internal static bool TryOpenVideo(
        string path,
        int width,
        int height,
        out FfmpegMediaStream? stream) =>
        TryOpen(path, AVMediaType.AVMEDIA_TYPE_VIDEO, width, height, out stream);

    internal static bool TryOpenAudio(string path, out FfmpegMediaStream? stream) =>
        TryOpen(path, AVMediaType.AVMEDIA_TYPE_AUDIO, 0, 0, out stream);

    internal bool TrySeekMilliseconds(ulong milliseconds)
    {
        lock (_decodeGate)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                _formatContext is null ||
                _codecContext is null ||
                _streamIndex < 0)
            {
                return false;
            }

            var stream = _formatContext->streams[_streamIndex];
            if (stream is null || stream->time_base.num <= 0 || stream->time_base.den <= 0)
            {
                return false;
            }

            var targetMilliseconds = checked((long)Math.Min(milliseconds, (ulong)long.MaxValue));
            var targetTimestamp = ffmpeg.av_rescale_q(
                targetMilliseconds,
                new AVRational { num = 1, den = 1000 },
                stream->time_base);
            if (ffmpeg.av_seek_frame(
                    _formatContext,
                    _streamIndex,
                    targetTimestamp,
                    ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            {
                return false;
            }

            ffmpeg.avcodec_flush_buffers(_codecContext);
            if (_packet is not null)
            {
                ffmpeg.av_packet_unref(_packet);
            }
            if (_frame is not null)
            {
                ffmpeg.av_frame_unref(_frame);
            }
            if (_swrContext is not null)
            {
                ffmpeg.swr_close(_swrContext);
            }

            _pending = [];
            _pendingOffset = 0;
            _draining = false;
            _finished = false;
            return true;
        }
    }

    private static bool TryOpen(
        string path,
        AVMediaType mediaType,
        int width,
        int height,
        out FfmpegMediaStream? stream)
    {
        stream = null;
        FfmpegRuntime.EnsureInitialized();

        var candidate = new FfmpegMediaStream(mediaType == AVMediaType.AVMEDIA_TYPE_VIDEO, width, height);
        AVFormatContext* formatContext = null;
        try
        {
            if (ffmpeg.avformat_open_input(&formatContext, path, null, null) < 0)
            {
                return false;
            }

            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
            {
                ffmpeg.avformat_close_input(&formatContext);
                return false;
            }

            AVCodec* decoder = null;
            var streamIndex = ffmpeg.av_find_best_stream(formatContext, mediaType, -1, -1, &decoder, 0);
            if (streamIndex < 0 || decoder is null)
            {
                ffmpeg.avformat_close_input(&formatContext);
                return false;
            }

            var codecContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (codecContext is null ||
                ffmpeg.avcodec_parameters_to_context(
                    codecContext,
                    formatContext->streams[streamIndex]->codecpar) < 0 ||
                ffmpeg.avcodec_open2(codecContext, decoder, null) < 0)
            {
                if (codecContext is not null)
                {
                    ffmpeg.avcodec_free_context(&codecContext);
                }

                ffmpeg.avformat_close_input(&formatContext);
                return false;
            }

            candidate._formatContext = formatContext;
            candidate._codecContext = codecContext;
            candidate._streamIndex = streamIndex;
            candidate._frame = ffmpeg.av_frame_alloc();
            candidate._packet = ffmpeg.av_packet_alloc();
            if (candidate._frame is null || candidate._packet is null)
            {
                candidate.Dispose();
                return false;
            }

            stream = candidate;
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][ERROR] in-process decoder failed to open '{path}': {exception.Message}");
            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }

            candidate.Dispose();
            return false;
        }
    }

    internal static bool TryProbe(
        string path,
        out int width,
        out int height,
        out double frameRate,
        out double durationSeconds)
    {
        width = 0;
        height = 0;
        frameRate = 0;
        durationSeconds = 0;
        FfmpegRuntime.EnsureInitialized();

        AVFormatContext* formatContext = null;
        try
        {
            if (ffmpeg.avformat_open_input(&formatContext, path, null, null) < 0)
            {
                return false;
            }

            if (ffmpeg.avformat_find_stream_info(formatContext, null) < 0)
            {
                return false;
            }

            var streamIndex = ffmpeg.av_find_best_stream(
                formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                return false;
            }

            var stream = formatContext->streams[streamIndex];
            width = stream->codecpar->width;
            height = stream->codecpar->height;

            var rate = stream->avg_frame_rate;
            if (rate.den > 0 && rate.num > 0)
            {
                frameRate = (double)rate.num / rate.den;
            }

            if (stream->duration > 0 && stream->time_base.den > 0)
            {
                durationSeconds = stream->duration *
                    ((double)stream->time_base.num / stream->time_base.den);
            }
            else if (formatContext->duration > 0)
            {
                durationSeconds = (double)formatContext->duration / ffmpeg.AV_TIME_BASE;
            }

            return width > 0 && height > 0;
        }
        finally
        {
            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        lock (_decodeGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return 0;
            }

            var written = 0;
            while (written < buffer.Length)
            {
                if (_pendingOffset >= _pending.Length)
                {
                    if (_finished || !TryDecodeIntoPending())
                    {
                        break;
                    }

                    continue;
                }

                var available = _pending.Length - _pendingOffset;
                var take = Math.Min(available, buffer.Length - written);
                _pending.AsSpan(_pendingOffset, take).CopyTo(buffer[written..]);
                _pendingOffset += take;
                written += take;
            }

            return written;
        }
    }

    private bool TryDecodeIntoPending()
    {
        if (!TryReceiveFrame())
        {
            _finished = true;
            return false;
        }

        var produced = _isVideo ? ConvertVideoFrame() : ConvertAudioFrame();
        ffmpeg.av_frame_unref(_frame);
        if (produced is null)
        {
            _finished = true;
            return false;
        }

        _pending = produced;
        _pendingOffset = 0;
        return true;
    }

    private byte[]? ConvertVideoFrame()
    {
        var width = _videoWidth > 0 ? _videoWidth : _frame->width;
        var height = _videoHeight > 0 ? _videoHeight : _frame->height;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        _swsContext = ffmpeg.sws_getCachedContext(
            _swsContext,
            _frame->width,
            _frame->height,
            (AVPixelFormat)_frame->format,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_NV12,
            ffmpeg.SWS_FAST_BILINEAR,
            null,
            null,
            null);
        if (_swsContext is null)
        {
            return null;
        }
        var lumaBytes = width * height;
        var output = new byte[lumaBytes + lumaBytes / 2];
        fixed (byte* outputPointer = output)
        {
            var planes = new byte*[4] { outputPointer, outputPointer + lumaBytes, null, null };
            var strides = new int[4] { width, width, 0, 0 };
            var rows = ffmpeg.sws_scale(
                _swsContext,
                _frame->data,
                _frame->linesize,
                0,
                _frame->height,
                planes,
                strides);
            return rows == height ? output : null;
        }
    }

    private byte[]? ConvertAudioFrame()
    {
        var outputLayout = new AVChannelLayout();
        ffmpeg.av_channel_layout_default(&outputLayout, AudioChannels);

        var inputLayout = _frame->ch_layout;
        SwrContext* swrContext = _swrContext;
        var configureResult = ffmpeg.swr_alloc_set_opts2(
            &swrContext,
            &outputLayout,
            AVSampleFormat.AV_SAMPLE_FMT_S16,
            AudioSampleRate,
            &inputLayout,
            (AVSampleFormat)_frame->format,
            _frame->sample_rate,
            0,
            null);
        _swrContext = swrContext;
        if (configureResult < 0 || _swrContext is null)
        {
            return null;
        }

        if (ffmpeg.swr_is_initialized(_swrContext) == 0 && ffmpeg.swr_init(_swrContext) < 0)
        {
            return null;
        }

        var maxSamples = (int)ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_swrContext, _frame->sample_rate) + _frame->nb_samples,
            AudioSampleRate,
            _frame->sample_rate,
            AVRounding.AV_ROUND_UP);
        if (maxSamples <= 0)
        {
            return null;
        }

        var output = new byte[maxSamples * AudioChannels * sizeof(short)];
        fixed (byte* outputPointer = output)
        {
            var planes = stackalloc byte*[1];
            planes[0] = outputPointer;
            var converted = ffmpeg.swr_convert(
                _swrContext,
                planes,
                maxSamples,
                _frame->extended_data,
                _frame->nb_samples);
            if (converted < 0)
            {
                return null;
            }

            var byteCount = converted * AudioChannels * sizeof(short);
            if (byteCount == output.Length)
            {
                return output;
            }

            var trimmed = new byte[byteCount];
            output.AsSpan(0, byteCount).CopyTo(trimmed);
            return trimmed;
        }
    }

    private bool TryReceiveFrame()
    {
        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult >= 0)
            {
                return true;
            }

            if (receiveResult == ffmpeg.AVERROR_EOF ||
                receiveResult != ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                _draining)
            {
                return false;
            }

            if (!TryFeedPacket())
            {
                return false;
            }
        }
    }

    private bool TryFeedPacket()
    {
        while (true)
        {
            var readResult = ffmpeg.av_read_frame(_formatContext, _packet);
            if (readResult < 0)
            {
                _draining = true;
                return ffmpeg.avcodec_send_packet(_codecContext, null) >= 0;
            }

            if (_packet->stream_index != _streamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            ffmpeg.av_packet_unref(_packet);
            return sendResult >= 0;
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        lock (_decodeGate)
        {
            if (_swsContext is not null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            if (_swrContext is not null)
            {
                var swrContext = _swrContext;
                ffmpeg.swr_free(&swrContext);
                _swrContext = null;
            }

            if (_frame is not null)
            {
                var frame = _frame;
                ffmpeg.av_frame_free(&frame);
                _frame = null;
            }

            if (_packet is not null)
            {
                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (_codecContext is not null)
            {
                var codecContext = _codecContext;
                ffmpeg.avcodec_free_context(&codecContext);
                _codecContext = null;
            }

            if (_formatContext is not null)
            {
                var formatContext = _formatContext;
                ffmpeg.avformat_close_input(&formatContext);
                _formatContext = null;
            }
        }

        base.Dispose(disposing);
    }
}
