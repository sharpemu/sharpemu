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

    /// <summary>Call where an UnauthorizedAccessException on a host path becomes a guest errno.</summary>
    public static void ReportBlocked(string? hostPath)
    {
        if (string.IsNullOrEmpty(hostPath) || !InsideSandbox.Value || Logged.Count >= MaxLoggedPaths)
        {
            return;
        }

        if (Logged.TryAdd(hostPath, true))
        {
            Console.Error.WriteLine($"[SANDBOX][WARN] blocked access to {hostPath}");
        }
    }

    private static bool DetectSandbox() =>
        OperatingSystem.IsWindows() && IsAppContainer();

    [SupportedOSPlatform("windows")]
    private static bool IsAppContainer() => WindowsAppContainerSandbox.IsCurrentProcessAppContainer();
}
