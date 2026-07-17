// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Lets host-facing libraries (VideoOut, AudioOut) request cooperative guest
/// shutdown without taking a dependency on SharpEmu.Core.
/// </summary>
public static class HostSessionControl
{
    private static Action<string>? _shutdownHandler;
    private static string? _pendingShutdownReason;
    private static int _shutdownRequested;
    private static long _embeddedHostWindow;
    private static long _embeddedHostDisplay;

    /// <summary>
    /// Indicates that the active host session is being stopped. Runtime code
    /// uses this to skip expensive post-exit diagnostics before returning the
    /// GUI to its library.
    /// </summary>
    public static bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    /// <summary>
    /// Native GUI surface used by an isolated emulator child. Input backends
    /// use it to treat the launcher window as the active game window.
    /// </summary>
    public static nint EmbeddedHostWindow => unchecked((nint)Interlocked.Read(ref _embeddedHostWindow));

    /// <summary>X11 Display* paired with <see cref="EmbeddedHostWindow"/> when available.</summary>
    public static nint EmbeddedHostDisplay => unchecked((nint)Interlocked.Read(ref _embeddedHostDisplay));

    public static void SetEmbeddedHostSurface(nint window, nint display = 0)
    {
        Interlocked.Exchange(ref _embeddedHostDisplay, unchecked((long)display));
        Interlocked.Exchange(ref _embeddedHostWindow, unchecked((long)window));
    }

    /// <summary>
    /// Starts a fresh session after the previous guest has fully left its
    /// execution backend.
    /// </summary>
    public static void ResetShutdownRequest()
    {
        Interlocked.Exchange(ref _pendingShutdownReason, null);
        Volatile.Write(ref _shutdownRequested, 0);
    }

    public static void SetShutdownHandler(Action<string>? handler)
    {
        Volatile.Write(ref _shutdownHandler, handler);
        if (handler is null)
        {
            Interlocked.Exchange(ref _pendingShutdownReason, null);
            return;
        }

        var pendingReason = Interlocked.Exchange(ref _pendingShutdownReason, null);
        if (pendingReason is not null)
        {
            Invoke(handler, pendingReason);
        }
    }

    public static void RequestShutdown(string reason)
    {
        Volatile.Write(ref _shutdownRequested, 1);
        var handler = Volatile.Read(ref _shutdownHandler);
        if (handler is not null)
        {
            Invoke(handler, reason);
            return;
        }

        // Stop can be pressed while the GUI session is starting. Retain the
        // request until the native backend installs its cooperative handler.
        Volatile.Write(ref _pendingShutdownReason, reason);
        handler = Volatile.Read(ref _shutdownHandler);
        if (handler is not null)
        {
            var pendingReason = Interlocked.Exchange(ref _pendingShutdownReason, null);
            if (pendingReason is not null)
            {
                Invoke(handler, pendingReason);
            }
        }
    }

    private static void Invoke(Action<string> handler, string reason)
    {
        try
        {
            handler(reason);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Host shutdown handler failed: {exception.Message}");
        }
    }
}
