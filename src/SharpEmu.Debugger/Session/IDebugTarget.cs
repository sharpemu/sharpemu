// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Debugger.Session;

/// <summary>
/// The inspection and control surface a debugger front-end (for example a
/// network server) drives. Register and memory accessors succeed only while the
/// target is <see cref="DebuggerRunState.Paused"/>; they return <c>false</c>
/// otherwise so callers never read torn state from a running guest.
/// </summary>
public interface IDebugTarget
{
    /// <summary>The current execution state.</summary>
    DebuggerRunState State { get; }

    /// <summary>The most recent stop, or null if the target has not stopped yet.</summary>
    DebugStopEvent? LastStop { get; }

    /// <summary>Reads the integer register file. Fails unless paused.</summary>
    bool TryGetRegisters(out DebugRegisterFile registers);

    /// <summary>Writes a single register. Fails unless paused.</summary>
    bool TrySetRegister(DebugRegisterId id, ulong value);

    /// <summary>Reads guest memory into <paramref name="destination"/>. Fails unless paused.</summary>
    bool TryReadMemory(ulong address, Span<byte> destination);

    /// <summary>Writes guest memory from <paramref name="source"/>. Fails unless paused.</summary>
    bool TryWriteMemory(ulong address, ReadOnlySpan<byte> source);

    /// <summary>Reads a 128-bit XMM register. Fails unless paused.</summary>
    bool TryReadXmm(int registerIndex, out ulong low, out ulong high);

    /// <summary>
    /// Resumes a paused target. Returns false when the target was not paused.
    /// </summary>
    bool Continue();

    /// <summary>
    /// Resumes a paused target and stops again at the next frame boundary.
    /// Returns false when the target was not paused.
    /// </summary>
    bool StepFrame();

    /// <summary>
    /// Requests that a running target stop at the next frame boundary. Has no
    /// effect if the target is already paused or terminated.
    /// </summary>
    void RequestPause();
}
