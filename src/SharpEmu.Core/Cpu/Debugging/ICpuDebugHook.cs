// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Debugging;

/// <summary>
/// The seam the CPU dispatcher uses to notify an attached debugger when guest
/// execution crosses a frame boundary. Implemented outside of Core (for
/// example by <c>SharpEmu.Debugger</c>) and supplied through
/// <see cref="CpuExecutionOptions.DebugHook"/>.
/// </summary>
/// <remarks>
/// This is intentionally coarse-grained: it exposes the entry and exit of each
/// dispatched frame rather than per-instruction stepping. Per-instruction
/// control requires cooperation from the native execution backend and is layered
/// on top of this seam as the backend gains support; keeping the dispatcher-level
/// contract stable lets the debugger infrastructure exist independently of that
/// work. Implementations must be thread-safe: frames may be dispatched from the
/// dedicated emulation thread while a debug server services clients on its own
/// threads.
/// </remarks>
public interface ICpuDebugHook
{
    /// <summary>
    /// Invoked immediately before the native backend begins executing a frame.
    /// The debugger may inspect or mutate <paramref name="frame"/> and may block
    /// the calling thread (for example, to honour a pause request) before
    /// returning to allow execution to proceed.
    /// </summary>
    void OnFrameEnter(ICpuDebugFrame frame);

    /// <summary>
    /// Invoked after a frame completes, whether it returned to the host or
    /// terminated with an error. <paramref name="frame"/> reflects the final
    /// guest state.
    /// </summary>
    void OnFrameExit(ICpuDebugFrame frame, OrbisGen2Result result);

    /// <summary>
    /// Invoked from the emulation thread when the backend detects an execution
    /// stall (for example a mutex spin loop) in the running frame, before it
    /// forces the guest out of the loop. As with <see cref="OnFrameEnter"/>, the
    /// implementation may inspect <paramref name="frame"/> and block to honour a
    /// break before returning to let the backend proceed.
    /// </summary>
    void OnStall(ICpuDebugFrame frame, CpuStallInfo info);
}
