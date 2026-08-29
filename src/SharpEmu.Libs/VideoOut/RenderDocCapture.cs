// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.VideoOut;

public static unsafe class RenderDocCapture
{
    private const int ApiVersion1_4_2 = 10402;

    private const int IndexUnloadCrashHandler = 10;
    private const int IndexSetCaptureFilePathTemplate = 11;
    private const int IndexGetNumCaptures = 13;
    private const int IndexGetCapture = 14;
    private const int IndexStartFrameCapture = 19;
    private const int IndexIsFrameCapturing = 20;
    private const int IndexEndFrameCapture = 21;
    private const int IndexDiscardFrameCapture = 24;

    private const int StateIdle = 0;
    private const int StateRequested = 1;
    private const int StateCapturing = 2;

    private static IntPtr* _api;
    private static int _state = StateIdle;
    private static bool _initialized;
    private static long _captureStartedTick;
    private static long _captureStartGuestFlipVersion;
    private static readonly long _captureTimeoutMilliseconds =
        long.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_CAPTURE_TIMEOUT_SECONDS"),
            out var captureTimeoutSeconds) && captureTimeoutSeconds > 0
            ? Math.Clamp(captureTimeoutSeconds, 1, 120) * 1_000
            : 15_000;

    public static bool IsAvailable => _api is not null;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        if (!TryLoadLibrary(out var module))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] renderdoc: SHARPEMU_RENDERDOC=1 was set but renderdoc.dll could not be loaded.");
            return;
        }

        if (!NativeLibrary.TryGetExport(module, "RENDERDOC_GetAPI", out var getApiAddress))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] renderdoc: RENDERDOC_GetAPI is missing; in-app capture disabled.");
            return;
        }

        void* api = null;
        var getApi = (delegate* unmanaged[Cdecl]<int, void**, int>)getApiAddress;
        if (getApi(ApiVersion1_4_2, &api) != 1 || api is null)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] renderdoc: API 1.4.2 unavailable; in-app capture disabled.");
            return;
        }

        _api = (IntPtr*)api;

        ((delegate* unmanaged[Cdecl]<void>)_api[IndexUnloadCrashHandler])();

        Console.Error.WriteLine(
            "[LOADER][INFO] renderdoc: in-app capture ready. Press F10 to capture the next complete guest frame.");
    }

    public static void SetCaptureDirectory(string titleId)
    {
        if (_api is null)
        {
            return;
        }

        var safeTitleId = string.IsNullOrWhiteSpace(titleId) ? "UNKNOWN" : titleId.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            safeTitleId = safeTitleId.Replace(invalid, '_');
        }

        try
        {
            var directory = Path.Combine(
                AppContext.BaseDirectory,
                "user",
                "logs",
                "capture_logs",
                safeTitleId);
            Directory.CreateDirectory(directory);

            var template = Path.Combine(directory, safeTitleId);
            var bytes = System.Text.Encoding.UTF8.GetBytes(template + "\0");
            fixed (byte* pointer = bytes)
            {
                ((delegate* unmanaged[Cdecl]<byte*, void>)_api[IndexSetCaptureFilePathTemplate])(
                    pointer);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] renderdoc: captures will be written under '{directory}'.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] renderdoc: could not set the capture directory: {exception.Message}");
        }
    }

    public static void RequestCapture()
    {
        if (_api is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _state, StateRequested, StateIdle) == StateIdle)
        {
            Console.Error.WriteLine(
                "[LOADER][INFO] renderdoc: capture requested; waiting for the next guest-flip boundary.");
        }
    }

    /// <summary>
    /// Marks a successfully submitted guest-flip boundary. A requested
    /// capture starts after the first boundary and ends after the next one,
    /// so asynchronous guest work remains inside one capture even when it is
    /// drained across several host-presenter ticks.
    /// </summary>
    public static void OnGuestFlipBoundary(long version)
    {
        if (_api is null)
        {
            return;
        }

        var state = Volatile.Read(ref _state);
        if (state == StateCapturing)
        {
            EndGuestFrame(version);
            return;
        }

        if (state != StateRequested)
        {
            return;
        }

        if (IsFrameCapturing())
        {
            Volatile.Write(ref _state, StateIdle);
            return;
        }

        StartFrameCapture();
        if (!IsFrameCapturing())
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] renderdoc: StartFrameCapture did not begin a capture.");
            Volatile.Write(ref _state, StateIdle);
            return;
        }

        Volatile.Write(ref _state, StateCapturing);
        Volatile.Write(ref _captureStartedTick, Environment.TickCount64);
        Volatile.Write(ref _captureStartGuestFlipVersion, version);
        Console.Error.WriteLine(
            $"[LOADER][INFO] renderdoc: guest-frame capture started after flip v{version}.");
    }

    public static void DiscardTimedOutFrame()
    {
        if (_api is null || Volatile.Read(ref _state) != StateCapturing)
        {
            return;
        }

        var elapsed = Environment.TickCount64 - Volatile.Read(ref _captureStartedTick);
        if (elapsed < _captureTimeoutMilliseconds ||
            Interlocked.CompareExchange(ref _state, StateIdle, StateCapturing) !=
                StateCapturing)
        {
            return;
        }

        _ = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint>)
            _api[IndexDiscardFrameCapture])(IntPtr.Zero, IntPtr.Zero);
        Console.Error.WriteLine(
            $"[LOADER][WARN] renderdoc: discarded a guest-frame capture after {elapsed} ms without the next guest flip.");
    }

    private static void EndGuestFrame(long version)
    {
        if (_api is null ||
            Interlocked.CompareExchange(ref _state, StateIdle, StateCapturing) !=
                StateCapturing)
        {
            return;
        }

        if (EndFrameCapture() != 0)
        {
            var startVersion = Volatile.Read(ref _captureStartGuestFlipVersion);
            Console.Error.WriteLine(
                $"[LOADER][INFO] renderdoc: guest-frame capture ended at flip v{version} " +
                $"(v{startVersion}->v{version}).");
            LogNewestCapture();
        }
        else
        {
            Console.Error.WriteLine("[LOADER][WARN] renderdoc: EndFrameCapture failed.");
        }
    }

    public static void DiscardFrame()
    {
        if (_api is null ||
            Interlocked.CompareExchange(ref _state, StateIdle, StateCapturing) !=
                StateCapturing)
        {
            return;
        }

        _ = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint>)
            _api[IndexDiscardFrameCapture])(IntPtr.Zero, IntPtr.Zero);
        Console.Error.WriteLine(
            "[LOADER][WARN] renderdoc: discarded an interrupted frame capture.");
    }


    private static void StartFrameCapture() =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)_api[IndexStartFrameCapture])(
            IntPtr.Zero,
            IntPtr.Zero);

    private static bool IsFrameCapturing() =>
        ((delegate* unmanaged[Cdecl]<uint>)_api[IndexIsFrameCapturing])() != 0;

    private static uint EndFrameCapture() =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint>)_api[IndexEndFrameCapture])(
            IntPtr.Zero,
            IntPtr.Zero);

    private static void LogNewestCapture()
    {
        var count = ((delegate* unmanaged[Cdecl]<uint>)_api[IndexGetNumCaptures])();
        if (count == 0)
        {
            return;
        }

        var getCapture =
            (delegate* unmanaged[Cdecl]<uint, byte*, uint*, ulong*, uint>)_api[IndexGetCapture];

        uint pathLength = 0;
        if (getCapture(count - 1, null, &pathLength, null) == 0 || pathLength == 0)
        {
            return;
        }

        var buffer = new byte[pathLength];
        fixed (byte* bufferPointer = buffer)
        {
            if (getCapture(count - 1, bufferPointer, &pathLength, null) == 0)
            {
                return;
            }
        }

        var path = System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        Console.Error.WriteLine($"[LOADER][INFO] renderdoc: capture written to '{path}'.");
    }

    private static bool TryLoadLibrary(out IntPtr module)
    {
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_DLL");
        if (!string.IsNullOrWhiteSpace(configured) &&
            NativeLibrary.TryLoad(configured, out module))
        {
            return true;
        }

        var name = OperatingSystem.IsWindows() ? "renderdoc.dll" : "librenderdoc.so";
        if (NativeLibrary.TryLoad(name, out module))
        {
            return true;
        }

        foreach (var candidate in KnownLibraryPaths(name))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out module))
            {
                return true;
            }
        }

        module = IntPtr.Zero;
        return false;
    }

    private static IEnumerable<string> KnownLibraryPaths(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return "/usr/lib/librenderdoc.so";
            yield return "/usr/local/lib/librenderdoc.so";
            yield break;
        }

        foreach (var variable in (string[])["ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"])
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, "RenderDoc", name);
            }
        }

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "RenderDoc", name);
        }
    }
}
