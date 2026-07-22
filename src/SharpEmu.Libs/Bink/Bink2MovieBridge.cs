// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Bink;

/// <summary>
/// Optional host-side Bink 2 bridge for games that ship a static Bink player.
///
/// The game in that case never imports libSceVideodec, so an HLE video-decoder
/// export cannot see its movie frames. Kernel file opens identify the active
/// .bk2 file and the presenter requests BGRA frames from a tiny native adapter.
/// The adapter is deliberately a separate, user-supplied library: Bink 2 is a
/// proprietary SDK and SharpEmu must neither bundle it nor depend on its ABI.
/// </summary>
internal static class Bink2MovieBridge
{
    private const uint MaxDimension = 16384;
    private const uint MaxHostVideoWidth = 1920;
    private const uint MaxHostVideoHeight = 1080;

    private static readonly object Gate = new();
    private static string? _activePath;
    private static Bink2MovieInfo _activeInfo;
    private static byte[]? _frameBuffer;
    private static bool _frameBufferPresented;
    private static BinkFramePlayback? _playback;
    private static long _frameSerial;
    private static uint _presentationWidth = MaxHostVideoWidth;
    private static uint _presentationHeight = MaxHostVideoHeight;

    internal static bool IsHostPlaybackActive
    {
        get
        {
            lock (Gate)
            {
                return _playback is not null || _frameBuffer is not null;
            }
        }
    }

    internal static void SetPresentationSize(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (Gate)
        {
            _presentationWidth = Math.Min(width, MaxHostVideoWidth);
            _presentationHeight = Math.Min(height, MaxHostVideoHeight);
        }
    }

    /// <summary>
    /// Returns true only when movie skipping was explicitly requested. Without
    /// a host adapter the guest must be allowed to run the Bink implementation
    /// statically linked into its executable.
    /// </summary>
    internal static bool ShouldSkipGuestMovie(string hostPath) =>
        hostPath.EndsWith(".bk2", StringComparison.OrdinalIgnoreCase) &&
        ResolveMode() == MovieMode.Skip;

    /// <summary>
    /// Starts or queues host decoding. Decoded frames are only exposed as a
    /// sampled guest texture; presentation and UI composition remain guest-owned.
    /// </summary>
    internal static bool ObserveGuestMovie(string hostPath)
    {
        if (!hostPath.EndsWith(".bk2", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(hostPath))
        {
            return false;
        }

        lock (Gate)
        {
            if (string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase))
            {
                return _playback is not null || _frameBuffer is not null;
            }

            var mode = ResolveMode();
            if (mode is MovieMode.Guest or MovieMode.Skip)
            {
                return false;
            }

            if (_playback is not null || _frameBuffer is not null)
            {
                if (PendingMoviePathSet.Add(hostPath))
                {
                    PendingMoviePaths.Enqueue(hostPath);
                    Console.Error.WriteLine(
                        "[LOADER][INFO] Bink2 bridge queued: " +
                        Path.GetFileName(hostPath));
                }
                return PendingMoviePathSet.Contains(hostPath);
            }

            AttachMovieLocked(hostPath, mode);
            return string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase) &&
                   (_playback is not null || _frameBuffer is not null);
        }
    }

    internal static bool TryDecodeNextFrame(
        bool advanceClock,
        out byte[] pixels,
        out uint width,
        out uint height,
        out bool advanced,
        out long frameSerial,
        out string hostPath)
    {
        lock (Gate)
        {
            pixels = [];
            width = 0;
            height = 0;
            advanced = false;
            frameSerial = _frameSerial;
            hostPath = _activePath ?? string.Empty;

            if (_playback is not null)
            {
                if (!_playback.TryGetFrame(advanceClock, out pixels, out advanced))
                {
                    if (_playback.IsFinished)
                    {
                        var completedPath = _activePath;
                        CloseActiveLocked();
                        Console.Error.WriteLine(
                            "[LOADER][INFO] Bink2 bridge completed: " +
                            Path.GetFileName(completedPath));
                        AttachNextQueuedMovieLocked();
                    }
                    return false;
                }

                width = _activeInfo.Width;
                height = _activeInfo.Height;
                if (advanced)
                {
                    frameSerial = ++_frameSerial;
                }
                return true;
            }

            if (_frameBuffer is null)
            {
                return false;
            }

            pixels = _frameBuffer;
            width = _activeInfo.Width;
            height = _activeInfo.Height;
            advanced = !_frameBufferPresented;
            _frameBufferPresented = true;
            if (advanced)
            {
                frameSerial = ++_frameSerial;
            }
            return true;
        }
    }

    private static bool IsValid(Bink2MovieInfo info) =>
        info.Width > 0 && info.Height > 0 &&
        info.Width <= MaxDimension && info.Height <= MaxDimension &&
        (ulong)info.Width * info.Height * 4 <= int.MaxValue;

    private static int GetFrameBufferLength(Bink2MovieInfo info) =>
        checked((int)((ulong)info.Width * info.Height * 4));

    private static void AttachMovieLocked(string hostPath, MovieMode mode)
    {
        switch (mode)
        {
            case MovieMode.Dummy:
                AttachDummyMovieLocked(hostPath);
                return;
            case MovieMode.Ffmpeg:
                AttachFfmpegMovieLocked(hostPath);
                return;
            case MovieMode.Native:
                AttachNativeMovieLocked(hostPath);
                return;
        }
    }

    private static void AttachNativeMovieLocked(string hostPath)
    {
        if (!FfmpegNativeBinkFrameSource.TryOpen(
                hostPath, _presentationWidth, _presentationHeight, out var source) ||
            source is null)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge could not open movie '" +
                Path.GetFileName(hostPath) + "'.");
            return;
        }

        var info = new Bink2MovieInfo(
            source.Width, source.Height, source.FramesPerSecondNumerator, source.FramesPerSecondDenominator);
        if (!IsValid(info))
        {
            source.Dispose();
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink2 bridge rejected invalid movie dimensions for '" +
                Path.GetFileName(hostPath) + "'.");
            return;
        }

        AttachPlaybackLocked(hostPath, info, source);
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink2 bridge attached: " + Path.GetFileName(hostPath) + " " +
            info.Width + "x" + info.Height + " @ " +
            info.FramesPerSecondNumerator + "/" + info.FramesPerSecondDenominator + " fps.");
    }

    private static MovieMode ResolveMode()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_BINK_MODE");
        if (string.Equals(configured, "dummy", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Dummy;
        }

        if (string.Equals(configured, "native", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Native;
        }

        if (string.Equals(configured, "skip", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Skip;
        }

        if (string.Equals(configured, "guest", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Guest;
        }

        if (string.Equals(configured, "ffmpeg", StringComparison.OrdinalIgnoreCase))
        {
            return MovieMode.Ffmpeg;
        }

        // Native is the default: FfmpegNativeBinkFrameSource.TryOpen degrades
        // gracefully (falls back to the guest's own decode, logging one
        // informational line) if the FFmpeg libraries SharpEmu.CLI.csproj
        // downloads next to the executable are genuinely unavailable, so
        // defaulting to Native unconditionally is safe.
        return MovieMode.Native;
    }

    private static void AttachDummyMovieLocked(string hostPath)
    {
        if (!TryReadBinkInfo(hostPath, out var info) || !IsValid(info))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink dummy could not read movie header '" +
                Path.GetFileName(hostPath) + "'.");
            return;
        }

        CloseActiveLocked();
        _activePath = hostPath;
        _activeInfo = info;
        _frameBuffer = GC.AllocateUninitializedArray<byte>(GetFrameBufferLength(info));
        _frameBufferPresented = false;
        FillDummyFrame(_frameBuffer, info.Width, info.Height);
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink dummy attached: " + Path.GetFileName(hostPath) + " " +
            info.Width + "x" + info.Height + ".");
    }

    private static void AttachFfmpegMovieLocked(string hostPath)
    {
        if (!TryReadBinkInfo(hostPath, out var info) || !IsValid(info))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Bink FFmpeg source has an invalid header: " +
                Path.GetFileName(hostPath));
            return;
        }

        if (!FfmpegBinkFrameSource.TryOpen(
                hostPath,
                info.Width,
                info.Height,
                info.FramesPerSecondNumerator,
                info.FramesPerSecondDenominator,
                out var source) || source is null)
        {
            return;
        }

        AttachPlaybackLocked(hostPath, info, source);
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink FFmpeg source attached: " +
            Path.GetFileName(hostPath) + " " + info.Width + "x" + info.Height + " @ " +
            info.FramesPerSecondNumerator + "/" + info.FramesPerSecondDenominator + " fps.");
    }

    private static void AttachPlaybackLocked(
        string hostPath,
        Bink2MovieInfo info,
        IBinkFrameDecoder decoder)
    {
        CloseActiveLocked();
        _activePath = hostPath;
        _activeInfo = info;
        _playback = new BinkFramePlayback(decoder);
    }

    internal static bool TryReadBinkInfo(string path, out Bink2MovieInfo info)
    {
        info = default;
        Span<byte> header = stackalloc byte[36];
        try
        {
            using var stream = File.OpenRead(path);
            stream.ReadExactly(header);
            if (!header[..3].SequenceEqual("KB2"u8))
            {
                return false;
            }

            info = new Bink2MovieInfo(
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x14, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x18, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x1C, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x20, 4)));
            return info.FramesPerSecondNumerator != 0 &&
                   info.FramesPerSecondDenominator != 0;
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            return false;
        }
    }

    private static void FillDummyFrame(byte[] pixels, uint width, uint height)
    {
        for (var y = 0u; y < height; y++)
        {
            for (var x = 0u; x < width; x++)
            {
                var offset = checked((int)(((ulong)y * width + x) * 4));
                var band = ((x / 96) + (y / 96)) & 1;
                pixels[offset] = band == 0 ? (byte)0x28 : (byte)0x18;
                pixels[offset + 1] = band == 0 ? (byte)0x18 : (byte)0x28;
                pixels[offset + 2] = 0x10;
                pixels[offset + 3] = 0xFF;
            }
        }
    }


    private static void CloseActiveLocked()
    {
        _playback?.Dispose();
        _playback = null;
        _activePath = null;
        _activeInfo = default;
        _frameBuffer = null;
        _frameBufferPresented = false;

        // Wake any guest _read() blocked in WaitForHostPlaybackToFinish: its
        // movie either just finished or is being pre-empted by a new attach.
        Monitor.PulseAll(Gate);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct Bink2MovieInfo
    {
        public readonly uint Width;
        public readonly uint Height;
        public readonly uint FramesPerSecondNumerator;
        public readonly uint FramesPerSecondDenominator;

        internal Bink2MovieInfo(
            uint width,
            uint height,
            uint framesPerSecondNumerator,
            uint framesPerSecondDenominator)
        {
            Width = width;
            Height = height;
            FramesPerSecondNumerator = framesPerSecondNumerator;
            FramesPerSecondDenominator = framesPerSecondDenominator;
        }
    }

    private enum MovieMode
    {
        Guest,
        Skip,
        Dummy,
        Native,
        Ffmpeg,
    }

    private static readonly Queue<string> PendingMoviePaths = new();
    private static readonly HashSet<string> PendingMoviePathSet =
        new(StringComparer.OrdinalIgnoreCase);
    private static void AttachNextQueuedMovieLocked()
    {
        while (PendingMoviePaths.Count > 0)
        {
            var path = PendingMoviePaths.Dequeue();
            PendingMoviePathSet.Remove(path);
            if (!File.Exists(path))
            {
                continue;
            }

            AttachMovieLocked(path, ResolveMode());
            if (_playback is not null || _frameBuffer is not null)
            {
                return;
            }
        }
    }
    // Longest a guest _read() will block waiting for real host playback to
    // finish. A safety net, not a target: real movies finish well under
    // this. Bounds the damage if a movie fails to attach/decode after being
    // queued, so the guest thread doesn't hang forever.
    private const long MaxCompletionWaitMilliseconds = 5 * 60 * 1000;
    /// <summary>
    /// Blocks the calling (guest I/O) thread until the host has actually
    /// finished presenting <paramref name="hostPath"/> — either because it
    /// played through, or because something else took over the timeline.
    ///
    /// The completion shim tells the guest's own Bink header parse "this
    /// movie is one frame and already done" so its native decoder never
    /// blocks the guest on real per-frame work. Without this wait, that lie
    /// lands the instant the guest reads the header, so guest-side game
    /// logic races far ahead of whatever the host is still showing on
    /// screen: pressing a button lands on the (already-advanced) guest
    /// state, but the video visibly keeps playing, and any real-time-gated
    /// trigger later in the guest's own flow can fire against a clock that
    /// no longer matches wall time. Gating the "done" read on real host
    /// completion keeps guest pacing and on-screen playback in lockstep.
    /// </summary>
    internal static void WaitForHostPlaybackToFinish(string hostPath)
    {
        var deadline = Environment.TickCount64 + MaxCompletionWaitMilliseconds;
        lock (Gate)
        {
            while (IsTrackedLocked(hostPath))
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    Console.Error.WriteLine(
                        "[LOADER][WARN] Bink2 bridge completion wait timed out for '" +
                        Path.GetFileName(hostPath) + "'.");
                    return;
                }

                Monitor.Wait(Gate, (int)Math.Min(remaining, 200));
            }
        }
    }

    private static bool IsTrackedLocked(string hostPath) =>
        string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase) ||
        PendingMoviePathSet.Contains(hostPath);

    internal static bool TryTakeOverGuestMovie(
        string hostPath,
        out BinkGuestCompletionShim completionShim,
        out bool observed)
    {
        completionShim = default;
        observed = ObserveGuestMovie(hostPath);

        // Keep the real header visible so the guest creates its movie surface
        // and draw. Host-decoded pixels replace that sampled image later; a
        // one-frame completion shim would finish before the descriptor exists.
        return false;
    }

    internal static void NotifyGuestMovieClosed(string hostPath)
    {
        lock (Gate)
        {
            if (PendingMoviePathSet.Remove(hostPath))
            {
                var retained = PendingMoviePaths
                    .Where(path => !string.Equals(
                        path,
                        hostPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                PendingMoviePaths.Clear();
                foreach (var path in retained)
                {
                    PendingMoviePaths.Enqueue(path);
                }
            }

            if (!string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase))
            {
                Monitor.PulseAll(Gate);
                return;
            }

            Console.Error.WriteLine(
                "[LOADER][INFO] Bink2 bridge stopped by guest close: " +
                Path.GetFileName(hostPath));
            CloseActiveLocked();
            AttachNextQueuedMovieLocked();
        }
    }

    internal static bool TryReadGuestCompletionShim(
        string hostPath,
        out BinkGuestCompletionShim completionShim)
    {
        completionShim = default;
        Span<byte> header = stackalloc byte[48];
        try
        {
            using var stream = File.OpenRead(hostPath);
            stream.ReadExactly(header);
            if (!header[..3].SequenceEqual("KB2"u8))
            {
                return false;
            }

            var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
            var audioTrackCount = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
            if (frameCount < 2 || audioTrackCount > 256)
            {
                return false;
            }

            var revision = header[3];
            var frameIndexOffset = 44L + checked(12L * audioTrackCount);
            if (revision == (byte)'m')
            {
                frameIndexOffset += 16;
            }
            else if (revision is (byte)'i' or (byte)'j' or (byte)'k' or (byte)'n')
            {
                frameIndexOffset += 4;
            }

            Span<byte> frameOffsets = stackalloc byte[8];
            stream.Position = frameIndexOffset;
            stream.ReadExactly(frameOffsets);
            var firstFrameOffset = BinaryPrimitives.ReadUInt32LittleEndian(frameOffsets[..4]) & ~1u;
            var secondFrameOffset = BinaryPrimitives.ReadUInt32LittleEndian(frameOffsets[4..]) & ~1u;
            if (firstFrameOffset < frameIndexOffset + 8 ||
                secondFrameOffset <= firstFrameOffset ||
                secondFrameOffset > stream.Length)
            {
                return false;
            }

            completionShim = new BinkGuestCompletionShim(
                secondFrameOffset - 8,
                secondFrameOffset - firstFrameOffset);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or EndOfStreamException or OverflowException)
        {
            return false;
        }
    }

    internal readonly struct BinkGuestCompletionShim
    {
        private readonly uint _fileSizeMinusHeader;
        private readonly uint _largestFrameSize;

        internal BinkGuestCompletionShim(uint fileSizeMinusHeader, uint largestFrameSize)
        {
            _fileSizeMinusHeader = fileSizeMinusHeader;
            _largestFrameSize = largestFrameSize;
        }

        /// <summary>
        /// Rewrites the frame-count/size fields the guest's own Bink header
        /// parse reads, if this read covers them. Returns true when the
        /// NumFrames field (the field that tells the guest "this movie is
        /// done") was in range, so the caller can gate that specific read on
        /// the host's real playback actually finishing first.
        /// </summary>
        internal bool Patch(long fileOffset, Span<byte> bytes)
        {
            PatchUInt32(fileOffset, bytes, 4, _fileSizeMinusHeader);
            var touchedCompletionField = PatchUInt32(fileOffset, bytes, 8, 1);
            PatchUInt32(fileOffset, bytes, 12, _largestFrameSize);
            return touchedCompletionField;
        }

        private static bool PatchUInt32(
            long fileOffset,
            Span<byte> bytes,
            long fieldOffset,
            uint value)
        {
            var relativeOffset = fieldOffset - fileOffset;
            if (relativeOffset < 0 || relativeOffset + sizeof(uint) > bytes.Length)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.Slice((int)relativeOffset, sizeof(uint)),
                value);
            return true;
        }
    }
}
