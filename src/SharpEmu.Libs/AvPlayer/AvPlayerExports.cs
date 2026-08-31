// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Media;
using SharpEmu.Libs.VideoOut;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace SharpEmu.Libs.AvPlayer;

public static class AvPlayerExports
{
    private const int InvalidParameters = unchecked((int)0x806A0001);
    private const int OperationFailed = unchecked((int)0x806A0002);
    private const int WarningJumpComplete = unchecked((int)0x806A00A3);
    private const int DefaultFrameBufferCount = 2;
    private const int MinimumFrameBufferCount = 2;
    private const int MaximumFrameBufferCount = 16;
    private const int MaxCatchUpFrames = 2;
    private const uint AvSyncModeDefault = 0;
    private const uint AvSyncModeNone = 1;
    private const int AudioSamplesPerFrame = 1024;
    private const int AudioSampleRate = 48_000;
    private const ulong TextureAllocationAlignment = 0x100;
    private const int FramePitchAlignment = 64;
    private const int FrameHeightAlignment = 16;
    private const int FrameInfoSize = 40;
    private const int FrameInfoExSize = 104;
    // The legacy destination is 40 bytes on Gen4 but only 32 bytes on Gen5.
    // Writing the Gen4 layout into a Gen5 caller can overwrite its stack canary.
    private const int Gen4StreamInfoSize = 40;
    private const int Gen5StreamInfoSize = 32;
    private const int StreamInfoExSize = 104;
    private const int MaxGuestPathLength = 4096;
    private const int ReplacementFileReadBufferSize = 1024 * 1024;
    private const int VideoPitchAlignment = 256;
    private static readonly object StateGate = new();
    private static readonly HashSet<string> TracedOnce = new();
    private static readonly Dictionary<ulong, PlayerState> Players = new();
    private static readonly ConcurrentDictionary<ulong, ulong> VideoBufferRanges = new();
    private static readonly bool TraceVideoImages = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_AVPLAYER_IMAGES"),
        "1",
        StringComparison.Ordinal);
    private static int _traceCount;
    private static int _videoPayloadTraceCount;
    private static long _fallbackPresentationSerial;

    internal static bool TryGetFallbackPresentationFrame(
        out byte[] pixels,
        out uint width,
        out uint height,
        out long serial)
    {
        lock (StateGate)
        {
            PlayerState? latest = null;
            foreach (var player in Players.Values)
            {
                if (!ShouldPresentFallback(player.Started, player.Paused))
                {
                    continue;
                }

                if (player.FallbackPlayback is { } playback &&
                    player.FallbackRequestedFrameIndex > player.FallbackPresentedFrameIndex)
                {
                    if (playback.TryGetFrameAtOrBeforeIndex(
                            player.FallbackRequestedFrameIndex,
                            out var playbackPixels,
                            out var playbackFrameIndex,
                            out var advanced))
                    {
                        if (playbackFrameIndex > player.FallbackPresentedFrameIndex)
                        {
                            var skipFirstDecodedFrame =
                                player.SkipFirstFallbackPlaybackFrame;
                            if (ShouldPublishFallbackPlaybackFrame(
                                    advanced,
                                    player.FallbackPresentationPixels is not null,
                                    ref skipFirstDecodedFrame))
                            {
                                player.FallbackPresentationPixels = playbackPixels;
                                player.FallbackPresentationWidth = playback.Width;
                                player.FallbackPresentationHeight = playback.Height;
                                player.FallbackPresentationSerial =
                                    Interlocked.Increment(ref _fallbackPresentationSerial);
                            }
                            player.SkipFirstFallbackPlaybackFrame =
                                skipFirstDecodedFrame;
                            player.FallbackPresentedFrameIndex = playbackFrameIndex;
                        }
                    }
                    else if (playback.IsFinished)
                    {
                        playback.Dispose();
                        player.FallbackPlayback = null;
                        player.FallbackPlaybackCompleted = true;
                        player.FallbackCompletionPending = true;
                        player.FallbackPlaybackCompletedTicks = Stopwatch.GetTimestamp();
                        if (ShouldCompleteGuestPlaybackAfterFallback(
                                player.FallbackCompletionPending,
                                player.Looping))
                        {
                            CompleteGuestPlaybackAfterFallback(player);
                        }
                        Trace(
                            $"host_fallback_finished handle=0x{player.Handle:X16} " +
                            $"guest_eof={player.EndOfStream} " +
                            $"completion_pending={player.FallbackCompletionPending} " +
                            "holding_last_frame=true");
                    }
                }

                // Keep a completed fallback frame only until the guest-visible
                // player reaches EOF or the bounded grace period expires. A
                // looping player does not use fallback completion as EOF.
                if (ShouldReleaseCompletedFallback(
                        player.FallbackPlaybackCompleted,
                        player.EndOfStream,
                        player.FallbackPlaybackCompletedTicks,
                        Stopwatch.GetTimestamp()))
                {
                    ClearFallbackPresentation(player);
                }

                if (player.FallbackPresentationPixels is null ||
                    player.FallbackPresentationSerial <= 0 ||
                    latest is not null &&
                    player.FallbackPresentationSerial <= latest.FallbackPresentationSerial)
                {
                    continue;
                }

                latest = player;
            }

            if (latest?.FallbackPresentationPixels is not { } frame)
            {
                pixels = [];
                width = 0;
                height = 0;
                serial = 0;
                return false;
            }

            pixels = frame;
            width = latest.FallbackPresentationWidth;
            height = latest.FallbackPresentationHeight;
            serial = latest.FallbackPresentationSerial;
            return IsValidBgraFrame(pixels, width, height);
        }
    }

    internal static bool ShouldPublishFallbackPlaybackFrame(
        bool advanced,
        bool hasPresentation,
        ref bool skipFirstDecodedFrame)
    {
        if (advanced && hasPresentation && skipFirstDecodedFrame)
        {
            skipFirstDecodedFrame = false;
            return false;
        }

        return advanced || !hasPresentation;
    }

    internal static bool ShouldCompleteGuestPlaybackAfterFallback(
        bool fallbackCompletionPending,
        bool looping) =>
        fallbackCompletionPending && !looping;

    internal static bool ShouldPresentFallback(bool started, bool paused) =>
        started && !paused;

    internal static bool ShouldCreateFallbackPoster(
        bool fallbackPlaybackAttempted,
        bool fallbackPlaybackRunning,
        bool hasPresentation) =>
        !hasPresentation &&
        (!fallbackPlaybackAttempted || fallbackPlaybackRunning);

    /// <summary>
    /// How long a finished host playback keeps its final image on screen while
    /// waiting for the guest player to reach end of stream.  Titles that pause
    /// their AvPlayer after the first frame never do, so the hold expires.
    /// </summary>
    private static readonly long FallbackHoldGraceTicks = Stopwatch.Frequency;

    internal static bool ShouldReleaseCompletedFallback(
        bool fallbackPlaybackCompleted,
        bool guestEndOfStream,
        long completedTicks,
        long nowTicks) =>
        fallbackPlaybackCompleted &&
        (guestEndOfStream ||
         completedTicks != 0 && nowTicks - completedTicks >= FallbackHoldGraceTicks);

    private static void ClearFallbackPresentation(PlayerState player)
    {
        player.FallbackPresentationPixels = null;
        player.FallbackPresentationWidth = 0;
        player.FallbackPresentationHeight = 0;
        player.FallbackPresentationSerial = 0;
        player.FallbackPlaybackCompleted = false;
        player.FallbackPlaybackCompletedTicks = 0;
        player.SkipFirstFallbackPlaybackFrame = false;
        Trace(
            $"host_fallback_released handle=0x{player.Handle:X16} " +
            $"guest_eof={player.EndOfStream}");
    }

    internal static bool ShouldTraceVideoBufferAddress(ulong address)
        => TraceVideoImages && IsVideoBufferAddress(address);

    internal static bool IsVideoBufferAddress(ulong address)
    {
        if (address == 0)
        {
            return false;
        }

        foreach (var (start, length) in VideoBufferRanges)
        {
            if (address >= start && address - start < length)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ShouldTraceVideoBufferRange(ulong address, ulong length)
    {
        if (!TraceVideoImages || address == 0 || length == 0)
        {
            return false;
        }

        foreach (var (start, rangeLength) in VideoBufferRanges)
        {
            if (address <= start
                    ? start - address < length
                    : address - start < rangeLength)
            {
                return true;
            }
        }

        return false;
    }

    private static void RegisterVideoBuffer(ulong address, int size, int index, string source)
    {
        if (address == 0 || size <= 0)
        {
            return;
        }

        VideoBufferRanges[address] = checked((ulong)size);
        if (TraceVideoImages)
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][TRACE] video_buffer index={index} source={source} " +
                $"data=0x{address:X16} size={size}");
        }
    }

    private enum VideoFrameReadResult
    {
        Pending,
        Ready,
        End,
    }

    private sealed class VideoFrameQueue : IDisposable
    {
        private readonly FfmpegMediaStream _stream;
        private readonly int _frameByteCount;
        private readonly Channel<byte[]> _frames;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _worker;
        private int _completed;
        private int _disposed;

        public VideoFrameQueue(
            FfmpegMediaStream stream,
            int frameByteCount,
            int capacity)
        {
            _stream = stream;
            _frameByteCount = frameByteCount;
            _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(
                Math.Clamp(capacity, MinimumFrameBufferCount, MaximumFrameBufferCount))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
            _worker = new Thread(DecodeFrames)
            {
                IsBackground = true,
                Name = "SharpEmu AvPlayer Video Decoder",
            };
            _worker.Start();
        }

        public VideoFrameReadResult TryRead(out byte[]? frame)
        {
            if (_frames.Reader.TryRead(out frame))
            {
                return VideoFrameReadResult.Ready;
            }

            frame = null;
            return Volatile.Read(ref _completed) != 0
                ? VideoFrameReadResult.End
                : VideoFrameReadResult.Pending;
        }

        private void DecodeFrames()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var frame = GC.AllocateUninitializedArray<byte>(_frameByteCount);
                    if (!ReadExactly(_stream, frame))
                    {
                        break;
                    }

                    _frames.Writer.WriteAsync(frame, _stop.Token)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException) when (_stop.IsCancellationRequested)
            {
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(
                    $"[AVPLAYER][ERROR] FFmpeg stream read failed: {exception.Message}");
            }
            finally
            {
                Volatile.Write(ref _completed, 1);
                _frames.Writer.TryComplete();
                _stream.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stop.Cancel();
            _stream.Dispose();
            _frames.Writer.TryComplete();
        }
    }

    private sealed class PlayerState : IDisposable
    {
        public required ulong Handle { get; init; }
        public bool AutoStart { get; init; }
        public ulong AllocatorObject { get; init; }
        public ulong AllocateTextureCallback { get; init; }
        public ulong AllocateCallback { get; init; }
        public ulong FileObject { get; init; }
        public ulong FileOpenCallback { get; init; }
        public ulong FileCloseCallback { get; init; }
        public ulong FileReadOffsetCallback { get; init; }
        public ulong FileSizeCallback { get; init; }
        public object FileReplacementGate { get; } = new();
        public ulong EventObject { get; init; }
        public ulong EventCallback { get; init; }
        public string? SourcePath { get; set; }
        public string? OwnedSourcePath { get; set; }
        public ulong ReplacementFileReadBuffer { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double FramesPerSecond { get; set; } = 30.0;
        public ulong DurationMilliseconds { get; set; }
        public bool HasAudio { get; set; }
        public uint AvSyncMode { get; set; } = AvSyncModeDefault;
        public bool IsGen5 { get; init; }
        public bool Started { get; set; }
        public bool Paused { get; set; }
        public bool Looping { get; set; }
        public bool EndOfStream { get; set; }
        public bool SeekVideoFramePending { get; set; }
        public ulong StartTimeMilliseconds { get; set; }
        public VideoFrameQueue? VideoDecoder { get; set; }
        public Stream? AudioDecoderOutput { get; set; }
        public Stopwatch PlaybackClock { get; } = new();
        public long SkippedFrameDebt { get; set; }
        public byte[]? RawFrame { get; set; }
        public byte[]? RawAudioFrame { get; set; }
        public byte[]? PaddedFrame { get; set; }
        public ulong[] GuestBuffers { get; init; } = new ulong[DefaultFrameBufferCount];
        public bool TextureAllocatorFailed { get; set; }
        public int GuestBufferStride { get; set; }
        public int NextGuestBuffer { get; set; }
        public ulong LastGuestBuffer { get; set; }
        public ulong LastVideoTimestamp { get; set; }
        public ulong EventDataBuffer { get; set; }
        public long NextFrameIndex { get; set; }
        public ulong AudioBufferBase { get; set; }
        public int NextAudioBuffer { get; set; }
        public long NextAudioFrameIndex { get; set; }
        public byte[]? FallbackPresentationPixels { get; set; }
        public uint FallbackPresentationWidth { get; set; }
        public uint FallbackPresentationHeight { get; set; }
        public long FallbackPresentationSerial { get; set; }
        public MediaFramePlayback? FallbackPlayback { get; set; }
        public bool FallbackPlaybackAttempted { get; set; }
        public bool FallbackPlaybackCompleted { get; set; }
        public bool FallbackCompletionPending { get; set; }
        public long FallbackPlaybackCompletedTicks { get; set; }
        public bool SkipFirstFallbackPlaybackFrame { get; set; }
        public long FallbackRequestedFrameIndex { get; set; } = -1;
        public long FallbackPresentedFrameIndex { get; set; } = -1;

        public void Dispose()
        {
            DisposePlaybackResources();
            if (OwnedSourcePath is { } ownedSourcePath)
            {
                TryDeleteMaterializedSource(ownedSourcePath);
                OwnedSourcePath = null;
            }
            SourcePath = null;
        }

        private void DisposePlaybackResources()
        {
            VideoDecoder?.Dispose();
            VideoDecoder = null;
            AudioDecoderOutput?.Dispose();
            AudioDecoderOutput = null;
            FallbackPlayback?.Dispose();
            FallbackPlayback = null;
        }

        public void ResetPlayback()
        {
            DisposePlaybackResources();
            PlaybackClock.Reset();
            NextFrameIndex = 0;
            LastGuestBuffer = 0;
            LastVideoTimestamp = 0;
            SeekVideoFramePending = false;
            StartTimeMilliseconds = 0;
            NextAudioFrameIndex = 0;
            SkippedFrameDebt = 0;
            EndOfStream = false;
            FallbackPresentationPixels = null;
            FallbackPresentationWidth = 0;
            FallbackPresentationHeight = 0;
            FallbackPresentationSerial = 0;
            FallbackPlaybackAttempted = false;
            FallbackPlaybackCompleted = false;
            FallbackCompletionPending = false;
            FallbackPlaybackCompletedTicks = 0;
            SkipFirstFallbackPlaybackFrame = false;
            FallbackRequestedFrameIndex = -1;
            FallbackPresentedFrameIndex = -1;
        }
    }

    [SysAbiExport(
        Nid = "aS66RI0gGgo",
        ExportName = "sceAvPlayerInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInit(CpuContext ctx)
    {
        var initDataAddress = ctx[CpuRegister.Rdi];
        if (initDataAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle))
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        lock (StateGate)
        {
            var autoStartOffset = GetAutoStartOffset(ctx.TargetGeneration, extended: false);
            Players.Add(handle, new PlayerState
            {
                Handle = handle,
                IsGen5 = IsGen5Target(ctx.TargetGeneration),
                AutoStart = TryReadByte(ctx, initDataAddress + autoStartOffset, out var autoStart) && autoStart != 0,
                GuestBuffers = new ulong[ReadOutputVideoFrameBufferCount(
                    ctx,
                    initDataAddress,
                    extended: false)],
                AllocatorObject = TryReadUInt64(ctx, initDataAddress, out var allocatorObject) ? allocatorObject : 0,
                AllocateTextureCallback = TryReadUInt64(ctx, initDataAddress + 24, out var allocateTexture) ? allocateTexture : 0,
                AllocateCallback = TryReadUInt64(ctx, initDataAddress + 8, out var allocate) ? allocate : 0,
                FileObject = TryReadUInt64(ctx, initDataAddress + 40, out var fileObject) ? fileObject : 0,
                FileOpenCallback = TryReadUInt64(ctx, initDataAddress + 48, out var fileOpen) ? fileOpen : 0,
                FileCloseCallback = TryReadUInt64(ctx, initDataAddress + 56, out var fileClose) ? fileClose : 0,
                FileReadOffsetCallback = TryReadUInt64(ctx, initDataAddress + 64, out var fileReadOffset) ? fileReadOffset : 0,
                FileSizeCallback = TryReadUInt64(ctx, initDataAddress + 72, out var fileSize) ? fileSize : 0,
                EventObject = TryReadUInt64(ctx, initDataAddress + 80, out var eventObject) ? eventObject : 0,
                EventCallback = TryReadUInt64(ctx, initDataAddress + 88, out var eventCallback) ? eventCallback : 0,
            });
        }

        Trace(
            $"init handle=0x{handle:X16} " +
            $"alloc_texture=0x{Players[handle].AllocateTextureCallback:X16} " +
            $"file_object=0x{Players[handle].FileObject:X16} " +
            $"file_open=0x{Players[handle].FileOpenCallback:X16} " +
            $"file_close=0x{Players[handle].FileCloseCallback:X16} " +
            $"file_read=0x{Players[handle].FileReadOffsetCallback:X16} " +
            $"file_size=0x{Players[handle].FileSizeCallback:X16} " +
            $"video_buffers={Players[handle].GuestBuffers.Length}");
        ctx[CpuRegister.Rax] = handle;
        return unchecked((int)handle);
    }

    [SysAbiExport(
        Nid = "HD1YKVU26-M",
        ExportName = "sceAvPlayerPostInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPostInit(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var dataAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            return SetReturn(
                ctx,
                handle != 0 && dataAddress != 0 && Players.ContainsKey(handle)
                    ? 0
                    : InvalidParameters);
        }
    }

    [SysAbiExport(
        Nid = "o9eWRkSL+M4",
        ExportName = "sceAvPlayerInitEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerInitEx(CpuContext ctx)
    {
        var initDataAddress = ctx[CpuRegister.Rdi];
        var playerOutAddress = ctx[CpuRegister.Rsi];
        if (initDataAddress == 0 ||
            playerOutAddress == 0 ||
            !KernelMemoryCompatExports.TryAllocateHleData(ctx, 0x40, 16, out var handle) ||
            !ctx.TryWriteUInt64(playerOutAddress, handle))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        lock (StateGate)
        {
            var autoStartOffset = GetAutoStartOffset(ctx.TargetGeneration, extended: true);
            Players.Add(handle, new PlayerState
            {
                Handle = handle,
                IsGen5 = IsGen5Target(ctx.TargetGeneration),
                AutoStart = TryReadByte(ctx, initDataAddress + autoStartOffset, out var autoStart) && autoStart != 0,
                GuestBuffers = new ulong[ReadOutputVideoFrameBufferCount(
                    ctx,
                    initDataAddress,
                    extended: true)],
                AllocatorObject = TryReadUInt64(ctx, initDataAddress + 8, out var allocatorObject) ? allocatorObject : 0,
                AllocateTextureCallback = TryReadUInt64(ctx, initDataAddress + 32, out var allocateTexture) ? allocateTexture : 0,
                AllocateCallback = TryReadUInt64(ctx, initDataAddress + 16, out var allocate) ? allocate : 0,
                FileObject = TryReadUInt64(ctx, initDataAddress + 48, out var fileObject) ? fileObject : 0,
                FileOpenCallback = TryReadUInt64(ctx, initDataAddress + 56, out var fileOpen) ? fileOpen : 0,
                FileCloseCallback = TryReadUInt64(ctx, initDataAddress + 64, out var fileClose) ? fileClose : 0,
                FileReadOffsetCallback = TryReadUInt64(ctx, initDataAddress + 72, out var fileReadOffset) ? fileReadOffset : 0,
                FileSizeCallback = TryReadUInt64(ctx, initDataAddress + 80, out var fileSize) ? fileSize : 0,
                EventObject = TryReadUInt64(ctx, initDataAddress + 88, out var eventObject) ? eventObject : 0,
                EventCallback = TryReadUInt64(ctx, initDataAddress + 96, out var eventCallback) ? eventCallback : 0,
            });
        }

        Trace(
            $"init_ex handle=0x{handle:X16} " +
            $"alloc_texture=0x{Players[handle].AllocateTextureCallback:X16} " +
            $"file_object=0x{Players[handle].FileObject:X16} " +
            $"file_open=0x{Players[handle].FileOpenCallback:X16} " +
            $"file_close=0x{Players[handle].FileCloseCallback:X16} " +
            $"file_read=0x{Players[handle].FileReadOffsetCallback:X16} " +
            $"file_size=0x{Players[handle].FileSizeCallback:X16} " +
            $"video_buffers={Players[handle].GuestBuffers.Length}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "eBTreZ84JFY",
        ExportName = "sceAvPlayerSetLogCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetLogCallback(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "NkJwDzKmIlw",
        ExportName = "sceAvPlayerClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerClose(CpuContext ctx)
    {
        PlayerState? player;
        lock (StateGate)
        {
            if (!Players.Remove(ctx[CpuRegister.Rdi], out player))
            {
                return SetReturn(ctx, InvalidParameters);
            }
        }

        player.Dispose();
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "KMcEa+rHsIo",
        ExportName = "sceAvPlayerAddSource",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSource(CpuContext ctx)
    {
        if (!TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rsi], MaxGuestPathLength, out var path))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        return AddSource(ctx, path);
    }

    [SysAbiExport(
        Nid = "x8uvuFOPZhU",
        ExportName = "sceAvPlayerAddSourceEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerAddSourceEx(CpuContext ctx)
    {
        var uriType = unchecked((uint)ctx[CpuRegister.Rsi]);
        var detailsAddress = ctx[CpuRegister.Rdx];
        if (uriType != 0 || detailsAddress == 0 ||
            !ctx.TryReadUInt64(detailsAddress, out var pathAddress) ||
            !TryReadUInt32(ctx, detailsAddress + sizeof(ulong), out var pathLength) ||
            pathLength == 0 || pathLength > MaxGuestPathLength ||
            !TryReadUtf8(ctx, pathAddress, checked((int)pathLength), out var path))
        {
            return SetReturn(ctx, InvalidParameters);
        }

        return AddSource(ctx, path.TrimEnd('\0'));
    }

    [SysAbiExport(
        Nid = "ET4Gr-Uu07s",
        ExportName = "sceAvPlayerStart",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStart(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer) || foundPlayer.SourcePath is null)
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            player.Started = true;
            player.Paused = false;
            player.EndOfStream = false;
            Trace($"start handle=0x{player.Handle:X16}");
        }

        // The platform player opens its codecs and launches dedicated decoder
        // workers as part of Start. Starting lazily from the first data poll can
        // starve high-resolution streams behind guest execution and audio work.
        if (!EnsureDecoder(player))
        {
            lock (StateGate)
            {
                player.Started = false;
                player.EndOfStream = true;
            }
            return SetReturn(ctx, OperationFailed);
        }

        // Event callbacks are guest code and can immediately query the player.
        // Never hold StateGate while waiting for one or the callback deadlocks
        // when it re-enters an AvPlayer export on another guest worker.
        NotifyEvent(ctx, player, 3); // StatePlay
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "ZC17w3vB5Lo",
        ExportName = "sceAvPlayerStop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStop(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            player.ResetPlayback();
            player.Started = false;
        }

        NotifyEvent(ctx, player, 1); // StateStop
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "9y5v+fGN4Wk",
        ExportName = "sceAvPlayerPause",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerPause(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            player.Paused = true;
            player.PlaybackClock.Stop();
            player.FallbackPlayback?.Pause();
            Console.Error.WriteLine($"[AVPLAYER][INFO] pause handle=0x{player.Handle:X16}");
        }


        NotifyEvent(ctx, player, 4); // StatePause
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "w5moABNwnRY",
        ExportName = "sceAvPlayerResume",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerResume(CpuContext ctx)
    {
        PlayerState player;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            player.Paused = false;
            player.SeekVideoFramePending = false;
            if (player.VideoDecoder is not null)
            {
                player.PlaybackClock.Start();
            }
            player.FallbackPlayback?.Resume();
            Console.Error.WriteLine($"[AVPLAYER][INFO] resume handle=0x{player.Handle:X16}");
        }

        NotifyEvent(ctx, player, 3); // StatePlay
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "OVths0xGfho",
        ExportName = "sceAvPlayerSetLooping",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetLooping(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            player.Looping = ctx[CpuRegister.Rsi] != 0;
            if (ShouldCompleteGuestPlaybackAfterFallback(
                    player.FallbackCompletionPending,
                    player.Looping))
            {
                CompleteGuestPlaybackAfterFallback(player);
            }
            Trace(
                $"set_looping handle=0x{player.Handle:X16} enabled={player.Looping} " +
                $"guest_eof={player.EndOfStream} " +
                $"completion_pending={player.FallbackCompletionPending}");
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "ODJK2sn9w4A",
        ExportName = "sceAvPlayerEnableStream",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerEnableStream(CpuContext ctx)
    {
        TraceOnce(
            $"enable_stream_{ctx[CpuRegister.Rsi]}",
            $"enable_stream index={ctx[CpuRegister.Rsi]}");
        return ValidatePlayer(ctx);
    }

    [SysAbiExport(
        Nid = "k-q+xOxdc3E",
        ExportName = "sceAvPlayerSetAvSyncMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerSetAvSyncMode(CpuContext ctx)
    {
        var requestedMode = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) ||
                !IsValidAvSyncMode(requestedMode))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            player.AvSyncMode = requestedMode;
            Trace(
                $"set_av_sync_mode handle=0x{player.Handle:X16} " +
                $"mode={player.AvSyncMode}");
            return SetReturn(ctx, 0);
        }
    }

    [SysAbiExport(
        Nid = "ctTAcF5DiKQ",
        ExportName = "sceAvPlayerGetStreamInfoEx",
        Target = Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetStreamInfoEx(CpuContext ctx)
    {
        var streamIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var infoAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) ||
                streamIndex > (player.HasAudio ? 1u : 0u) ||
                infoAddress == 0)
            {
                return SetReturn(ctx, InvalidParameters);
            }

            Span<byte> info = stackalloc byte[StreamInfoExSize];
            info.Clear();
            WriteGen5StreamInfoEx(
                info,
                GetStreamType(ctx.TargetGeneration, streamIndex),
                streamIndex == 0 ? checked((uint)player.Width) : 0,
                streamIndex == 0 ? checked((uint)player.Height) : 0,
                streamIndex == 0 ? player.FramesPerSecond : 0,
                player.DurationMilliseconds);
            return SetReturn(
                ctx,
                ctx.Memory.TryWrite(infoAddress, info) ? 0 : InvalidParameters);
        }
    }

    [SysAbiExport(
        Nid = "XC9wM+xULz8",
        ExportName = "sceAvPlayerJumpToTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerJumpToTime(CpuContext ctx)
    {
        PlayerState player;
        var requestedMilliseconds = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer) ||
                foundPlayer.SourcePath is null)
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;

            var wasPaused = player.Paused;
            player.ResetPlayback();
            player.Started = true;
            player.Paused = wasPaused;
            player.StartTimeMilliseconds = Math.Min(
                requestedMilliseconds,
                player.DurationMilliseconds);
            player.NextFrameIndex = 0;
            player.NextAudioFrameIndex = 0;
            player.SeekVideoFramePending = wasPaused;
            Trace(
                $"jump handle=0x{player.Handle:X16} requested_ms={requestedMilliseconds} " +
                $"start_ms={player.StartTimeMilliseconds} paused={wasPaused} " +
                $"seek_frame={player.SeekVideoFramePending}");
        }

        NotifyWarning(ctx, player, WarningJumpComplete);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "yN7Jhuv8g24",
        ExportName = "sceAvPlayerVprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerVprintf(CpuContext ctx) => SetReturn(ctx, 0);

    [SysAbiExport(
        Nid = "UbQoYawOsfY",
        ExportName = "sceAvPlayerIsActive",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerIsActive(CpuContext ctx)
    {
        lock (StateGate)
        {
            var found = Players.TryGetValue(ctx[CpuRegister.Rdi], out var player);
            var active = found && player!.Started && !player.EndOfStream;
            TraceOnce(
                "is_active",
                $"is_active found={found} started={(found && player!.Started)} " +
                $"eos={(found && player!.EndOfStream)} returned={(active ? 1 : 0)}");
            return SetReturn(ctx, active ? 1 : 0);
        }
    }

    [SysAbiExport(
        Nid = "o3+RWnHViSg",
        ExportName = "sceAvPlayerGetVideoData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoData(CpuContext ctx) => GetVideoData(ctx, extended: false);

    [SysAbiExport(
        Nid = "JdksQu8pNdQ",
        ExportName = "sceAvPlayerGetVideoDataEx",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetVideoDataEx(CpuContext ctx) => GetVideoData(ctx, extended: true);

    [SysAbiExport(
        Nid = "Wnp1OVcrZgk",
        ExportName = "sceAvPlayerGetAudioData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetAudioData(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            var found = Players.TryGetValue(ctx[CpuRegister.Rdi], out var player);
            if (!found || infoAddress == 0 || !player!.Started || player.Paused ||
                player.EndOfStream || player.SourcePath is null ||
                !player.HasAudio || !EnsureAudioDecoder(player))
            {
                TraceOnce(
                    "audio_data_refused",
                    $"audio_data refused found={found} info=0x{infoAddress:X16} " +
                    $"started={(found && player!.Started)} paused={(found && player!.Paused)} " +
                    $"eos={(found && player!.EndOfStream)} " +
                    $"has_audio={(found && player!.HasAudio)}");
                return SetReturn(ctx, 0);
            }

            TraceOnce("audio_data_ok", "audio_data first delivery");

            const int channelCount = 2;
            const int audioFrameSize = AudioSamplesPerFrame * channelCount * sizeof(short);
            if (player.RawAudioFrame is null ||
                !ReadExactly(player.AudioDecoderOutput, player.RawAudioFrame))
            {
                return SetReturn(ctx, 0);
            }
            if (player.AudioBufferBase == 0)
            {
                if (!KernelMemoryCompatExports.TryAllocateHleData(
                        ctx,
                        audioFrameSize * 8UL,
                        0x100,
                        out var audioBufferBase))
                {
                    return SetReturn(ctx, 0);
                }
                player.AudioBufferBase = audioBufferBase;
            }

            var bufferAddress = player.AudioBufferBase +
                checked((ulong)(player.NextAudioBuffer * audioFrameSize));
            player.NextAudioBuffer = (player.NextAudioBuffer + 1) % 8;
            if (!ctx.Memory.TryWrite(bufferAddress, player.RawAudioFrame))
            {
                return SetReturn(ctx, 0);
            }

            var timestamp = player.StartTimeMilliseconds + checked((ulong)(
                player.NextAudioFrameIndex * AudioSamplesPerFrame * 1000L /
                AudioSampleRate));
            player.NextAudioFrameIndex++;
            Span<byte> info = stackalloc byte[FrameInfoSize];
            info.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(info[0..], bufferAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(info[16..], timestamp);
            BinaryPrimitives.WriteUInt16LittleEndian(info[24..], channelCount);
            BinaryPrimitives.WriteUInt32LittleEndian(info[28..], AudioSampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(info[32..], audioFrameSize);
            if (!ctx.Memory.TryWrite(infoAddress, info))
            {
                return SetReturn(ctx, 0);
            }
            Trace($"audio_frame handle=0x{player.Handle:X16} ts={timestamp} data=0x{bufferAddress:X16}");
            return SetReturn(ctx, 1);
        }
    }

    [SysAbiExport(
        Nid = "wwM99gjFf1Y",
        ExportName = "sceAvPlayerCurrentTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerCurrentTime(CpuContext ctx)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            var milliseconds = player.AvSyncMode == AvSyncModeDefault && player.HasAudio
                ? player.StartTimeMilliseconds + checked((ulong)(
                    player.NextAudioFrameIndex * AudioSamplesPerFrame * 1000L /
                    AudioSampleRate))
                : player.StartTimeMilliseconds +
                    checked((ulong)player.PlaybackClock.ElapsedMilliseconds);
            ctx[CpuRegister.Rax] = milliseconds;
            return unchecked((int)milliseconds);
        }
    }

    [SysAbiExport(
        Nid = "hdTyRzCXQeQ",
        ExportName = "sceAvPlayerStreamCount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerStreamCount(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(
                ctx,
                Players.TryGetValue(ctx[CpuRegister.Rdi], out var player)
                    ? player.HasAudio ? 2 : 1
                    : InvalidParameters);
        }
    }

    internal static void RegisterPlayerForTest(
        ulong handle,
        int width,
        int height,
        ulong durationMilliseconds,
        ulong allocateTextureCallback = 0,
        ulong allocateCallback = 0,
        ulong fileObject = 0,
        ulong fileOpenCallback = 0,
        ulong fileCloseCallback = 0,
        ulong fileReadOffsetCallback = 0,
        ulong fileSizeCallback = 0,
        bool hasAudio = false,
        double framesPerSecond = 30.0,
        bool isGen5 = true)
    {
        PlayerState? previous;
        lock (StateGate)
        {
            Players.Remove(handle, out previous);
            Players[handle] = new PlayerState
            {
                Handle = handle,
                IsGen5 = isGen5,
                Width = width,
                Height = height,
                DurationMilliseconds = durationMilliseconds,
                HasAudio = hasAudio,
                FramesPerSecond = framesPerSecond,
                AllocateTextureCallback = allocateTextureCallback,
                AllocateCallback = allocateCallback,
                FileObject = fileObject,
                FileOpenCallback = fileOpenCallback,
                FileCloseCallback = fileCloseCallback,
                FileReadOffsetCallback = fileReadOffsetCallback,
                FileSizeCallback = fileSizeCallback,
            };
        }

        previous?.Dispose();
    }

    internal static bool AllocateGuestVideoBuffersForTest(
        CpuContext ctx,
        ulong handle,
        out ulong firstBuffer)
    {
        lock (StateGate)
        {
            if (!Players.TryGetValue(handle, out var player))
            {
                firstBuffer = 0;
                return false;
            }

            var bufferSize = GetVideoBufferSize(player);
            var allocated = AllocateGuestVideoBuffers(ctx, player, bufferSize);
            firstBuffer = player.GuestBuffers[0];
            return allocated && firstBuffer != 0;
        }
    }

    internal static void RemovePlayerForTest(ulong handle)
    {
        PlayerState? player;
        lock (StateGate)
        {
            Players.Remove(handle, out player);
        }

        player?.Dispose();
    }

    [SysAbiExport(
        Nid = "d8FcbzfAdQw",
        ExportName = "sceAvPlayerGetStreamInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAvPlayer")]
    public static int AvPlayerGetStreamInfo(CpuContext ctx) =>
        GetStreamInfoCore(ctx);

    private static int GetStreamInfoCore(CpuContext ctx)
    {
        var streamIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var infoAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) ||
                streamIndex > (player.HasAudio ? 1u : 0u) ||
                infoAddress == 0 || player.Width <= 0 || player.Height <= 0)
            {
                return SetReturn(ctx, InvalidParameters);
            }

            var infoSize = GetLegacyStreamInfoSize(ctx.TargetGeneration);
            Span<byte> info = stackalloc byte[infoSize];
            info.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(
                info[0..],
                GetStreamType(ctx.TargetGeneration, streamIndex));
            if (streamIndex == 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(info[8..], checked((uint)player.Width));
                BinaryPrimitives.WriteUInt32LittleEndian(info[12..], checked((uint)player.Height));
                BinaryPrimitives.WriteSingleLittleEndian(info[16..], (float)player.Width / player.Height);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(info[8..], 2);
                BinaryPrimitives.WriteUInt32LittleEndian(info[12..], 48_000);
            }
            BinaryPrimitives.WriteUInt64LittleEndian(info[24..], player.DurationMilliseconds);
            if (!ctx.Memory.TryWrite(infoAddress, info))
            {
                return SetReturn(ctx, InvalidParameters);
            }

            TraceOnce(
                $"stream_info_{streamIndex}_{infoSize}",
                $"stream_info index={streamIndex} size={infoSize} " +
                $"type={(streamIndex == 0 ? "video" : "audio")} duration_ms={player.DurationMilliseconds}");
            return SetReturn(ctx, 0);
        }
    }

    internal static bool MaterializeReplacementSourceForTest(
        CpuContext ctx,
        ulong handle,
        string guestPath,
        out string path)
    {
        path = string.Empty;
        PlayerState? player;
        lock (StateGate)
        {
            Players.TryGetValue(handle, out player);
        }

        if (player is null ||
            !TryMaterializeReplacementSource(ctx, player, guestPath, out path))
        {
            return false;
        }

        lock (StateGate)
        {
            if (!Players.TryGetValue(handle, out var currentPlayer) ||
                !ReferenceEquals(currentPlayer, player))
            {
                TryDeleteMaterializedSource(path);
                path = string.Empty;
                return false;
            }

            player.SourcePath = path;
            player.OwnedSourcePath = path;
            return true;
        }
    }

    private static int AddSource(CpuContext ctx, string guestPath)
    {
        PlayerState player;
        bool autoStart;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var foundPlayer))
            {
                return SetReturn(ctx, InvalidParameters);
            }
            player = foundPlayer;
        }

        var ownsHostPath = false;
        string? hostPath;
        if (player.FileOpenCallback != 0)
        {
            if (TryMaterializeReplacementSource(
                    ctx,
                    player,
                    guestPath,
                    out var materializedPath))
            {
                hostPath = materializedPath;
                ownsHostPath = true;
            }
            else
            {
                // A replacement interface is optional and may reject paths it
                // does not own. Preserve the ordinary sandboxed file path when
                // the same source is directly available and decodable.
                hostPath = ResolveGuestPath(guestPath);
            }
        }
        else
        {
            hostPath = ResolveGuestPath(guestPath);
        }

        if (hostPath is null ||
            !ProbeVideo(
                hostPath,
                out var width,
                out var height,
                out var fps,
                out var duration,
                out var hasAudio))
        {
            if (ownsHostPath && hostPath is not null)
            {
                TryDeleteMaterializedSource(hostPath);
            }
            Console.Error.WriteLine($"[AVPLAYER][ERROR] Could not open guest video '{guestPath}' (resolved '{hostPath ?? "<none>"}').");
            return SetReturn(ctx, OperationFailed);
        }

        string? previousOwnedSource;
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var currentPlayer) ||
                !ReferenceEquals(currentPlayer, player))
            {
                if (ownsHostPath)
                {
                    TryDeleteMaterializedSource(hostPath);
                }
                return SetReturn(ctx, InvalidParameters);
            }

            player.ResetPlayback();
            previousOwnedSource = player.OwnedSourcePath;
            player.SourcePath = hostPath;
            player.OwnedSourcePath = ownsHostPath ? hostPath : null;
            player.Width = width;
            player.Height = height;
            player.FramesPerSecond = fps;
            player.DurationMilliseconds = duration;
            player.HasAudio = hasAudio;
            player.Started = player.AutoStart;
            autoStart = player.AutoStart;
            Trace(
                $"source guest='{guestPath}' host='{hostPath}' {width}x{height} " +
                $"fps={fps:F3} duration_ms={duration} audio={hasAudio} auto_start={player.AutoStart}");
        }
        if (previousOwnedSource is not null && previousOwnedSource != hostPath)
        {
            TryDeleteMaterializedSource(previousOwnedSource);
        }
        NotifyEvent(ctx, player, 2); // StateReady
        if (autoStart)
        {
            NotifyEvent(ctx, player, 3); // StatePlay
        }
        return SetReturn(ctx, 0);
    }

    private static bool TryMaterializeReplacementSource(
        CpuContext ctx,
        PlayerState player,
        string guestPath,
        out string path)
    {
        lock (player.FileReplacementGate)
        {
            return TryMaterializeReplacementSourceCore(
                ctx,
                player,
                guestPath,
                out path);
        }
    }

    private static bool TryMaterializeReplacementSourceCore(
        CpuContext ctx,
        PlayerState player,
        string guestPath,
        out string path)
    {
        path = string.Empty;
        if (player.FileOpenCallback == 0 ||
            player.FileCloseCallback == 0 ||
            player.FileReadOffsetCallback == 0 ||
            player.FileSizeCallback == 0 ||
            GuestThreadExecution.Scheduler is not { } scheduler)
        {
            Console.Error.WriteLine(
                "[AVPLAYER][WARN] Replacement file interface is incomplete or the guest scheduler is unavailable.");
            return false;
        }

        if (player.ReplacementFileReadBuffer == 0)
        {
            if (!KernelMemoryCompatExports.TryAllocateHleData(
                    ctx,
                    ReplacementFileReadBufferSize,
                    0x1000,
                    out var readBuffer))
            {
                return false;
            }

            player.ReplacementFileReadBuffer = readBuffer;
        }

        var encodedPath = Encoding.UTF8.GetBytes(guestPath + '\0');
        if (encodedPath.Length > ReplacementFileReadBufferSize ||
            !ctx.Memory.TryWrite(player.ReplacementFileReadBuffer, encodedPath))
        {
            return false;
        }

        if (!scheduler.TryCallGuestFunction(
                ctx,
                player.FileOpenCallback,
                player.FileObject,
                player.ReplacementFileReadBuffer,
                0,
                0,
                0,
                "avplayer_file_open",
                out var openResult,
                out var openError) ||
            unchecked((int)openResult) < 0)
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][WARN] Replacement file open failed callback=0x{player.FileOpenCallback:X16}: " +
                $"{openError ?? $"result={unchecked((int)openResult)}"}");
            return false;
        }

        Trace(
            $"replacement_file open object=0x{player.FileObject:X16} " +
            $"callback=0x{player.FileOpenCallback:X16} result={unchecked((int)openResult)}");

        var materializedPath = Path.Combine(
            Path.GetTempPath(),
            "SharpEmu",
            "AvPlayer",
            Path.GetRandomFileName());
        var hostBuffer = ArrayPool<byte>.Shared.Rent(ReplacementFileReadBufferSize);
        try
        {
            if (!scheduler.TryCallGuestFunction(
                    ctx,
                    player.FileSizeCallback,
                    player.FileObject,
                    0,
                    0,
                    0,
                    0,
                    "avplayer_file_size",
                    out var size,
                    out var sizeError) ||
                size == 0 || size > long.MaxValue)
            {
                Console.Error.WriteLine(
                    $"[AVPLAYER][WARN] Replacement file size failed " +
                    $"object=0x{player.FileObject:X16} callback=0x{player.FileSizeCallback:X16}: " +
                    $"{sizeError ?? $"size={size}"}");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(materializedPath)!);
            using var output = new FileStream(
                materializedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                ReplacementFileReadBufferSize,
                FileOptions.SequentialScan);
            for (ulong position = 0; position < size;)
            {
                var requested = checked((uint)Math.Min(
                    (ulong)ReplacementFileReadBufferSize,
                    size - position));
                if (!scheduler.TryCallGuestFunction(
                        ctx,
                        player.FileReadOffsetCallback,
                        player.FileObject,
                        player.ReplacementFileReadBuffer,
                        position,
                        requested,
                        0,
                        0,
                        "avplayer_file_read_offset",
                        out var rawRead,
                        out var readError))
                {
                    Console.Error.WriteLine(
                        $"[AVPLAYER][WARN] Replacement file read failed callback=0x{player.FileReadOffsetCallback:X16}: " +
                        $"{readError ?? "guest callback failed"}");
                    return false;
                }

                var read = unchecked((int)rawRead);
                if (read <= 0 || (uint)read > requested ||
                    !ctx.Memory.TryRead(
                        player.ReplacementFileReadBuffer,
                        hostBuffer.AsSpan(0, read)))
                {
                    Console.Error.WriteLine(
                        $"[AVPLAYER][WARN] Replacement file read returned invalid length {read} at offset {position}.");
                    return false;
                }

                output.Write(hostBuffer, 0, read);
                position += checked((uint)read);
            }

            output.Flush();
            path = materializedPath;
            materializedPath = string.Empty;
            Trace($"replacement_source materialized bytes={size}");
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hostBuffer);
            if (!scheduler.TryCallGuestFunction(
                    ctx,
                    player.FileCloseCallback,
                    player.FileObject,
                    0,
                    0,
                    0,
                    0,
                    "avplayer_file_close",
                    out var closeResult,
                    out var closeError) ||
                unchecked((int)closeResult) < 0)
            {
                Console.Error.WriteLine(
                    $"[AVPLAYER][WARN] Replacement file close failed callback=0x{player.FileCloseCallback:X16}: " +
                    $"{closeError ?? $"result={unchecked((int)closeResult)}"}");
            }
            if (materializedPath.Length != 0)
            {
                TryDeleteMaterializedSource(materializedPath);
            }
        }
    }

    private static void TryDeleteMaterializedSource(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int GetVideoData(CpuContext ctx, bool extended)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Players.TryGetValue(ctx[CpuRegister.Rdi], out var player) ||
                infoAddress == 0 || !player.Started || player.EndOfStream ||
                player.SourcePath is null)
            {
                return SetReturn(ctx, 0);
            }

            if (player.Paused)
            {
                if (player.SeekVideoFramePending &&
                    EnsureDecoder(player) &&
                    ReadFrame(player) == VideoFrameReadResult.Ready)
                {
                    var seekFrameIndex = player.NextFrameIndex;
                    var seekTimestamp = player.StartTimeMilliseconds +
                        checked((ulong)Math.Round(
                            seekFrameIndex * 1000.0 /
                            Math.Max(1.0, player.FramesPerSecond)));
                    player.NextFrameIndex++;
                    if (WriteVideoFrame(
                            ctx,
                            player,
                            infoAddress,
                            seekTimestamp,
                            seekFrameIndex,
                            extended))
                    {
                        player.LastVideoTimestamp = seekTimestamp;
                        player.SeekVideoFramePending = false;
                        Trace(
                            $"seek_frame handle=0x{player.Handle:X16} ex={extended} " +
                            $"ts={seekTimestamp} data=0x{player.LastGuestBuffer:X16}");
                        return SetReturn(ctx, 1);
                    }
                }

                return SetReturn(ctx, 0);
            }

            if (!EnsureDecoder(player))
            {
                player.EndOfStream = true;
                return SetReturn(ctx, 0);
            }

            var fps = Math.Max(1.0, player.FramesPerSecond);
            if (player.AvSyncMode == AvSyncModeDefault)
            {
                var expectedFrame = CalculateExpectedDefaultSyncVideoFrame(
                    player.HasAudio,
                    player.NextAudioFrameIndex,
                    player.PlaybackClock.Elapsed.TotalSeconds,
                    fps) - player.SkippedFrameDebt;
                if (player.NextFrameIndex > expectedFrame)
                {
                    return SetReturn(ctx, 0);
                }

                var behind = expectedFrame - player.NextFrameIndex;
                if (behind > MaxCatchUpFrames)
                {
                    player.SkippedFrameDebt += behind - MaxCatchUpFrames;
                    expectedFrame = player.NextFrameIndex + MaxCatchUpFrames;
                    TraceOnce(
                        "catch_up_capped",
                        $"catch_up capped behind={behind} max={MaxCatchUpFrames} " +
                        $"fps={fps:F3} {player.Width}x{player.Height}");
                }

                while (player.NextFrameIndex < expectedFrame)
                {
                    var skippedFrame = ReadFrame(player);
                    if (skippedFrame == VideoFrameReadResult.Pending)
                    {
                        return SetReturn(ctx, 0);
                    }
                    if (skippedFrame == VideoFrameReadResult.End)
                    {
                        return FinishStream(ctx, player);
                    }
                    player.NextFrameIndex++;
                }
            }

            var frameResult = ReadFrame(player);
            if (frameResult == VideoFrameReadResult.Pending)
            {
                return SetReturn(ctx, 0);
            }
            if (frameResult == VideoFrameReadResult.End)
            {
                return FinishStream(ctx, player);
            }

            var frameIndex = player.NextFrameIndex;
            var timestamp = player.StartTimeMilliseconds +
                checked((ulong)Math.Round(frameIndex * 1000.0 / fps));
            player.NextFrameIndex++;
            if (!WriteVideoFrame(
                    ctx,
                    player,
                    infoAddress,
                    timestamp,
                    frameIndex,
                    extended))
            {
                return SetReturn(ctx, 0);
            }
            player.LastVideoTimestamp = timestamp;

            Trace($"video_frame handle=0x{player.Handle:X16} ex={extended} ts={timestamp} data=0x{player.LastGuestBuffer:X16}");
            return SetReturn(ctx, 1);
        }
    }

    private static int FinishStream(CpuContext ctx, PlayerState player)
    {
        if (player.Looping)
        {
            player.ResetPlayback();
            player.Started = true;
        }
        else
        {
            player.EndOfStream = true;
            player.PlaybackClock.Stop();
            player.FallbackPlayback?.Dispose();
            player.FallbackPlayback = null;
            if (player.FallbackPresentationPixels is not null)
            {
                player.FallbackPlaybackCompleted = true;
                player.FallbackCompletionPending = false;
                player.FallbackPlaybackCompletedTicks = Stopwatch.GetTimestamp();
            }
        }
        return SetReturn(ctx, 0);
    }

    private static bool EnsureDecoder(PlayerState player)
    {
        if (player.VideoDecoder is not null)
        {
            return true;
        }

        if (player.SourcePath is null)
        {
            return false;
        }

        if (!FfmpegMediaStream.TryOpenVideo(
                player.SourcePath,
                checked((int)player.Width),
                checked((int)player.Height),
                out var videoStream) ||
            videoStream is null)
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][ERROR] Could not open a video stream in '{player.SourcePath}'.");
            return false;
        }

        if (player.StartTimeMilliseconds != 0 &&
            !videoStream.TrySeekMilliseconds(player.StartTimeMilliseconds))
        {
            videoStream.Dispose();
            Console.Error.WriteLine(
                $"[AVPLAYER][ERROR] Could not seek video stream to " +
                $"{player.StartTimeMilliseconds} ms.");
            return false;
        }

        player.VideoDecoder = new VideoFrameQueue(
            videoStream,
            checked(player.Width * player.Height * 3 / 2),
            player.GuestBuffers.Length);
        if (!player.Paused)
        {
            player.PlaybackClock.Start();
        }
        Trace($"decoder_started source='{player.SourcePath}' {player.Width}x{player.Height} nv12");
        return true;
    }

    private static bool EnsureAudioDecoder(PlayerState player)
    {
        if (player.AudioDecoderOutput is not null)
        {
            return true;
        }

        if (player.SourcePath is null)
        {
            return false;
        }

        if (!FfmpegMediaStream.TryOpenAudio(player.SourcePath, out var audioStream) ||
            audioStream is null)
        {
            return false;
        }

        if (player.StartTimeMilliseconds != 0 &&
            !audioStream.TrySeekMilliseconds(player.StartTimeMilliseconds))
        {
            audioStream.Dispose();
            return false;
        }

        player.AudioDecoderOutput = audioStream;
        player.RawAudioFrame = new byte[1024 * FfmpegMediaStream.AudioChannels * sizeof(short)];
        Trace($"audio_decoder_started source='{player.SourcePath}' s16 stereo 48000");
        return true;
    }

    private static VideoFrameReadResult ReadFrame(PlayerState player)
    {
        if (player.VideoDecoder is null)
        {
            return VideoFrameReadResult.End;
        }

        var result = player.VideoDecoder.TryRead(out var frame);
        if (result == VideoFrameReadResult.Ready)
        {
            player.RawFrame = frame;
        }
        return result;
    }

    private static bool ReadExactly(Stream? stream, byte[] buffer)
    {
        if (stream is null)
        {
            return false;
        }
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static bool WriteVideoFrame(
        CpuContext ctx,
        PlayerState player,
        ulong infoAddress,
        ulong timestamp,
        long frameIndex,
        bool extended)
    {
        if (player.RawFrame is null)
        {
            return false;
        }

        var alignedWidth = AlignUp(player.Width, 16);
        var alignedHeight = AlignUp(player.Height, 16);
        var (pitch, bufferHeight) = GetFrameGeometry(player, extended);
        var bufferStride = CalculateNv12BufferSize(pitch, bufferHeight);
        if (player.GuestBuffers[0] == 0)
        {
            if (!AllocateGuestVideoBuffers(ctx, player, bufferStride))
            {
                return false;
            }
            player.GuestBufferStride = bufferStride;
            Trace(
                $"video_layout ex={extended} width={player.Width} height={player.Height} " +
                $"pitch={pitch} uv_offset={checked(pitch * bufferHeight)} size={bufferStride}");
        }

        var frameData = player.RawFrame;
        if (extended)
        {
            if (player.PaddedFrame is null || player.PaddedFrame.Length != bufferStride)
            {
                player.PaddedFrame = new byte[bufferStride];
            }
            CopyNv12ToGuestBuffer(
                player.RawFrame,
                player.PaddedFrame,
                player.Width,
                player.Height,
                player.Width,
                player.Width,
                pitch);
            frameData = player.PaddedFrame;
        }
        else if (alignedWidth != player.Width || alignedHeight != player.Height)
        {
            if (player.PaddedFrame is null || player.PaddedFrame.Length != bufferStride)
            {
                player.PaddedFrame = new byte[bufferStride];
            }
            player.PaddedFrame.AsSpan().Clear();
            for (var row = 0; row < player.Height; row++)
            {
                player.RawFrame.AsSpan(row * player.Width, player.Width)
                    .CopyTo(player.PaddedFrame.AsSpan(row * alignedWidth, player.Width));
            }
            var rawChromaOffset = player.Width * player.Height;
            var paddedChromaOffset = alignedWidth * alignedHeight;
            for (var row = 0; row < player.Height / 2; row++)
            {
                player.RawFrame.AsSpan(rawChromaOffset + (row * player.Width), player.Width)
                    .CopyTo(player.PaddedFrame.AsSpan(paddedChromaOffset + (row * alignedWidth), player.Width));
            }
            frameData = player.PaddedFrame;
        }

        var bufferAddress = player.GuestBuffers[player.NextGuestBuffer];
        player.NextGuestBuffer =
            (player.NextGuestBuffer + 1) % player.GuestBuffers.Length;
        player.LastGuestBuffer = bufferAddress;
        if (!ctx.Memory.TryWrite(bufferAddress, frameData))
        {
            return false;
        }
        if (player.TextureAllocatorFailed)
        {
            EnsureFallbackPlayback(player);
            if (ShouldCreateFallbackPoster(
                    player.FallbackPlaybackAttempted,
                    player.FallbackPlayback is not null,
                    player.FallbackPresentationPixels is not null))
            {
                // Keep one immediate poster frame while the background decoder
                // starts. Subsequent frames come from the bounded, scaled host
                // playback; converting every 4K NV12 guest frame here would
                // duplicate decoding work and dominate the emulation thread.
                var bgra = GC.AllocateUninitializedArray<byte>(
                    checked(player.Width * player.Height * 4));
                ConvertNv12ToBgra(
                    frameData,
                    pitch,
                    bufferHeight,
                    player.Width,
                    player.Height,
                    bgra);
                player.FallbackPresentationPixels = bgra;
                player.FallbackPresentationWidth = checked((uint)player.Width);
                player.FallbackPresentationHeight = checked((uint)player.Height);
                player.FallbackPresentationSerial =
                    Interlocked.Increment(ref _fallbackPresentationSerial);
                player.SkipFirstFallbackPlaybackFrame =
                    player.FallbackPlayback is not null;
            }
        }
        if (TraceVideoImages)
        {
            var traceIndex = Interlocked.Increment(ref _videoPayloadTraceCount);
            if (traceIndex <= 16)
            {
                var summary = GuestImageUploadPayloadDiagnostics.Summarize(frameData);
                Console.Error.WriteLine(
                    $"[AVPLAYER][TRACE] video_payload index={traceIndex - 1} " +
                    $"data=0x{bufferAddress:X16} bytes={frameData.Length} " +
                    $"pitch={pitch} uv_offset={checked(pitch * bufferHeight)} " +
                    $"nonzero_bytes={summary.NonzeroBytes}/{frameData.Length} " +
                    $"hash=0x{summary.Hash:X16}");
            }
        }

        Span<byte> info = extended
            ? stackalloc byte[FrameInfoExSize]
            : stackalloc byte[FrameInfoSize];
        info.Clear();
        WriteVideoFrameInfo(
            info,
            ctx.TargetGeneration,
            extended,
            bufferAddress,
            timestamp,
            checked((uint)pitch),
            checked((uint)player.Width),
            checked((uint)(extended ? player.Height : bufferHeight)),
            checked((uint)pitch),
            player.FramesPerSecond);
        if (!ctx.Memory.TryWrite(infoAddress, info))
        {
            return false;
        }

        if (player.FallbackPlayback is not null ||
            player.FallbackPresentationPixels is not null)
        {
            player.FallbackRequestedFrameIndex = Math.Max(
                player.FallbackRequestedFrameIndex,
                frameIndex);
        }
        return true;
    }

    private static (int Pitch, int Height) GetFrameGeometry(
        PlayerState player,
        bool extended)
    {
        var gen5Extended = extended && player.IsGen5;
        return (
            gen5Extended ? CalculateNv12Pitch(player.Width) : AlignUp(player.Width, 16),
            gen5Extended ? player.Height : AlignUp(player.Height, 16));
    }

    private static bool WriteHeldVideoFrameInfo(
        CpuContext ctx,
        PlayerState player,
        ulong infoAddress,
        bool extended)
    {
        var (pitch, bufferHeight) = GetFrameGeometry(player, extended);
        Span<byte> info = extended
            ? stackalloc byte[FrameInfoExSize]
            : stackalloc byte[FrameInfoSize];
        info.Clear();
        WriteVideoFrameInfo(
            info,
            ctx.TargetGeneration,
            extended,
            player.LastGuestBuffer,
            player.LastVideoTimestamp,
            checked((uint)pitch),
            checked((uint)player.Width),
            checked((uint)(extended ? player.Height : bufferHeight)),
            checked((uint)pitch),
            player.FramesPerSecond);
        return ctx.Memory.TryWrite(infoAddress, info);
    }

    /// <summary>
    /// The title-provided allocators can reject large decoded surfaces.  In
    /// that case the guest has no texture it can sample, and some titles pause
    /// their AvPlayer after acquiring a poster frame.  Keep that compatibility
    /// path useful by running a separate, bounded host playback to completion.
    /// MediaFramePlayback performs decode work off the Vulkan thread. The guest
    /// AvPlayer delivery index controls which fallback frame can be visible, so
    /// decode-ahead cannot bypass pause or client-controlled presentation.
    /// </summary>
    private static void EnsureFallbackPlayback(PlayerState player)
    {
        if (player.FallbackPlaybackAttempted || player.SourcePath is null)
        {
            return;
        }

        player.FallbackPlaybackAttempted = true;
        var videoOptions = HostVideoHost.CurrentOptions;
        var maximumWidth = checked((uint)videoOptions.Width);
        var maximumHeight = checked((uint)videoOptions.Height);
        if (!FfmpegVideoDecoder.TryOpen(
                player.SourcePath,
                maximumWidth,
                maximumHeight,
                out var decoder,
                enableAudio: false) ||
            decoder is null)
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][WARN] Could not start host fallback playback for '{player.SourcePath}'.");
            return;
        }

        player.FallbackPlayback = new MediaFramePlayback(decoder);
        Trace(
            $"host_fallback_started handle=0x{player.Handle:X16} " +
            $"source={player.Width}x{player.Height} output={decoder.Width}x{decoder.Height} " +
            $"host_limit={maximumWidth}x{maximumHeight} " +
            $"fps={decoder.FramesPerSecondNumerator}/{decoder.FramesPerSecondDenominator} " +
            "host_audio=false");
    }

    private static void CompleteGuestPlaybackAfterFallback(PlayerState player)
    {
        player.FallbackCompletionPending = false;
        player.EndOfStream = true;
        player.PlaybackClock.Stop();
    }

    internal static int CalculateNv12Pitch(int width) =>
        AlignUp(width, VideoPitchAlignment);

    internal static int CalculateNv12BufferSize(int pitch, int height) =>
        checked(pitch * height * 3 / 2);

    internal static void ConvertNv12ToBgra(
        ReadOnlySpan<byte> nv12,
        int pitch,
        int bufferHeight,
        int width,
        int height,
        Span<byte> bgra)
    {
        var requiredNv12 = CalculateNv12BufferSize(pitch, bufferHeight);
        var requiredBgra = checked(width * height * 4);
        if (pitch < width || bufferHeight < height ||
            nv12.Length < requiredNv12 || bgra.Length < requiredBgra)
        {
            throw new ArgumentException("NV12 frame dimensions do not match the supplied buffers.");
        }

        var chromaOffset = checked(pitch * bufferHeight);
        for (var y = 0; y < height; y++)
        {
            var lumaRow = y * pitch;
            var chromaRow = chromaOffset + ((y >> 1) * pitch);
            var outputRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var luma = nv12[lumaRow + x];
                var chromaColumn = x & ~1;
                var u = nv12[chromaRow + chromaColumn];
                var v = nv12[chromaRow + chromaColumn + 1];
                var c = Math.Max(0, luma - 16);
                var d = u - 128;
                var e = v - 128;
                var output = outputRow + (x * 4);
                bgra[output] = ClampToByte((298 * c + 516 * d + 128) >> 8);
                bgra[output + 1] = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
                bgra[output + 2] = ClampToByte((298 * c + 409 * e + 128) >> 8);
                bgra[output + 3] = byte.MaxValue;
            }
        }
    }

    private static byte ClampToByte(int value) =>
        checked((byte)Math.Clamp(value, byte.MinValue, byte.MaxValue));

    private static int GetVideoBufferSize(PlayerState player) =>
        checked(
            AlignUp(player.Width, FramePitchAlignment) *
            AlignUp(player.Height, FrameHeightAlignment) * 3 / 2);

    internal static void CopyNv12ToGuestBuffer(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int width,
        int height,
        int sourceLumaStride,
        int sourceChromaStride,
        int destinationPitch)
    {
        var sourceChromaOffset = checked(sourceLumaStride * height);
        var destinationChromaOffset = checked(destinationPitch * height);
        var destinationSize = CalculateNv12BufferSize(destinationPitch, height);
        destination[..destinationSize].Clear();

        for (var row = 0; row < height; row++)
        {
            source.Slice(row * sourceLumaStride, width)
                .CopyTo(destination.Slice(row * destinationPitch, width));
        }
        for (var row = 0; row < height / 2; row++)
        {
            source.Slice(sourceChromaOffset + (row * sourceChromaStride), width)
                .CopyTo(destination.Slice(destinationChromaOffset + (row * destinationPitch), width));
        }
    }

    private static bool AllocateGuestVideoBuffers(CpuContext ctx, PlayerState player, int bufferSize)
    {
        var scheduler = GuestThreadExecution.Scheduler;
        if (!player.TextureAllocatorFailed && scheduler is not null)
        {
            foreach (var (callback, kind) in new[]
                     {
                         (player.AllocateTextureCallback, "texture"),
                         (player.AllocateCallback, "generic"),
                     })
            {
                if (callback == 0)
                {
                    continue;
                }

                var allocated = true;
                for (var index = 0; index < player.GuestBuffers.Length; index++)
                {
                    if (!scheduler.TryCallGuestFunction(
                            ctx,
                            callback,
                            player.AllocatorObject,
                            TextureAllocationAlignment,
                            checked((ulong)bufferSize),
                            0,
                            0,
                            "avplayer_allocate_" + kind,
                            out var buffer,
                            out var error) || buffer == 0)
                    {
                        Console.Error.WriteLine(
                            $"[AVPLAYER][WARN] Guest {kind} allocation failed index={index} " +
                            $"callback=0x{callback:X16} size={bufferSize} " +
                            $"align=0x{TextureAllocationAlignment:X}: {error ?? "returned null"}");
                        allocated = false;
                        Array.Clear(player.GuestBuffers);
                        break;
                    }
                    player.GuestBuffers[index] = buffer;
                    RegisterVideoBuffer(buffer, bufferSize, index, "guest-callback");
                    Trace($"{kind}_buffer index={index} data=0x{buffer:X16} size={bufferSize}");
                }

                if (allocated)
                {
                    return true;
                }
            }
            player.TextureAllocatorFailed = true;
        }

        if (!KernelMemoryCompatExports.TryAllocateHleData(
                ctx,
                checked((ulong)bufferSize * (ulong)player.GuestBuffers.Length),
                0x1000,
                out var bufferBase))
        {
            return false;
        }
        for (var index = 0; index < player.GuestBuffers.Length; index++)
        {
            player.GuestBuffers[index] = bufferBase + checked((ulong)(index * bufferSize));
            RegisterVideoBuffer(player.GuestBuffers[index], bufferSize, index, "hle-fallback");
        }
        Console.Error.WriteLine("[AVPLAYER][WARN] Guest texture allocator unavailable; using generic HLE memory.");
        return true;
    }

    private static bool ProbeVideo(
        string path,
        out int width,
        out int height,
        out double framesPerSecond,
        out ulong durationMilliseconds,
        out bool hasAudio)
    {
        width = 0;
        height = 0;
        framesPerSecond = 30.0;
        durationMilliseconds = 0;
        hasAudio = false;

        if (!FfmpegMediaStream.TryProbe(path, out width, out height, out var rate, out var duration))
        {
            return false;
        }

        if (rate > 0)
        {
            framesPerSecond = rate;
        }

        if (duration > 0)
        {
            durationMilliseconds = checked((ulong)Math.Max(0, Math.Round(duration * 1000.0)));
        }

        hasAudio = FfmpegMediaStream.TryOpenAudio(path, out var audioStream) &&
                   audioStream is not null;
        audioStream?.Dispose();

        return width > 0 && height > 0 && framesPerSecond > 0;
    }

    internal static string? ResolveGuestPath(string guestPath)
    {
        if (string.IsNullOrWhiteSpace(guestPath))
        {
            return null;
        }

        var normalized = guestPath.Replace('\\', '/');
        var fileReference = normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        var unrealProjectRelative =
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.StartsWith("./", StringComparison.Ordinal);
        if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            uri.IsFile)
        {
            if (!string.IsNullOrEmpty(uri.Host) &&
                !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            normalized = uri.LocalPath.Replace('\\', '/');
        }
        else if (normalized.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            // Some console middleware emits Unreal-style project-relative
            // media references such as file://../../../Project/Content/....
            // System.Uri rejects these because the first ".." is parsed as
            // an invalid authority. Treat the scheme as a guest-path marker;
            // the app0 sandbox below resolves the relative path.
            normalized = normalized["file://".Length..];
            unrealProjectRelative = true;
        }
        else if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["file:".Length..];
            unrealProjectRelative = true;
        }

        if (unrealProjectRelative)
        {
            if (!TryRemoveUnrealLeadingDotSegments(normalized, out normalized))
            {
                return null;
            }
        }

        var app0 = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        if (string.IsNullOrWhiteSpace(app0))
        {
            return null;
        }

        var app0MountedPath = false;
        foreach (var prefix in new[] { "app0:/", "/app0/", "app0/", "app0:" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[prefix.Length..];
                app0MountedPath = true;
                break;
            }
        }

        if (!app0MountedPath &&
            (string.Equals(normalized, "app0:", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, "/app0", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalized, "app0", StringComparison.OrdinalIgnoreCase)))
        {
            normalized = string.Empty;
            app0MountedPath = true;
        }

        try
        {
            if (fileReference)
            {
                if (!TryDecodeFileReference(normalized, out normalized))
                {
                    return null;
                }
            }
            else if (ContainsInvalidMediaPathCharacters(normalized))
            {
                return null;
            }

            if ((!fileReference &&
                 !app0MountedPath &&
                 Uri.TryCreate(normalized, UriKind.Absolute, out _)) ||
                Path.IsPathFullyQualified(normalized) ||
                normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            if (!TryNormalizeApp0RelativePath(normalized, out var relativePath) ||
                relativePath.Length == 0)
            {
                return null;
            }

            var root = Path.GetFullPath(app0);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            var relativeToRoot = Path.GetRelativePath(root, candidate);
            if (Path.IsPathFullyQualified(relativeToRoot) ||
                string.Equals(relativeToRoot, "..", StringComparison.Ordinal) ||
                relativeToRoot.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return null;
            }

            return TryResolveSandboxedFile(root, relativePath, out var resolved)
                ? resolved
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                             IOException or
                                             NotSupportedException or
                                             UnauthorizedAccessException or
                                             UriFormatException)
        {
            return null;
        }
    }

    private static bool TryRemoveUnrealLeadingDotSegments(
        string guestPath,
        out string normalized)
    {
        var removedParent = false;
        while (guestPath.StartsWith("../", StringComparison.Ordinal) ||
               guestPath.StartsWith("./", StringComparison.Ordinal))
        {
            removedParent |= guestPath.StartsWith("../", StringComparison.Ordinal);
            guestPath = guestPath[(guestPath.IndexOf('/') + 1)..];
        }

        normalized = guestPath;
        return !removedParent || guestPath.Contains('/');
    }

    private static bool TryDecodeFileReference(string encoded, out string decoded)
    {
        decoded = string.Empty;
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] != '%')
            {
                continue;
            }

            if (index + 2 >= encoded.Length ||
                !Uri.IsHexDigit(encoded[index + 1]) ||
                !Uri.IsHexDigit(encoded[index + 2]))
            {
                return false;
            }

            var escapedByte = Convert.ToByte(encoded.Substring(index + 1, 2), 16);
            if (escapedByte is (byte)'/' or (byte)'\\')
            {
                return false;
            }

            index += 2;
        }

        decoded = Uri.UnescapeDataString(encoded);
        return !ContainsInvalidMediaPathCharacters(decoded);
    }

    private static bool ContainsInvalidMediaPathCharacters(string path) =>
        path.IndexOfAny(['?', '#']) >= 0 || path.Any(char.IsControl);

    private static bool TryNormalizeApp0RelativePath(
        string guestPath,
        out string relativePath)
    {
        var segments = new List<string>();
        foreach (var segment in guestPath.TrimStart('/').Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    relativePath = string.Empty;
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        relativePath = string.Join(Path.DirectorySeparatorChar, segments);
        return true;
    }

    private static bool TryResolveSandboxedFile(
        string root,
        string relativePath,
        out string resolved)
    {
        resolved = string.Empty;
        var current = root;
        var segments = relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var exact = Path.Combine(current, segments[index]);
            var finalSegment = index == segments.Length - 1;
            string? match;
            if (finalSegment ? File.Exists(exact) : Directory.Exists(exact))
            {
                match = exact;
            }
            else
            {
                if (!Directory.Exists(current))
                {
                    return false;
                }

                match = null;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (!string.Equals(
                            Path.GetFileName(entry),
                            segments[index],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        // A case-sensitive host can contain two names that are
                        // indistinguishable to the guest. Refuse an ambiguous
                        // media path instead of selecting one nondeterministically.
                        return false;
                    }

                    match = entry;
                }
            }

            if (match is null ||
                (finalSegment ? !File.Exists(match) : !Directory.Exists(match)))
            {
                return false;
            }

            if ((File.GetAttributes(match) & FileAttributes.ReparsePoint) != 0)
            {
                // App packages do not need host filesystem links. Refusing
                // them keeps media resolution inside the configured app0
                // tree even when a dump contains a symlink or junction.
                return false;
            }

            current = match;
        }

        if (!File.Exists(current))
        {
            return false;
        }

        resolved = Path.GetFullPath(current);
        return true;
    }

    internal static bool IsValidBgraFrame(
        ReadOnlySpan<byte> pixels,
        uint width,
        uint height)
    {
        if (width == 0 || height == 0)
        {
            return false;
        }

        var requiredBytes = (ulong)width * height * 4;
        return requiredBytes <= int.MaxValue &&
               pixels.Length >= checked((int)requiredBytes);
    }

    internal static bool IsGen5Target(Generation generation) =>
        (generation & Generation.Gen5) != 0;

    internal static ulong GetAutoStartOffset(Generation generation, bool extended) =>
        IsGen5Target(generation)
            ? extended ? 116UL : 108UL
            : extended ? 164UL : 108UL;

    internal static ulong GetOutputVideoFrameBufferCountOffset(
        Generation generation,
        bool extended) =>
        IsGen5Target(generation)
            ? extended ? 552UL : 104UL
            : extended ? 600UL : 104UL;

    internal static int NormalizeOutputVideoFrameBufferCount(int requested) =>
        requested is >= MinimumFrameBufferCount and <= MaximumFrameBufferCount
            ? requested
            : DefaultFrameBufferCount;

    internal static bool IsValidAvSyncMode(uint mode) =>
        mode is AvSyncModeDefault or AvSyncModeNone;

    internal static long CalculateExpectedDefaultSyncVideoFrame(
        bool hasAudio,
        long deliveredAudioFrameCount,
        double internalClockSeconds,
        double framesPerSecond)
    {
        var clockSeconds = hasAudio
            ? deliveredAudioFrameCount * AudioSamplesPerFrame / (double)AudioSampleRate
            : Math.Max(0, internalClockSeconds);
        return Math.Max(0, (long)Math.Floor(clockSeconds * Math.Max(1, framesPerSecond)));
    }

    private static int ReadOutputVideoFrameBufferCount(
        CpuContext ctx,
        ulong initDataAddress,
        bool extended)
    {
        var offset = GetOutputVideoFrameBufferCountOffset(
            ctx.TargetGeneration,
            extended);
        return TryReadUInt32(ctx, initDataAddress + offset, out var requested)
            ? NormalizeOutputVideoFrameBufferCount(unchecked((int)requested))
            : DefaultFrameBufferCount;
    }

    internal static int GetLegacyStreamInfoSize(Generation generation) =>
        IsGen5Target(generation)
            ? Gen5StreamInfoSize
            : Gen4StreamInfoSize;

    internal static uint GetStreamType(Generation generation, uint streamIndex) =>
        IsGen5Target(generation)
            ? streamIndex + 1
            : streamIndex;

    internal static void WriteGen5StreamInfoEx(
        Span<byte> info,
        uint streamType,
        uint width,
        uint height,
        double framesPerSecond,
        ulong durationMilliseconds)
    {
        if (info.Length < StreamInfoExSize)
        {
            throw new ArgumentException(
                $"Stream-info buffer must contain at least {StreamInfoExSize} bytes.",
                nameof(info));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(info[0..], StreamInfoExSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info[8..], streamType);
        BinaryPrimitives.WriteUInt32LittleEndian(info[16..], width);
        BinaryPrimitives.WriteUInt32LittleEndian(info[20..], height);
        BinaryPrimitives.WriteDoubleLittleEndian(info[0x40..], framesPerSecond);
        BinaryPrimitives.WriteUInt64LittleEndian(info[0x60..], durationMilliseconds);
    }

    internal static void WriteVideoFrameInfo(
        Span<byte> info,
        Generation generation,
        bool extended,
        ulong bufferAddress,
        ulong timestamp,
        uint width,
        uint visibleWidth,
        uint height,
        uint pitch,
        double framesPerSecond)
    {
        var requiredSize = extended ? FrameInfoExSize : FrameInfoSize;
        if (info.Length < requiredSize)
        {
            throw new ArgumentException(
                $"Frame-info buffer must contain at least {requiredSize} bytes.",
                nameof(info));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(info[0..], bufferAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(info[16..], timestamp);
        BinaryPrimitives.WriteUInt32LittleEndian(info[24..], width);
        BinaryPrimitives.WriteUInt32LittleEndian(info[28..], height);
        BinaryPrimitives.WriteSingleLittleEndian(info[32..], 1.0f);
        if (!extended)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            info[48..],
            width > visibleWidth ? width - visibleWidth : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(info[60..], pitch);
        info[64] = 8;
        info[65] = 8;
        if (IsGen5Target(generation))
        {
            BinaryPrimitives.WriteDoubleLittleEndian(info[0x48..], framesPerSecond);
        }
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0 || maxLength <= 0)
        {
            return false;
        }
        var bytes = new List<byte>(Math.Min(maxLength, 256));
        Span<byte> single = stackalloc byte[1];
        for (var index = 0; index < maxLength; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, single))
            {
                return false;
            }
            if (single[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes.ToArray());
                return true;
            }
            bytes.Add(single[0]);
        }
        return false;
    }

    private static bool TryReadUtf8(CpuContext ctx, ulong address, int length, out string value)
    {
        value = string.Empty;
        if (address == 0 || length <= 0)
        {
            return false;
        }
        var bytes = new byte[length];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            return false;
        }
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static bool TryReadByte(CpuContext ctx, ulong address, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }
        value = buffer[0];
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value) =>
        ctx.TryReadUInt64(address, out value);

    private static void NotifyWarning(CpuContext ctx, PlayerState player, int warning)
    {
        var eventDataBuffer = player.EventDataBuffer;
        if (eventDataBuffer == 0)
        {
            if (!KernelMemoryCompatExports.TryAllocateHleData(
                    ctx,
                    0x10,
                    0x10,
                    out eventDataBuffer))
            {
                Console.Error.WriteLine(
                    $"[AVPLAYER][WARN] Could not allocate event data for warning 0x{warning:X8}.");
                return;
            }

            player.EventDataBuffer = eventDataBuffer;
        }

        Span<byte> data = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(data, warning);
        if (!ctx.Memory.TryWrite(eventDataBuffer, data))
        {
            return;
        }

        NotifyEvent(ctx, player, 0x20, eventDataBuffer);
    }

    private static void NotifyEvent(CpuContext ctx, PlayerState player, ulong eventId) =>
        NotifyEvent(ctx, player, eventId, 0);

    private static void NotifyEvent(
        CpuContext ctx,
        PlayerState player,
        ulong eventId,
        ulong eventData)
    {
        if (player.EventCallback == 0)
        {
            Trace($"event skipped handle=0x{player.Handle:X16} id={eventId} callback=0");
            return;
        }

        var scheduler = GuestThreadExecution.Scheduler;
        string? error = null;
        if (scheduler is null ||
            !scheduler.TryCallGuestFunction(
                callerContext: ctx,
                entryPoint: player.EventCallback,
                arg0: player.EventObject,
                arg1: eventId,
                arg2: 0,
                arg3: eventData,
                stackAddress: 0,
                stackSize: 0,
                reason: $"avplayer_event_{eventId}",
                returnValue: out _,
                error: out error))
        {
            Console.Error.WriteLine(
                $"[AVPLAYER][WARN] Event callback failed handle=0x{player.Handle:X16} " +
                $"event={eventId} callback=0x{player.EventCallback:X16}: {error ?? "scheduler unavailable"}");
            return;
        }

        Trace($"event handle=0x{player.Handle:X16} id={eventId} callback=0x{player.EventCallback:X16}");
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) & -alignment);

    private static int ValidatePlayer(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(ctx, Players.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : InvalidParameters);
        }
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void Trace(string message)
    {
        var count = Interlocked.Increment(ref _traceCount);
        if (count <= 32 || count % 300 == 0)
        {
            Console.Error.WriteLine($"[AVPLAYER][INFO] {message}");
        }
    }

    private static void TraceOnce(string key, string message)
    {
        lock (TracedOnce)
        {
            if (!TracedOnce.Add(key))
            {
                return;
            }
        }

        Console.Error.WriteLine($"[AVPLAYER][INFO] {message}");
    }
}
