// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using FFmpeg.AutoGen;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Media;

/// <summary>
/// Decodes a .bk2 (or any FFmpeg-readable movie) directly via FFmpeg's C API
/// through FFmpeg.AutoGen P/Invoke bindings against the dynamically linked
/// libraries published by github.com/sharpemu/ffmpeg-core -- no native C
/// bridge of our own to build. See docs/bink2-bridge.md.
/// </summary>
internal sealed unsafe class FfmpegVideoDecoder : IMediaFrameDecoder
{
    private const int OutputAudioChannels = 2;
    private const int OutputAudioBytesPerSample = sizeof(short);

    private readonly object _decodeGate = new();
    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVCodecContext* _audioCodecContext;
    private SwsContext* _swsContext;
    private SwrContext* _swrContext;
    private AVFrame* _frame;
    private AVFrame* _audioFrame;
    private AVPacket* _packet;
    private IHostAudioStream? _audioStream;
    private readonly int _videoStreamIndex;
    private readonly int _audioStreamIndex;
    private readonly int _audioOutputSampleRate;
    private AVChannelLayout _swrInputLayout;
    private AVSampleFormat _swrInputFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;
    private int _swrInputSampleRate;
    private bool _swrInputLayoutValid;
    private bool _draining;
    private bool _audioDraining;
    private bool _audioFailed;
    private int _disposed;

    public uint Width { get; }

    public uint Height { get; }

    public uint FramesPerSecondNumerator { get; }

    public uint FramesPerSecondDenominator { get; }

    private FfmpegVideoDecoder(
        AVFormatContext* formatContext,
        AVCodecContext* codecContext,
        int videoStreamIndex,
        AVCodecContext* audioCodecContext,
        int audioStreamIndex,
        IHostAudioStream? audioStream,
        int audioOutputSampleRate,
        uint width,
        uint height,
        uint framesPerSecondNumerator,
        uint framesPerSecondDenominator)
    {
        _formatContext = formatContext;
        _codecContext = codecContext;
        _videoStreamIndex = videoStreamIndex;
        _audioCodecContext = audioCodecContext;
        _audioStreamIndex = audioStreamIndex;
        _audioStream = audioStream;
        _audioOutputSampleRate = audioOutputSampleRate;
        Width = width;
        Height = height;
        FramesPerSecondNumerator = framesPerSecondNumerator;
        FramesPerSecondDenominator = framesPerSecondDenominator;
        _frame = ffmpeg.av_frame_alloc();
        _audioFrame = audioCodecContext is null ? null : ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    private static void EnsureRootPathInitialized() =>
        SharpEmu.Libs.Media.FfmpegRuntime.EnsureInitialized();

    internal static bool TryOpen(
        string path,
        uint maximumWidth,
        uint maximumHeight,
        out FfmpegVideoDecoder? source,
        bool enableAudio = true)
    {
        source = null;
        EnsureRootPathInitialized();

        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
        AVCodecContext* audioCodecContext = null;
        IHostAudioStream? audioStream = null;
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

            AVCodec* decoder = null;
            var videoStreamIndex = ffmpeg.av_find_best_stream(
                formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
            if (videoStreamIndex < 0 || decoder is null)
            {
                return false;
            }

            var stream = formatContext->streams[videoStreamIndex];
            codecContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (codecContext is null)
            {
                return false;
            }

            if (ffmpeg.avcodec_parameters_to_context(codecContext, stream->codecpar) < 0)
            {
                return false;
            }

            codecContext->thread_count = 0;
            codecContext->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
            if (ffmpeg.avcodec_open2(codecContext, decoder, null) < 0)
            {
                return false;
            }

            if (codecContext->width <= 0 || codecContext->height <= 0)
            {
                return false;
            }

            var frameRate = ffmpeg.av_guess_frame_rate(formatContext, stream, null);
            if (frameRate.num <= 0 || frameRate.den <= 0)
            {
                frameRate = stream->avg_frame_rate;
            }
            if (frameRate.num <= 0 || frameRate.den <= 0)
            {
                frameRate = stream->r_frame_rate;
            }
            if (frameRate.num <= 0 || frameRate.den <= 0)
            {
                frameRate = new AVRational { num = 30, den = 1 };
            }

            var audioStreamIndex = -1;
            var audioOutputSampleRate = 0;
            if (enableAudio)
            {
                audioStreamIndex = TryOpenAudioDecoder(
                    formatContext,
                    out audioCodecContext,
                    out audioOutputSampleRate);
                if (audioStreamIndex >= 0 && audioCodecContext is not null)
                {
                    try
                    {
                        audioStream = HostPlatform.Current.Audio.OpenStereoPcm16Stream(
                            checked((uint)audioOutputSampleRate));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or
                                                         ArgumentOutOfRangeException)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][WARN] Bink audio output unavailable: {exception.Message}");
                        ffmpeg.avcodec_free_context(&audioCodecContext);
                        audioStreamIndex = -1;
                        audioOutputSampleRate = 0;
                    }
                }
            }

            var outputWidth = (uint)codecContext->width;
            var outputHeight = (uint)codecContext->height;
            if (maximumWidth > 0 && maximumHeight > 0 &&
                (outputWidth > maximumWidth || outputHeight > maximumHeight))
            {
                if ((ulong)outputWidth * maximumHeight > (ulong)outputHeight * maximumWidth)
                {
                    outputHeight = (uint)((ulong)outputHeight * maximumWidth / outputWidth);
                    outputWidth = maximumWidth;
                }
                else
                {
                    outputWidth = (uint)((ulong)outputWidth * maximumHeight / outputHeight);
                    outputHeight = maximumHeight;
                }

                outputWidth = Math.Max(1, outputWidth);
                outputHeight = Math.Max(1, outputHeight);
            }

            source = new FfmpegVideoDecoder(
                formatContext,
                codecContext,
                videoStreamIndex,
                audioCodecContext,
                audioStreamIndex,
                audioStream,
                audioOutputSampleRate,
                outputWidth,
                outputHeight,
                (uint)frameRate.num,
                (uint)frameRate.den);
            formatContext = null;
            codecContext = null;
            audioCodecContext = null;
            audioStream = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (codecContext is not null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
            }

            if (audioCodecContext is not null)
            {
                ffmpeg.avcodec_free_context(&audioCodecContext);
            }

            audioStream?.Dispose();

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }

    private static int TryOpenAudioDecoder(
        AVFormatContext* formatContext,
        out AVCodecContext* codecContext,
        out int outputSampleRate)
    {
        codecContext = null;
        outputSampleRate = 0;

        AVCodec* decoder = null;
        var streamIndex = ffmpeg.av_find_best_stream(
            formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &decoder, 0);
        if (streamIndex < 0 || decoder is null)
        {
            return -1;
        }

        var candidate = ffmpeg.avcodec_alloc_context3(decoder);
        if (candidate is null)
        {
            return -1;
        }

        var stream = formatContext->streams[streamIndex];
        if (ffmpeg.avcodec_parameters_to_context(candidate, stream->codecpar) < 0)
        {
            ffmpeg.avcodec_free_context(&candidate);
            return -1;
        }

        candidate->thread_count = 0;
        candidate->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
        if (ffmpeg.avcodec_open2(candidate, decoder, null) < 0)
        {
            ffmpeg.avcodec_free_context(&candidate);
            return -1;
        }

        outputSampleRate = candidate->sample_rate > 0 ? candidate->sample_rate : 48_000;
        codecContext = candidate;
        return streamIndex;
    }

    public bool TryDecodeNextFrame(Span<byte> destination)
    {
        lock (_decodeGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var stride = checked((int)(Width * 4));
            var required = (long)stride * Height;
            if (destination.Length < required)
            {
                return false;
            }

            if (!TryReceiveFrame())
            {
                return false;
            }

            _swsContext = ffmpeg.sws_getCachedContext(
                _swsContext,
                _frame->width,
                _frame->height,
                (AVPixelFormat)_frame->format,
                (int)Width,
                (int)Height,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                ffmpeg.SWS_FAST_BILINEAR,
                null,
                null,
                null);
            if (_swsContext is null)
            {
                ffmpeg.av_frame_unref(_frame);
                return false;
            }

            fixed (byte* destinationPointer = destination)
            {
                var destinationPlanes = new byte*[4] { destinationPointer, null, null, null };
                var destinationStrides = new int[4] { stride, 0, 0, 0 };
                var convertedRows = ffmpeg.sws_scale(
                    _swsContext,
                    _frame->data,
                    _frame->linesize,
                    0,
                    _frame->height,
                    destinationPlanes,
                    destinationStrides);
                ffmpeg.av_frame_unref(_frame);
                return convertedRows == (int)Height;
            }
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

            if (receiveResult == ffmpeg.AVERROR_EOF)
            {
                return false;
            }

            if (receiveResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                return false;
            }

            if (_draining)
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
                ffmpeg.avcodec_send_packet(_codecContext, null);
                DrainAudioDecoder();
                return true;
            }

            if (_packet->stream_index == _audioStreamIndex)
            {
                DecodeAudioPacket(_packet);
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            if (_packet->stream_index != _videoStreamIndex)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            ffmpeg.av_packet_unref(_packet);
            if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                return false;
            }

            return true;
        }
    }

    private void DecodeAudioPacket(AVPacket* packet)
    {
        if (_audioCodecContext is null || _audioFrame is null || _audioFailed)
        {
            return;
        }

        var sendResult = ffmpeg.avcodec_send_packet(_audioCodecContext, packet);
        if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            DrainAvailableAudioFrames();
            sendResult = ffmpeg.avcodec_send_packet(_audioCodecContext, packet);
        }

        if (sendResult < 0)
        {
            DisableAudio("packet decode failed");
            return;
        }

        DrainAvailableAudioFrames();
    }

    private void DrainAudioDecoder()
    {
        if (_audioCodecContext is null || _audioFrame is null ||
            _audioDraining || _audioFailed)
        {
            return;
        }

        _audioDraining = true;
        var sendResult = ffmpeg.avcodec_send_packet(_audioCodecContext, null);
        if (sendResult >= 0 || sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            DrainAvailableAudioFrames();
        }
    }

    private void DrainAvailableAudioFrames()
    {
        while (_audioCodecContext is not null && _audioFrame is not null)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(_audioCodecContext, _audioFrame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                receiveResult == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            if (receiveResult < 0)
            {
                DisableAudio("frame decode failed");
                return;
            }

            if (!SubmitAudioFrame())
            {
                ffmpeg.av_frame_unref(_audioFrame);
                DisableAudio("host submission failed");
                return;
            }

            ffmpeg.av_frame_unref(_audioFrame);
        }
    }

    private bool SubmitAudioFrame()
    {
        if (_audioStream is null || _audioFrame is null ||
            _audioFrame->nb_samples <= 0 || _audioFrame->extended_data is null)
        {
            return true;
        }

        var sampleRate = _audioFrame->sample_rate > 0
            ? _audioFrame->sample_rate
            : _audioCodecContext->sample_rate;
        if (sampleRate <= 0)
        {
            return false;
        }

        var inputLayout = _audioFrame->ch_layout;
        var ownsInputLayout = false;
        if (ffmpeg.av_channel_layout_check(&inputLayout) == 0)
        {
            inputLayout = _audioCodecContext->ch_layout;
        }
        if (ffmpeg.av_channel_layout_check(&inputLayout) == 0)
        {
            ffmpeg.av_channel_layout_default(
                &inputLayout,
                Math.Max(1, _audioFrame->ch_layout.nb_channels));
            ownsInputLayout = true;
        }

        try
        {
            if (!EnsureAudioResampler(
                    &inputLayout,
                    (AVSampleFormat)_audioFrame->format,
                    sampleRate))
            {
                return false;
            }

            var maximumSamples = ffmpeg.swr_get_out_samples(
                _swrContext, _audioFrame->nb_samples);
            if (maximumSamples <= 0)
            {
                return true;
            }

            var outputBytes = checked(
                maximumSamples * OutputAudioChannels * OutputAudioBytesPerSample);
            var buffer = ArrayPool<byte>.Shared.Rent(outputBytes);
            try
            {
                fixed (byte* output = buffer)
                {
                    var outputPlanes = stackalloc byte*[1];
                    outputPlanes[0] = output;
                    var convertedSamples = ffmpeg.swr_convert(
                        _swrContext,
                        outputPlanes,
                        maximumSamples,
                        _audioFrame->extended_data,
                        _audioFrame->nb_samples);
                    if (convertedSamples < 0)
                    {
                        return false;
                    }

                    var convertedBytes = checked(
                        convertedSamples * OutputAudioChannels * OutputAudioBytesPerSample);
                    return _audioStream.Submit(buffer.AsSpan(0, convertedBytes));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            if (ownsInputLayout)
            {
                ffmpeg.av_channel_layout_uninit(&inputLayout);
            }
        }
    }

    private bool EnsureAudioResampler(
        AVChannelLayout* inputLayout,
        AVSampleFormat inputFormat,
        int inputSampleRate)
    {
        var storedInputLayout = _swrInputLayout;
        if (_swrContext is not null &&
            _swrInputFormat == inputFormat &&
            _swrInputSampleRate == inputSampleRate &&
            ffmpeg.av_channel_layout_compare(&storedInputLayout, inputLayout) == 0)
        {
            return true;
        }

        FreeAudioResampler();

        AVChannelLayout copiedInputLayout = default;
        if (ffmpeg.av_channel_layout_copy(&copiedInputLayout, inputLayout) < 0)
        {
            return false;
        }

        AVChannelLayout outputLayout = default;
        ffmpeg.av_channel_layout_default(&outputLayout, OutputAudioChannels);
        SwrContext* context = null;
        var allocateResult = ffmpeg.swr_alloc_set_opts2(
            &context,
            &outputLayout,
            AVSampleFormat.AV_SAMPLE_FMT_S16,
            _audioOutputSampleRate,
            &copiedInputLayout,
            inputFormat,
            inputSampleRate,
            0,
            null);
        ffmpeg.av_channel_layout_uninit(&outputLayout);
        if (allocateResult < 0 || context is null || ffmpeg.swr_init(context) < 0)
        {
            if (context is not null)
            {
                ffmpeg.swr_free(&context);
            }
            ffmpeg.av_channel_layout_uninit(&copiedInputLayout);
            return false;
        }

        _swrContext = context;
        _swrInputLayout = copiedInputLayout;
        _swrInputLayoutValid = true;
        _swrInputFormat = inputFormat;
        _swrInputSampleRate = inputSampleRate;
        return true;
    }

    private void DisableAudio(string reason)
    {
        if (_audioFailed)
        {
            return;
        }

        _audioFailed = true;
        Console.Error.WriteLine($"[LOADER][WARN] Bink audio disabled: {reason}.");
        FreeAudioResampler();
        _audioStream?.Dispose();
        _audioStream = null;
    }

    private void FreeAudioResampler()
    {
        if (_swrContext is not null)
        {
            var context = _swrContext;
            ffmpeg.swr_free(&context);
            _swrContext = null;
        }

        if (_swrInputLayoutValid)
        {
            var inputLayout = _swrInputLayout;
            ffmpeg.av_channel_layout_uninit(&inputLayout);
            _swrInputLayout = default;
            _swrInputLayoutValid = false;
        }

        _swrInputFormat = AVSampleFormat.AV_SAMPLE_FMT_NONE;
        _swrInputSampleRate = 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_decodeGate)
        {
            FreeAudioResampler();
            _audioStream?.Dispose();
            _audioStream = null;

            if (_swsContext is not null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            if (_packet is not null)
            {
                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (_frame is not null)
            {
                var frame = _frame;
                ffmpeg.av_frame_free(&frame);
                _frame = null;
            }

            if (_audioFrame is not null)
            {
                var frame = _audioFrame;
                ffmpeg.av_frame_free(&frame);
                _audioFrame = null;
            }

            if (_codecContext is not null)
            {
                var codecContext = _codecContext;
                ffmpeg.avcodec_free_context(&codecContext);
                _codecContext = null;
            }

            if (_audioCodecContext is not null)
            {
                var codecContext = _audioCodecContext;
                ffmpeg.avcodec_free_context(&codecContext);
                _audioCodecContext = null;
            }

            if (_formatContext is not null)
            {
                var formatContext = _formatContext;
                ffmpeg.avformat_close_input(&formatContext);
                _formatContext = null;
            }
        }
    }
}
