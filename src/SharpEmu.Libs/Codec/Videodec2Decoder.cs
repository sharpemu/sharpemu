// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading.Channels;
using FFmpeg.AutoGen;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Codec;

internal sealed record Videodec2DecodedFrame(
    byte[] Nv12,
    uint Width,
    uint Height,
    uint Pitch,
    bool ErrorFrame)
{
    public ulong Pts { get; init; } = ulong.MaxValue;
    public ulong Dts { get; init; } = ulong.MaxValue;
    public ulong AttachedData { get; init; }
    public bool KeyFrame { get; init; }
    public uint Profile { get; init; }
    public uint Level { get; init; }
}

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

    private const int WorkQueueCapacity = 64;

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

    private readonly Channel<byte[]?> _workChannel =
        Channel.CreateBounded<byte[]?>(new BoundedChannelOptions(WorkQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
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

    private long _workerInputCount;
    private long _workerFrameCount;
    private static readonly bool TraceEnabled = IsTraceEnabled();

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
                Trace("create failed: H.264 decoder not found");
                return null;
            }

            codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (codecContext == null)
            {
                Trace("create failed: avcodec_alloc_context3 returned null");
                return null;
            }

            codecContext->flags |= ffmpeg.AV_CODEC_FLAG_COPY_OPAQUE;
            if (ffmpeg.avcodec_open2(codecContext, codec, null) < 0)
            {
                ffmpeg.avcodec_free_context(&codecContext);
                Trace("create failed: avcodec_open2 rejected H.264 decoder");
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
                Trace("create failed: frame or packet allocation returned null");
                return null;
            }

            var decoder = new Videodec2Decoder(codecContext, frame, packet);
            Trace("create succeeded: FFmpeg H.264 worker and scheduler started");
            return decoder;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            // FFmpeg's native libraries are optional; missing ones degrade to the stub, not a crash.
            Trace($"create failed: {ex.GetType().Name}: {ex.Message}");
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

    public bool EnqueueAccessUnit(byte[] accessUnit)
    {
        return _workChannel.Writer.TryWrite(accessUnit);
    }

    /// <summary>Queues an end-of-stream drain: flush FFmpeg and emit one more buffered picture, if any.</summary>
    public bool RequestDrain()
    {
        return _workChannel.Writer.TryWrite(null);
    }

    public bool DecodeAccessUnit(
        byte[] accessUnit,
        ulong pts,
        ulong dts,
        ulong attachedData,
        out Videodec2DecodedFrame? decodedFrame)
    {
        decodedFrame = null;
        lock (_gate)
        {
            if (_disposed || accessUnit.Length == 0)
            {
                return false;
            }

            ffmpeg.av_packet_unref(_packet);
            ffmpeg.av_frame_unref(_frame);
            var packetResult = ffmpeg.av_new_packet(_packet, accessUnit.Length);
            if (packetResult < 0)
            {
                Trace($"decode failed: av_new_packet={packetResult}");
                return false;
            }

            try
            {
                fixed (byte* source = accessUnit)
                {
                    Buffer.MemoryCopy(source, _packet->data, accessUnit.Length, accessUnit.Length);
                }

                _packet->pts = ToAvTimestamp(pts);
                _packet->dts = ToAvTimestamp(dts);

                var havePendingFrame = false;
                var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                if (sendResult == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    var pendingResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                    if (pendingResult < 0)
                    {
                        Trace($"decode failed: receive-before-resend={pendingResult}");
                        return false;
                    }

                    havePendingFrame = true;
                    sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
                }

                if (sendResult < 0)
                {
                    Trace($"decode failed: avcodec_send_packet={sendResult}");
                    return false;
                }

                if (!havePendingFrame)
                {
                    var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
                    if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                        receiveResult == ffmpeg.AVERROR_EOF)
                    {
                        return true;
                    }

                    if (receiveResult < 0)
                    {
                        Trace($"decode failed: avcodec_receive_frame={receiveResult}");
                        return false;
                    }
                }

                decodedFrame = ConvertFrameToNv12Locked(pts, dts, attachedData);
                return decodedFrame is not null;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    public bool FlushOutput(out Videodec2DecodedFrame? decodedFrame)
    {
        decodedFrame = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            ffmpeg.av_frame_unref(_frame);
            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                receiveResult == ffmpeg.AVERROR_EOF)
            {
                return true;
            }

            if (receiveResult < 0)
            {
                Trace($"flush failed: avcodec_receive_frame={receiveResult}");
                return false;
            }

            try
            {
                decodedFrame = ConvertFrameToNv12Locked();
                return decodedFrame is not null;
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ffmpeg.avcodec_flush_buffers(_codecContext);
            ffmpeg.av_packet_unref(_packet);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    private Videodec2DecodedFrame? ConvertFrameToNv12Locked(
        ulong fallbackPts = ulong.MaxValue,
        ulong fallbackDts = ulong.MaxValue,
        ulong attachedData = 0)
    {
        if (_frame->width <= 0 || _frame->height <= 0)
        {
            return null;
        }

        var width = checked((uint)_frame->width);
        var height = checked((uint)_frame->height);
        uint pitch;
        ulong lumaBytes;
        ulong requiredBytes;
        try
        {
            pitch = checked((width + 255u) & ~255u);
            lumaBytes = checked((ulong)pitch * height);
            var chromaRows = ((ulong)height + 1UL) / 2UL;
            requiredBytes = checked(lumaBytes + ((ulong)pitch * chromaRows));
        }
        catch (OverflowException)
        {
            return null;
        }

        if (requiredBytes > int.MaxValue)
        {
            return null;
        }

        _swsContext = ffmpeg.sws_getCachedContext(
            _swsContext,
            _frame->width,
            _frame->height,
            (AVPixelFormat)_frame->format,
            _frame->width,
            _frame->height,
            AVPixelFormat.AV_PIX_FMT_NV12,
            ffmpeg.SWS_FAST_BILINEAR,
            null,
            null,
            null);
        if (_swsContext == null)
        {
            return null;
        }

        var nv12 = new byte[(int)requiredBytes];
        fixed (byte* destination = nv12)
        {
            var destinationPlanes = new byte*[4]
            {
                destination,
                destination + checked((int)lumaBytes),
                null,
                null,
            };
            var destinationStrides = new int[4]
            {
                checked((int)pitch),
                checked((int)pitch),
                0,
                0,
            };
            var rows = ffmpeg.sws_scale(
                _swsContext,
                _frame->data,
                _frame->linesize,
                0,
                _frame->height,
                destinationPlanes,
                destinationStrides);
            if (rows != _frame->height)
            {
                return null;
            }
        }

        return new Videodec2DecodedFrame(
            nv12,
            width,
            height,
            pitch,
            (_frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0)
        {
            Pts = FromAvTimestamp(_frame->pts, fallbackPts),
            Dts = FromAvTimestamp(_frame->pkt_dts, fallbackDts),
            AttachedData = attachedData,
            KeyFrame = (_frame->flags & ffmpeg.AV_FRAME_FLAG_KEY) != 0,
            Profile = _codecContext->profile > 0 ? (uint)_codecContext->profile : 0,
            Level = _codecContext->level > 0 ? (uint)_codecContext->level : 0,
        };
    }

    private static long ToAvTimestamp(ulong timestamp) =>
        timestamp <= long.MaxValue ? (long)timestamp : ffmpeg.AV_NOPTS_VALUE;

    private static ulong FromAvTimestamp(long timestamp, ulong fallback) =>
        timestamp == ffmpeg.AV_NOPTS_VALUE || timestamp < 0 ? fallback : (ulong)timestamp;

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

            var inputOrdinal = Interlocked.Increment(ref _workerInputCount);
            var decodedOk = item is null
                ? DrainCoreLocked(out var bgraFrame, out var hasPicture, out var width, out var height)
                : DecodeCoreLocked(item, out bgraFrame, out hasPicture, out width, out height);
            if (ShouldTrace(inputOrdinal))
            {
                Trace(
                    $"worker input={inputOrdinal} kind={(item is null ? "drain" : "au")} " +
                    $"bytes={item?.Length ?? 0} ok={decodedOk} picture={hasPicture} size={width}x{height}");
            }

            if (!decodedOk || !hasPicture || bgraFrame is null)
            {
                continue;
            }

            var frameOrdinal = Interlocked.Increment(ref _workerFrameCount);
            if (ShouldTrace(frameOrdinal))
            {
                Trace($"worker frame={frameOrdinal} produced size={width}x{height} bytes={bgraFrame.Length}");
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

    private static bool IsTraceEnabled()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_LOG_VIDEODEC2");
        return string.Equals(value, "1", StringComparison.Ordinal) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldTrace(long ordinal) =>
        TraceEnabled && (ordinal <= 16 || ordinal % 120 == 0);

    private static void Trace(string message)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine($"[VIDEODEC2][TRACE] {message}");
        }
    }
}
