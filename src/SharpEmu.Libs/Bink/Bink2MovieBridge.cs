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
    private static readonly object Gate = new();
    private static NativeAdapter? _adapter;
    private static string? _activePath;
    private static IntPtr _activeMovie;
    private static Bink2MovieInfo _activeInfo;
    private static byte[]? _frameBuffer;
    private static bool _usingDummyMovie;
    private static bool _loadAttempted;
    private static bool _availabilityReported;

    /// <summary>
    /// Returns true when the guest should receive a normal "file not found"
    /// result for a Bink movie. This is the safe default without a decoder:
    /// games that treat movies as optional fall through to their next state
    /// rather than submitting an empty Bink GPU texture forever.
    /// </summary>
    internal static bool ShouldSkipGuestMovie(string hostPath) =>
        hostPath.EndsWith(".bk2", StringComparison.OrdinalIgnoreCase) &&
        ResolveMode() == MovieMode.Skip;

    internal static void ObserveGuestMovie(string hostPath)
    {
        if (!hostPath.EndsWith(".bk2", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(hostPath))
        {
            return;
        }

        lock (Gate)
        {
            if (string.Equals(_activePath, hostPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (ResolveMode() == MovieMode.Dummy)
            {
                AttachDummyMovieLocked(hostPath);
                return;
            }

            var adapter = GetAdapterLocked();
            if (adapter is null)
            {
                return;
            }

            CloseActiveLocked();
            if (!adapter.TryOpen(hostPath, out var movie, out var info))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Bink2 bridge could not open movie '" +
                    Path.GetFileName(hostPath) + "'.");
                return;
            }

            if (!IsValid(info))
            {
                adapter.Close(movie);
                Console.Error.WriteLine(
                    "[LOADER][WARN] Bink2 bridge rejected invalid movie dimensions for '" +
                    Path.GetFileName(hostPath) + "'.");
                return;
            }

            _activePath = hostPath;
            _activeMovie = movie;
            _activeInfo = info;
            _frameBuffer = GC.AllocateUninitializedArray<byte>(GetFrameBufferLength(info));
            Console.Error.WriteLine(
                "[LOADER][INFO] Bink2 bridge attached: " + Path.GetFileName(hostPath) + " " +
                info.Width + "x" + info.Height + " @ " +
                info.FramesPerSecondNumerator + "/" + info.FramesPerSecondDenominator + " fps.");
        }
    }

    internal static bool TryDecodeNextFrame(
        out byte[] pixels,
        out uint width,
        out uint height)
    {
        lock (Gate)
        {
            pixels = [];
            width = 0;
            height = 0;
            if (_adapter is null || _activeMovie == IntPtr.Zero || _frameBuffer is null)
            {
                if (_usingDummyMovie && _frameBuffer is not null)
                {
                    pixels = _frameBuffer;
                    width = _activeInfo.Width;
                    height = _activeInfo.Height;
                    return true;
                }

                return false;
            }

            unsafe
            {
                fixed (byte* destination = _frameBuffer)
                {
                    if (!_adapter.DecodeNextBgra(
                            _activeMovie,
                            (IntPtr)destination,
                            _activeInfo.Width * 4,
                            (uint)_frameBuffer.Length))
                    {
                        return false;
                    }
                }
            }

            pixels = _frameBuffer;
            width = _activeInfo.Width;
            height = _activeInfo.Height;
            return true;
        }
    }

    private static bool IsValid(Bink2MovieInfo info) =>
        info.Width > 0 && info.Height > 0 &&
        info.Width <= MaxDimension && info.Height <= MaxDimension &&
        (ulong)info.Width * info.Height * 4 <= int.MaxValue;

    private static int GetFrameBufferLength(Bink2MovieInfo info) =>
        checked((int)((ulong)info.Width * info.Height * 4));

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

        // With no SDK adapter present, returning "not found" makes optional
        // cinematics advance. Supplying either an explicit path or the normal
        // side-by-side adapter enables native playback automatically.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHARPEMU_BINK2_BRIDGE")) ||
            EnumerateAdapterCandidates().Any(File.Exists))
        {
            return MovieMode.Native;
        }

        return MovieMode.Skip;
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
        FillDummyFrame(_frameBuffer, info.Width, info.Height);
        _usingDummyMovie = true;
        Console.Error.WriteLine(
            "[LOADER][INFO] Bink dummy attached: " + Path.GetFileName(hostPath) + " " +
            info.Width + "x" + info.Height + ".");
    }

    private static bool TryReadBinkInfo(string path, out Bink2MovieInfo info)
    {
        info = default;
        Span<byte> header = stackalloc byte[32];
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Read(header) != header.Length ||
                !header[..4].SequenceEqual("KB2j"u8))
            {
                return false;
            }

            info = new Bink2MovieInfo(
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x14, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x18, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x1C, 4)),
                1);
            return true;
        }
        catch (IOException)
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

    private static NativeAdapter? GetAdapterLocked()
    {
        if (_loadAttempted)
        {
            return _adapter;
        }

        _loadAttempted = true;
        foreach (var candidate in EnumerateAdapterCandidates())
        {
            if (!NativeLibrary.TryLoad(candidate, out var library))
            {
                continue;
            }

            if (NativeAdapter.TryCreate(library, out var adapter))
            {
                _adapter = adapter;
                Console.Error.WriteLine("[LOADER][INFO] Bink2 bridge loaded: " + candidate);
                return adapter;
            }

            NativeLibrary.Free(library);
        }

        if (!_availabilityReported)
        {
            _availabilityReported = true;
            Console.Error.WriteLine(
                "[LOADER][INFO] Bink2 bridge unavailable; install the licensed adapter and set SHARPEMU_BINK2_BRIDGE.");
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAdapterCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_BINK2_BRIDGE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(baseDirectory, "libsharpemu_bink2_bridge.dylib");
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(baseDirectory, "sharpemu_bink2_bridge.dll");
        }
        else
        {
            yield return Path.Combine(baseDirectory, "libsharpemu_bink2_bridge.so");
        }
    }

    private static void CloseActiveLocked()
    {
        if (_activeMovie != IntPtr.Zero)
        {
            _adapter?.Close(_activeMovie);
        }

        _activePath = null;
        _activeMovie = IntPtr.Zero;
        _activeInfo = default;
        _frameBuffer = null;
        _usingDummyMovie = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Bink2MovieInfo
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
        Skip,
        Dummy,
        Native,
    }

    private sealed class NativeAdapter
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenUtf8Delegate(IntPtr pathUtf8, out IntPtr movie, out Bink2MovieInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecodeNextBgraDelegate(IntPtr movie, IntPtr destination, uint stride, uint destinationBytes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseDelegate(IntPtr movie);

        private readonly OpenUtf8Delegate _openUtf8;
        private readonly DecodeNextBgraDelegate _decodeNextBgra;
        private readonly CloseDelegate _close;

        private NativeAdapter(
            OpenUtf8Delegate openUtf8,
            DecodeNextBgraDelegate decodeNextBgra,
            CloseDelegate close)
        {
            _openUtf8 = openUtf8;
            _decodeNextBgra = decodeNextBgra;
            _close = close;
        }

        internal static bool TryCreate(IntPtr library, out NativeAdapter? adapter)
        {
            adapter = null;
            if (!NativeLibrary.TryGetExport(library, "sharpemu_bink2_open_utf8", out var open) ||
                !NativeLibrary.TryGetExport(library, "sharpemu_bink2_decode_next_bgra", out var decode) ||
                !NativeLibrary.TryGetExport(library, "sharpemu_bink2_close", out var close))
            {
                return false;
            }

            adapter = new NativeAdapter(
                Marshal.GetDelegateForFunctionPointer<OpenUtf8Delegate>(open),
                Marshal.GetDelegateForFunctionPointer<DecodeNextBgraDelegate>(decode),
                Marshal.GetDelegateForFunctionPointer<CloseDelegate>(close));
            return true;
        }

        internal bool TryOpen(string path, out IntPtr movie, out Bink2MovieInfo info)
        {
            var utf8 = Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                return _openUtf8(utf8, out movie, out info) != 0 && movie != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeCoTaskMem(utf8);
            }
        }

        internal bool DecodeNextBgra(IntPtr movie, IntPtr destination, uint stride, uint destinationBytes) =>
            _decodeNextBgra(movie, destination, stride, destinationBytes) != 0;

        internal void Close(IntPtr movie) => _close(movie);
    }
}
