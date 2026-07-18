// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Session;

/// <summary>Why the target stopped and handed control to the debugger.</summary>
public enum DebugStopReason
{
    /// <summary>Stopped at the configured entry point before running any frame.</summary>
    EntryPoint,

    /// <summary>An execution breakpoint was hit.</summary>
    Breakpoint,

    /// <summary>A data watchpoint was hit.</summary>
    Watchpoint,

    /// <summary>A single-step (frame step) request completed.</summary>
    Step,

    /// <summary>A client-requested pause took effect.</summary>
    Pause,

    /// <summary>The guest raised a fault or trap.</summary>
    Fault,

    /// <summary>
    /// The backend detected an execution stall (for example a mutex spin loop /
    /// livelock) with no forward progress.
    /// </summary>
    Stall,
}
