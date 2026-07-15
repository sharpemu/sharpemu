// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.HLE;

/// <summary>
/// Runs work on the real process main thread. macOS only allows AppKit (and
/// therefore GLFW windowing) on that thread, so the CLI moves emulation onto
/// a worker thread, parks the main thread in <see cref="Pump"/>, and the
/// video presenter posts its window loop here. On other platforms
/// <see cref="IsAvailable"/> stays false and nothing changes.
/// </summary>
public static class HostMainThread
{
    private static readonly BlockingCollection<Action> _work = new();
    private static Action? _shutdownRequestHandler;

    public static bool IsAvailable { get; private set; }

    /// <summary>
    /// Registers a callback invoked by <see cref="Shutdown"/> so a
    /// long-running posted work item (the presenter's window loop) can be
    /// asked to return to the pump.
    /// </summary>
    public static void SetShutdownRequestHandler(Action handler) =>
        _shutdownRequestHandler = handler;

    /// <summary>Marks the pump as present. Call before guest code can run.</summary>
    public static void Enable() => IsAvailable = true;

    public static void Post(Action work)
    {
        try
        {
            _work.Add(work);
        }
        catch (InvalidOperationException)
        {
            // Shutdown already requested; the process is exiting.
        }
    }

    /// <summary>
    /// Services posted work on the calling (main) thread until
    /// <see cref="Shutdown"/> is called and the queue drains.
    /// </summary>
    public static void Pump()
    {
        foreach (var work in _work.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[LOADER][ERROR] Main-thread work failed: {exception}");
            }
        }
    }

    public static void Shutdown()
    {
        IsAvailable = false;
        try
        {
            _shutdownRequestHandler?.Invoke();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Main-thread shutdown handler failed: {exception.Message}");
        }

        _work.CompleteAdding();
    }
}
