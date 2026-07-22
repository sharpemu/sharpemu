// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using FFmpeg.AutoGen;

namespace SharpEmu.Libs.Bink;

/// <summary>
/// Decodes a .bk2 (or any FFmpeg-readable movie) directly via FFmpeg's C API
/// through FFmpeg.AutoGen P/Invoke bindings against the dynamically linked
/// libraries published by github.com/sharpemu/ffmpeg-core -- no native C
/// bridge of our own to build. See docs/bink2-bridge.md.
/// </summary>
internal sealed unsafe class FfmpegNativeBinkFrameSource : IBinkFrameDecoder
{
    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private SwsContext* _swsContext;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private readonly int _videoStreamIndex;
    private bool _draining;
    private int _disposed;

    public uint Width { get; }

    public uint Height { get; }

    public uint FramesPerSecondNumerator { get; }

    public uint FramesPerSecondDenominator { get; }

    private FfmpegNativeBinkFrameSource(
        AVFormatContext* formatContext,
        AVCodecContext* codecContext,
        int videoStreamIndex,
        uint width,
        uint height,
        uint framesPerSecondNumerator,
        uint framesPerSecondDenominator)
    {
        _formatContext = formatContext;
        _codecContext = codecContext;
        _videoStreamIndex = videoStreamIndex;
        Width = width;
        Height = height;
        FramesPerSecondNumerator = framesPerSecondNumerator;
        FramesPerSecondDenominator = framesPerSecondDenominator;
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    private static bool _rootPathInitialized;

    /// <summary>
    /// Points FFmpeg.AutoGen at the FFmpeg shared libraries SharpEmu.CLI
    /// downloads next to the executable (see SharpEmu.CLI.csproj's
    /// FetchFfmpegRuntime target); kept as loose files rather than embedded
    /// in the single-file bundle so the OS loader can resolve the normal
    /// inter-library dependencies (avcodec depends on avutil, etc.) itself.
    /// </summary>
    private static void EnsureRootPathInitialized()
    {
        if (_rootPathInitialized)
        {
            return;
        }

        _rootPathInitialized = true;
        // SharpEmu.CLI.csproj publishes FFmpeg's shared libraries into a
        // "plugins" subfolder next to the executable rather than flat beside
        // it (see NativeLibraryFolderName in SharpEmu.CLI.csproj).
        ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "plugins");

        // ffmpeg's static constructor runs DynamicallyLoadedBindings.Initialize()
        // itself, but that constructor fires on first touch of the ffmpeg type --
        // which is the RootPath assignment above -- so it binds against the
        // default (empty) RootPath before the assignment's own setter body runs.
        // Every function resolved during that first pass permanently throws
        // NotSupportedException. Re-running Initialize() now, with RootPath
        // actually set, rebinds everything against the real search path.
        DynamicallyLoadedBindings.Initialize();
    }

    internal static bool TryOpen(
        string path,
        uint maximumWidth,
        uint maximumHeight,
        out FfmpegNativeBinkFrameSource? source)
    {
        source = null;
        EnsureRootPathInitialized();

        AVFormatContext* formatContext = null;
        AVCodecContext* codecContext = null;
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

            source = new FfmpegNativeBinkFrameSource(
                formatContext,
                codecContext,
                videoStreamIndex,
                outputWidth,
                outputHeight,
                (uint)frameRate.num,
                (uint)frameRate.den);
            formatContext = null;
            codecContext = null;
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

            if (formatContext is not null)
            {
                ffmpeg.avformat_close_input(&formatContext);
            }
        }
    }

    public bool TryDecodeNextFrame(Span<byte> destination)
    {
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
                return true;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
}
