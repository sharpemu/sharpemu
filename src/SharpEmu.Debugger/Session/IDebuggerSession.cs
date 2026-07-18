// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Debugger.Breakpoints;

namespace SharpEmu.Debugger.Session;

/// <summary>
/// The debugger's coordination point. It bridges the CPU dispatcher seam
/// (<see cref="Hook"/>) to the inspection surface (<see cref="IDebugTarget"/>),
/// owns breakpoint state, and raises lifecycle events that a server relays to
/// connected clients.
/// </summary>
public interface IDebuggerSession : IDebugTarget
{
    /// <summary>The breakpoints armed for this session.</summary>
    BreakpointStore Breakpoints { get; }

    /// <summary>
    /// The dispatcher-facing hook. Assign this to
    /// <c>SharpEmuRuntimeOptions.DebugHook</c> so guest frames are routed through
    /// the session.
    /// </summary>
    ICpuDebugHook Hook { get; }

    /// <summary>Raised on the emulation thread each time the target stops.</summary>
    event EventHandler<DebugStopEvent>? Stopped;

    /// <summary>Raised when a paused target resumes.</summary>
    event EventHandler? Resumed;

    /// <summary>Raised once the target has terminated.</summary>
    event EventHandler? Terminated;

    /// <summary>
    /// Signals that the guest run has finished. Releases any parked emulation
    /// thread and transitions the session to
    /// <see cref="DebuggerRunState.Terminated"/>. Hosts call this after the
    /// runtime returns.
    /// </summary>
    void NotifyTerminated();
}
