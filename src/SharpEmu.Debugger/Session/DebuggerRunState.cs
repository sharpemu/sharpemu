// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Session;

/// <summary>The execution state of a debugged target as seen by the debugger.</summary>
public enum DebuggerRunState
{
    /// <summary>No guest frame has entered the debugger yet.</summary>
    Detached,

    /// <summary>The guest is executing and cannot be inspected safely.</summary>
    Running,

    /// <summary>
    /// The guest is parked at a frame boundary. Registers and memory can be
    /// read and written, and breakpoints can be edited.
    /// </summary>
    Paused,

    /// <summary>The guest has finished; no further frames will run.</summary>
    Terminated,
}
