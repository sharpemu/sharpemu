// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Debugging;
using SharpEmu.Debugger.Breakpoints;
using SharpEmu.HLE;

namespace SharpEmu.Debugger.Session;

/// <summary>
/// Describes a stop delivered to debugger clients: why the target stopped,
/// where, and the register snapshot at that point.
/// </summary>
public sealed class DebugStopEvent
{
    public DebugStopEvent(
        DebugStopReason reason,
        DebugRegisterFile registers,
        CpuDebugFrameKind frameKind,
        string frameLabel,
        Breakpoint? breakpoint = null,
        OrbisGen2Result? result = null,
        string? detail = null,
        string? opcodeBytes = null,
        CpuStallInfo? stallInfo = null)
    {
        Reason = reason;
        Registers = registers;
        FrameKind = frameKind;
        FrameLabel = frameLabel ?? string.Empty;
        Breakpoint = breakpoint;
        Result = result;
        Detail = detail;
        OpcodeBytes = opcodeBytes;
        StallInfo = stallInfo;
    }

    public DebugStopReason Reason { get; }

    /// <summary>The instruction pointer where the target stopped.</summary>
    public ulong Address => Registers.Rip;

    public DebugRegisterFile Registers { get; }

    public CpuDebugFrameKind FrameKind { get; }

    public string FrameLabel { get; }

    /// <summary>The breakpoint responsible for the stop, when applicable.</summary>
    public Breakpoint? Breakpoint { get; }

    /// <summary>
    /// The frame result for a <see cref="DebugStopReason.Fault"/> stop; null for
    /// non-fault stops.
    /// </summary>
    public OrbisGen2Result? Result { get; }

    /// <summary>A human-readable summary of a fault, when applicable.</summary>
    public string? Detail { get; }

    /// <summary>
    /// A hex preview of the bytes at <see cref="Address"/> (the faulting
    /// instruction), when the stop is a fault and the bytes were readable.
    /// </summary>
    public string? OpcodeBytes { get; }

    /// <summary>Structured backend evidence for a stall stop.</summary>
    public CpuStallInfo? StallInfo { get; }
}
