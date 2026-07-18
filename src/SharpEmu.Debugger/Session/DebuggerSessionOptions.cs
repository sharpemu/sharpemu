// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Session;

/// <summary>Configuration for a <see cref="DebuggerSession"/>.</summary>
public sealed class DebuggerSessionOptions
{
    /// <summary>
    /// When true, the session pauses at the first frame it observes so a client
    /// can attach breakpoints before the guest runs. Defaults to true, matching
    /// the "stop at entry" behaviour most debuggers expose.
    /// </summary>
    public bool StopAtEntry { get; init; } = true;

    /// <summary>
    /// When true, the session pauses when a frame ends with a non-OK result (a
    /// CPU trap, memory fault, or unimplemented path) so a client can inspect the
    /// post-fault register/memory state before the frame is torn down. Defaults
    /// to true. The stop reports <see cref="DebugStopReason.Fault"/>.
    /// </summary>
    public bool BreakOnFault { get; init; } = true;

    /// <summary>
    /// When true, the session pauses when the backend detects an execution stall
    /// (a mutex spin loop / livelock) before the guest is forced out of the loop,
    /// so a client can inspect the stalled state. Defaults to true. The stop
    /// reports <see cref="DebugStopReason.Stall"/>.
    /// </summary>
    public bool BreakOnStall { get; init; } = true;
}
