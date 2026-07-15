// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Raw host thread and native-TLS primitives for the execution engine. Guest
/// code must run on threads the CLR did not create (no managed frames below
/// guest frames), so thread creation takes a native entry point and is not
/// expressible with managed threads.
/// </summary>
public interface IHostThreading
{
    /// <summary>Allocates a native TLS slot; returns <see cref="uint.MaxValue"/> on failure.</summary>
    uint AllocateTlsSlot();

    bool FreeTlsSlot(uint slot);

    bool SetTlsValue(uint slot, nint value);

    nint GetTlsValue(uint slot);

    uint CurrentThreadId { get; }

    bool TrySetCurrentThreadAffinity(nuint affinityMask);

    /// <summary>
    /// Asks the OS for ~1 ms timed-wait granularity for the life of the process
    /// (idempotent; best-effort). No-op on platforms whose default is already fine.
    /// </summary>
    void RequestTimerResolution();

    /// <summary>
    /// Creates a raw OS thread executing native code at <paramref name="entry"/> with
    /// <paramref name="stackReserveBytes"/> of reserved (not committed) stack.
    /// Returns the thread handle, or 0 on failure.
    /// </summary>
    nint CreateNativeThread(nint entry, nint parameter, nuint stackReserveBytes, out uint threadId);

    /// <summary>Waits for the thread to exit; true when it did within the timeout.</summary>
    bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds);

    void CloseThreadHandle(nint threadHandle);

    /// <summary>
    /// Suspends the thread, snapshots its general-purpose registers, and resumes it —
    /// one indivisible operation (diagnostics only). The caller must not pass the
    /// current thread. Returns false if the thread cannot be opened or suspended.
    /// </summary>
    bool TryCaptureThreadRegisters(uint threadId, out HostCapturedRegisters registers);
}
