// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Threading;

namespace SharpEmu.HLE;

/// <summary>
/// Process-wide liveness stamp that long-running HLE work reports into so the CPU
/// backend's stall watchdog can tell legitimate slow work from a genuine hang.
/// </summary>
/// <remarks>
/// Lives in SharpEmu.HLE because it is the one project both the CPU backend
/// (SharpEmu.Core, which owns the watchdog) and the HLE library
/// (SharpEmu.Libs, which does the slow work) already reference; the dependency
/// runs Core -> Libs -> HLE, so Libs cannot call back into Core directly.
///
/// The stamp is process-wide on purpose: the watchdog's remedy is
/// <see cref="Environment.Exit"/>, which is a process-level decision, so
/// liveness is a process-level question. A host running more than one backend
/// would share the stamp, which can only make the watchdog more conservative.
/// </remarks>
public static class EmulationActivity
{
    private static long _lastProgressTimestamp;

    /// <summary>
    /// Records that emulation is still making forward progress. Cheap enough to
    /// call once per item in a bulk loop.
    /// </summary>
    public static void MarkProgress()
    {
        Volatile.Write(ref _lastProgressTimestamp, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Ticks since the last <see cref="MarkProgress"/>. Zero if nothing has ever
    /// reported progress in this process.
    /// </summary>
    public static long TicksSinceProgress() =>
        Stopwatch.GetTimestamp() - Volatile.Read(ref _lastProgressTimestamp);
}
