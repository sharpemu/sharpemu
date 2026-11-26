// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// Logs the host paths a sandboxed guest could not access, once per path and for a bounded number of paths. It
/// only logs: the errno the guest sees is decided by the caller.
/// </summary>
public static class SandboxAccessLog
{
    private const int MaxLoggedPaths = 50;

    private static readonly ConcurrentDictionary<string, bool> Logged = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<bool> InsideSandbox = new(DetectSandbox);

    // Lets a test act as if the process were (or were not) inside an AppContainer.
    internal static bool? SandboxOverride { get; set; }

    internal static int LoggedCount => Logged.Count;

    internal static void ResetForTests() => Logged.Clear();

    /// <summary>Call where an UnauthorizedAccessException on a host path becomes a guest errno.</summary>
    public static void ReportBlocked(string? hostPath)
    {
        if (string.IsNullOrEmpty(hostPath) || !(SandboxOverride ?? InsideSandbox.Value) || Logged.Count >= MaxLoggedPaths)
        {
            return;
        }

        if (Logged.TryAdd(hostPath, true))
        {
            Console.Error.WriteLine($"[SANDBOX][WARN] blocked access to {hostPath}");
        }
    }

    /// <summary>
    /// Same, for a catch that has no path at hand: the runtime names it in the message
    /// ("Access to the path 'X' is denied."), otherwise the message itself is logged.
    /// </summary>
    public static void ReportBlocked(UnauthorizedAccessException exception) =>
        ReportBlocked(PathFromMessage(exception.Message));

    internal static string PathFromMessage(string message)
    {
        var first = message.IndexOf('\'');
        var last = message.LastIndexOf('\'');
        return first >= 0 && last > first + 1 ? message[(first + 1)..last] : message;
    }

    private static bool DetectSandbox() =>
        OperatingSystem.IsWindows() && IsAppContainer();

    [SupportedOSPlatform("windows")]
    private static bool IsAppContainer() => WindowsAppContainerSandbox.IsCurrentProcessAppContainer();
}
