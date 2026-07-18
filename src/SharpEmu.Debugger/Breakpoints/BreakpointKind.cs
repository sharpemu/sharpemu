// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Breakpoints;

/// <summary>
/// The kind of stop a breakpoint requests. Execution breakpoints are honoured
/// at the frame-boundary seam that exists today; the data-watch kinds are part
/// of the surface so client protocols and tooling can be built against them,
/// and are armed once the execution backend can report the corresponding
/// accesses.
/// </summary>
public enum BreakpointKind
{
    /// <summary>Stop when the instruction pointer reaches the address.</summary>
    Execute,

    /// <summary>Stop when the guest reads from the address range.</summary>
    ReadWatch,

    /// <summary>Stop when the guest writes to the address range.</summary>
    WriteWatch,

    /// <summary>Stop when the guest reads from or writes to the address range.</summary>
    AccessWatch,
}
