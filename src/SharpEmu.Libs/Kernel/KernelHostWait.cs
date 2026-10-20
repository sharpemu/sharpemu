// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

internal static class KernelHostWait
{
    // Monitor.Wait truncates a TimeSpan to whole milliseconds, so a sub-millisecond
    // remainder becomes a zero timeout and the wait spins until the deadline (UE's
    // game thread waits for the render fence in 1 ms slices, which kept a core busy).
    // A timed wait may wake late, so round up instead.
    internal static int RoundUpMilliseconds(TimeSpan duration) =>
        duration <= TimeSpan.Zero
            ? 0
            : (int)Math.Min(int.MaxValue, Math.Ceiling(duration.TotalMilliseconds));
}
