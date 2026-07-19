// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Support for HLE synchronization primitives that block the guest thread's
/// host thread in place (inside the HLE call, on a host primitive) instead of
/// capturing a continuation and re-scheduling through the cooperative wake-key
/// machinery. In-place blocking makes block-and-wake atomic — the host
/// primitive owns the race — which removes the lost-wakeup window the
/// continuation path had between block registration and wake delivery.
/// </summary>
public static class GuestThreadBlocking
{
    /// <summary>
    /// Upper bound on a single host wait while a guest thread is parked. Waits
    /// are sliced so parked threads observe <see cref="ShutdownRequested"/>
    /// promptly at teardown; a wake via Monitor.Pulse still lands immediately.
    /// </summary>
    public const int WaitSliceMilliseconds = 50;

    private static volatile bool _shutdownRequested;

    // Guest thread handle -> what it is parked on. Populated only while a
    // thread is blocked (the slow path), read by the stall watchdog so
    // in-place-blocked threads are not reported as opaque "Running" threads.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _blockDescriptions = new();

    /// <summary>True once emulator teardown has begun; parked guest threads unwind.</summary>
    public static bool ShutdownRequested => _shutdownRequested;

    /// <summary>Called by the execution backend when guest execution is being torn down.</summary>
    public static void RequestShutdown() => _shutdownRequested = true;

    /// <summary>Records what the given guest thread is about to park on (diagnostics only).</summary>
    public static void NoteBlocked(ulong guestThreadHandle, string description)
    {
        if (guestThreadHandle != 0)
        {
            _blockDescriptions[guestThreadHandle] = description;
        }
    }

    /// <summary>Clears the parked-state note recorded by <see cref="NoteBlocked"/>.</summary>
    public static void NoteUnblocked(ulong guestThreadHandle)
    {
        if (guestThreadHandle != 0)
        {
            _blockDescriptions.TryRemove(guestThreadHandle, out _);
        }
    }

    /// <summary>What the thread is parked on, or null if it is not parked in place.</summary>
    public static string? DescribeBlock(ulong guestThreadHandle) =>
        _blockDescriptions.TryGetValue(guestThreadHandle, out var description) ? description : null;

    /// <summary>All currently parked threads (diagnostics; covers the primary thread too).</summary>
    public static KeyValuePair<ulong, string>[] SnapshotBlockDescriptions() => _blockDescriptions.ToArray();

    // Interrupt delivery for threads parked in place. A thread blocked inside
    // an HLE wait keeps its executor busy, so it never reaches the import-return
    // safe point where queued guest exceptions (IL2CPP stop-the-world suspend)
    // are delivered. When an exception is queued for such a thread, its handle
    // is flagged here; each sliced wait loop calls Checkpoint, which — on the
    // thread's OWN host thread, with the wait's gate released — runs the
    // registered deliverer (the same safe-point delivery used at import
    // boundaries), then the loop re-checks its predicate and re-parks. This is
    // the SA_RESTART-style "signal on top of a blocking wait" a real kernel
    // provides. Dormant unless an exception is actually pending (empty-check
    // fast path), so it adds no cost to normal blocking.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, byte> _interrupted = new();

    /// <summary>Set by the backend: delivers any exception queued for the current guest thread, in place.</summary>
    public static Action? DeliverInterruptForCurrentThread { get; set; }

    /// <summary>Flags a parked guest thread to deliver a queued exception at its next wait checkpoint.</summary>
    public static void RequestInterrupt(ulong guestThreadHandle)
    {
        if (guestThreadHandle != 0)
        {
            _interrupted[guestThreadHandle] = 0;
        }
    }

    /// <summary>
    /// Called from every sliced wait loop while it holds <paramref name="gate"/>. If an
    /// exception is pending for the current guest thread, releases the gate, delivers it on
    /// this host thread, then re-acquires the gate so the loop re-checks its predicate.
    /// </summary>
    public static void Checkpoint(ulong guestThreadHandle, object gate)
    {
        if (_interrupted.IsEmpty || guestThreadHandle == 0 || !_interrupted.TryRemove(guestThreadHandle, out _))
        {
            return;
        }

        var deliver = DeliverInterruptForCurrentThread;
        if (deliver is null)
        {
            return;
        }

        // Never run guest code (the handler) while holding an HLE gate — the
        // handler may re-enter this same primitive. Release across delivery.
        Monitor.Exit(gate);
        try
        {
            deliver();
        }
        finally
        {
            Monitor.Enter(gate);
        }
    }
}
