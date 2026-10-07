// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading.Channels;
using FFmpeg.AutoGen;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Codec;

/// <summary>
/// Owns one FFmpeg H.264 decode session for a single sceVideodec2 decoder
/// handle, feeding it pre-demuxed Annex-B access units from guest memory.
///
/// Three-stage pipeline, none of it on the guest thread:
///   Decode() -> AU queue -> decode worker -> frame queue -> scheduler -> Submit
///
/// The scheduler paces presentation to the stream's own framerate (no PTS
/// is available) instead of draining as fast as it decodes. Neither worker
/// thread may write to guest memory directly (the guest's stack slot may
/// already be reused by the time they finish), so readiness is reported via
/// TryConsumeProtocolReadySignal (metadata only) while pixels go straight
/// to VulkanVideoPresenter.Submit from the scheduler thread.
/// </summary>
internal sealed unsafe class Videodec2Decoder : IDisposable
{
    // BGRA matches VulkanVideoPresenter.Submit; decode bypasses guest memory entirely.
    private const AVPixelFormat OutputPixelFormat = AVPixelFormat.AV_PIX_FMT_BGRA;

    // Enough lookahead to absorb decode jitter without adding visible latency.
    private const int FrameQueueCapacity = 4;

    // Fallback when the stream doesn't declare a usable framerate.
    private const double FallbackFps = 30.0;

    private static bool _rootPathInitialized;
    private static readonly object InitGate = new();

    private readonly object _gate = new();
    private AVCodecContext* _codecContext;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _swsContext;
    private int _swsSourceWidth;
    private int _swsSourceHeight;
    private AVPixelFormat _swsSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _disposed;

    // Unbounded: access units are small, backpressure lives on the frame queue below.
    private readonly Channel<byte[]?> _workChannel =
        Channel.CreateUnbounded<byte[]?>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

    // Bounded and blocking-on-full: the backpressure that keeps decode paced to playback.
    private readonly Channel<(byte[] Bgra, uint Width, uint Height)> _frameQueue =
        Channel.CreateBounded<(byte[], uint, uint)>(new BoundedChannelOptions(FrameQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly Thread _worker;
    private readonly Thread _scheduler;

    // Cancelled (not just completed) on Dispose so both loops stop promptly instead of draining a backlog.
    private readonly CancellationTokenSource _workerCts = new();

    private readonly object _protocolGate = new();
    private long _producedCount;
    private long _reportedCount;
    private uint _lastWidth;
    private uint _lastHeight;

    private Videodec2Decoder(AVCodecContext* codecContext, AVFrame* frame, AVPacket* packet)
    {
        _codecContext = codecContext;
        _frame = frame;
        _packet = packet;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SharpEmu Videodec2 Worker",
        };
        _scheduler = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "SharpEmu Videodec2 Scheduler",
        };
        _worker.Start();
        _scheduler.Start();
    }

    /// <summary>Opens a new H.264 session, or null if FFmpeg is unavailable or the decoder couldn't open.</summary>
    public static Videodec2Decoder? TryCreate()
    {
        EnsureRootPathInitialized();

        AVCodecContext* codecContext = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        try
        {
            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
            {
                return null;
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                return null;
            }

            if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
            {
                ffmpeg.avcodec_free_context(&codecContext);
                return null;
            }

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            if (frame == null || packet == null)
            {
                if (frame != null)
                {
                    ffmpeg.av_frame_free(&frame);
                }

                if (packet != null)
                {
                    ffmpeg.av_packet_free(&packet);
                }

                ffmpeg.avcodec_free_context(&codecContext);
                return null;
            }

            return new Videodec2Decoder(codecContext, frame, packet);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            // FFmpeg's native libraries are optional; missing ones degrade to the stub, not a crash.
            if (codecContext != null)
            {
                ffmpeg.avcodec_free_context(&codecContext);
            }

            return null;
        }
    }

    private static void EnsureRootPathInitialized()
    {
        if (_rootPathInitialized)
        {
            return;
        }

        lock (InitGate)
        {
            if (_rootPathInitialized)
            {
                return;
            }

            _rootPathInitialized = true;
            // Must be set before any ffmpeg.* call, or bindings resolve against the empty default RootPath.
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "plugins");
            DynamicallyLoadedBindings.Initialize();
        }
    }

    /// <summary>Hands one Annex-B access unit to the decode worker and returns immediately.</summary>
    public void EnqueueAccessUnit(byte[] accessUnit)
    {
        _workChannel.Writer.TryWrite(accessUnit);
    }

    /// <summary>Queues an end-of-stream drain: flush FFmpeg and emit one more buffered picture, if any.</summary>
    public void RequestDrain()
    {
        _workChannel.Writer.TryWrite(null);
    }

    /// <summary>Non-blocking: true exactly once per frame the worker has produced, in order.</summary>
    public bool TryConsumeProtocolReadySignal(out uint width, out uint height)
    {
        lock (_protocolGate)
        {
            if (_reportedCount >= _producedCount)
            {
                width = 0;
                height = 0;
                return false;
            }

            _reportedCount++;
            width = _lastWidth;
            height = _lastHeight;
            return true;
        }
    }

    private void WorkerLoop()
    {
        var reader = _workChannel.Reader;
        var token = _workerCts.Token;
        while (true)
        {
            byte[]? item;
            try
            {
                if (!reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
                {
                    return;
                }

                if (!reader.TryRead(out item))
                {
                    continue;
                }
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var decodedOk = item is null
                ? DrainCoreLocked(out var bgraFrame, out var hasPicture, out var width, out var height)
                : DecodeCoreLocked(item, out bgraFrame, out hasPicture, out width, out height);

            if (!decodedOk || !hasPicture || bgraFrame is null)
            {
                continue;
            }

            try
            {
                // Blocks if the scheduler hasn't kept up; deliberate backpressure.
                _frameQueue.Writer.WriteAsync((bgraFrame, width, height), token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            lock (_protocolGate)
            {
                _producedCount++;
                _lastWidth = width;
                _lastHeight = height;
            }
        }
    }

    private void SchedulerLoop()
    {
        var reader = _frameQueue.Reader;
        var token = _workerCts.Token;
        var haveDeadline = false;
        var nextDeadline = DateTime.MinValue;
        var frameInterval = TimeSpan.FromSeconds(1.0 / FallbackFps);

        while (true)
        {
            (byte[] Bgra, uint Width, uint Height) item;
            try
            {
                if (!reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
                {
                    return;
                }

                if (!reader.TryRead(out item))
                {
                    continue;
                }
            }
            catch (ChannelClosedException)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!haveDeadline)
            {
                // Framerate isn't known until FFmpeg parses the first frame's SPS/VUI.
                var rate = _codecContext->framerate;
                var fps = rate.den > 0 && rate.num > 0
                    ? (double)rate.num / rate.den
                    : FallbackFps;
                frameInterval = TimeSpan.FromSeconds(1.0 / fps);
                nextDeadline = DateTime.UtcNow;
                haveDeadline = true;
            }

            var now = DateTime.UtcNow;
            if (nextDeadline > now)
            {
                try
                {
                    Task.Delay(nextDeadline - now, token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            VulkanVideoPresenter.Submit(item.Bgra, item.Width, item.Height);
            nextDeadline += frameInterval;

            // Resync to "now" if we fell behind, instead of burning through a deadline backlog unpaced.
            if (nextDeadline < DateTime.UtcNow)
            {
                nextDeadline = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Feeds one access unit and converts the resulting picture to BGRA, if any. Decode-worker thread only.</summary>
    private bool DecodeCoreLocked(
        byte[] accessUnit,
        out byte[]? bgraFrame,
        out bool hasPicture,
        out uint width,
        out uint height)
    {
        bgraFrame = null;
        hasPicture = false;
        width = 0;
        height = 0;

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            ffmpeg.av_packet_unref(_packet);
            var buffer = ffmpeg.av_malloc((nuint)accessUnit.Length + (nuint)ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE);
            if (buffer == null)
            {
                return false;
            }

            fixed (byte* source = accessUnit)
            {
                Buffer.MemoryCopy(source, buffer, accessUnit.Length, accessUnit.Length);
            }

            new Span<byte>((byte*)buffer + accessUnit.Length, ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE).Clear();

            _packet->data = (byte*)buffer;
            _packet->size = accessUnit.Length;

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            ffmpeg.av_freep(&buffer);
            _packet->data = null;
            _packet->size = 0;
            if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                return false;
            }

            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                return true;
            }

            if (receiveResult < 0)
            {
                return false;
            }

            try
            {
                bgraFrame = ConvertFrameToBgraLocked(out width, out height);
                if (bgraFrame == null)
                {
                    return false;
                }

                hasPicture = true;
                return true;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>Signals end-of-stream and pulls one remaining buffered frame, if any. Decode-worker thread only.</summary>
    private bool DrainCoreLocked(out byte[]? bgraFrame, out bool hasPicture, out uint width, out uint height)
    {
        bgraFrame = null;
        hasPicture = false;
        width = 0;
        height = 0;

        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, null);
            if (sendResult < 0 && sendResult != ffmpeg.AVERROR_EOF)
            {
                return false;
            }

            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receiveResult == ffmpeg.AVERROR_EOF)
            {
                return true;
            }

            if (receiveResult < 0)
            {
                return false;
            }

            try
            {
                bgraFrame = ConvertFrameToBgraLocked(out width, out height);
                if (bgraFrame == null)
                {
                    return false;
                }

                hasPicture = true;
                return true;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    /// <summary>Converts <see cref="_frame"/> to a tightly packed width*height*4 BGRA buffer, or null on failure.</summary>
    private byte[]? ConvertFrameToBgraLocked(out uint width, out uint height)
    {
        width = (uint)_frame->width;
        height = (uint)_frame->height;
        var sourceFormat = (AVPixelFormat)_frame->format;

        if (_swsContext == null ||
            _swsSourceWidth != _frame->width ||
            _swsSourceHeight != _frame->height ||
            _swsSourceFormat != sourceFormat)
        {
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
            }

            _swsContext = ffmpeg.sws_getContext(
                _frame->width, _frame->height, sourceFormat,
                _frame->width, _frame->height, OutputPixelFormat,
                ffmpeg.SWS_BILINEAR, null, null, null);
            if (_swsContext == null)
            {
                return null;
            }

            _swsSourceWidth = _frame->width;
            _swsSourceHeight = _frame->height;
            _swsSourceFormat = sourceFormat;
        }

        var bgraFrame = new byte[checked((int)(width * height * 4))];
        fixed (byte* destinationPtr = bgraFrame)
        {
            var dstData = new byte_ptrArray4();
            var dstLinesize = new int_array4();
            ffmpeg.av_image_fill_arrays(
                ref dstData, ref dstLinesize, destinationPtr,
                OutputPixelFormat, _frame->width, _frame->height, 1);

            var srcData = new byte_ptrArray8();
            var srcLinesize = new int_array8();
            for (var i = 0; i < 4; i++)
            {
                srcData[(uint)i] = _frame->data[(uint)i];
                srcLinesize[(uint)i] = _frame->linesize[(uint)i];
            }

            ffmpeg.sws_scale(
                _swsContext, srcData, srcLinesize, 0, _frame->height,
                dstData, dstLinesize);
        }

        return bgraFrame;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Outside _gate: the worker needs it to finish whatever item it's mid-call on.
        _workerCts.Cancel();
        _workChannel.Writer.TryComplete();
        _frameQueue.Writer.TryComplete();
        _worker.Join(TimeSpan.FromSeconds(2));
        _scheduler.Join(TimeSpan.FromSeconds(2));
        _workerCts.Dispose();

        lock (_gate)
        {
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            if (_packet != null)
            {
                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }

            if (_frame != null)
            {
                var frame = _frame;
                ffmpeg.av_frame_free(&frame);
                _frame = null;
            }

            if (_codecContext != null)
            {
                var codecContext = _codecContext;
                ffmpeg.avcodec_free_context(&codecContext);
                _codecContext = null;
            }
        }
    }
}
