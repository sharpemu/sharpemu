// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Diagnostics.Core;

/// <summary>
/// Single source of truth for timestamps. All plugins and events use this
/// instead of DateTime.Now or Stopwatch directly, ensuring consistent
/// timing across the diagnostic subsystem.
/// </summary>
public static class DiagnosticClock
{
    private static readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Monotonic milliseconds since emulator start.</summary>
    public static double ElapsedMs => _sw.Elapsed.TotalMilliseconds;

    /// <summary>Monotonic ticks (high resolution).</summary>
    public static long Ticks => System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>ISO 8601 wall-clock timestamp (for reports only, not for ordering).</summary>
    public static string UtcNow => DateTime.UtcNow.ToString("o");
}
