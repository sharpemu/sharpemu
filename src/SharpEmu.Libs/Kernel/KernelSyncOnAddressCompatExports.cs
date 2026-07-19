// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// libKernel's address-wait primitives (sceKernelSyncOnAddress*) are the PS5's
// futex-style wait/wake: a thread parks on a guest address until another thread
// wakes that address. Guest runtimes (seen driving Juicy Realm, PPSA19268)
// build their own spinlocks/queues on top of it and call the wait in a hot
// loop; left unimplemented, every wait returns immediately and the runtime
// busy-spins forever (millions of calls, no forward progress).
//
// Waits block in place on a per-address gate (see GuestThreadBlocking):
// Monitor.Wait releases the gate and parks atomically, so a wake's generation
// bump + PulseAll cannot be lost between the generation check and the park.
// The real primitive takes a compare value so the wait only sleeps while the
// address still holds the expected value; that exact value is not recovered
// here, so each wait is bounded by a self-heal deadline and treated as a
// spurious-wakeup-tolerant park: the guest re-checks its own condition after
// resuming, which futex callers already tolerate. Wake-one degrades to
// wake-all for the same reason (each resumed waiter re-evaluates).
public static class KernelSyncOnAddressCompatExports
{
    // Safety-net bound. Real releases come from the wake side; this only limits
    // how long a wait that genuinely raced/missed its wake stays parked before
    // the guest re-evaluates. Kept large: a short bound turns every parked
    // waiter into a hot re-poll that steals CPU from the threads that would
    // issue the wake, so it must be a rare last resort, not a spin substitute.
    private static readonly TimeSpan WaitSelfHealTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly ConcurrentDictionary<ulong, object> _addressGates = new();

    // Per-address wake generation. A wait captures the current generation and
    // stays parked while it is unchanged; a wake bumps it first, then pulses
    // the gate, so a wait between its generation check and its park still
    // observes the bump (the check happens under the gate).
    private static readonly ConcurrentDictionary<ulong, long> _wakeGenerations = new();

    private static long CurrentGeneration(ulong address) =>
        _wakeGenerations.TryGetValue(address, out var generation) ? generation : 0;

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var observedGeneration = CurrentGeneration(address);
        var gate = _addressGates.GetOrAdd(address, static _ => new object());
        var deadlineMs = Environment.TickCount64 + (long)WaitSelfHealTimeout.TotalMilliseconds;
        var guestThreadHandle = GuestThreadExecution.CurrentGuestThreadHandle;
        GuestThreadBlocking.NoteBlocked(guestThreadHandle, "sceKernelSyncOnAddressWait");
        try
        {
            lock (gate)
            {
                while (CurrentGeneration(address) == observedGeneration &&
                       !GuestThreadBlocking.ShutdownRequested)
                {
                    var remaining = deadlineMs - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        // Self-heal: resume and let the guest re-check its condition.
                        break;
                    }

                    GuestThreadBlocking.Checkpoint(guestThreadHandle, gate);
                    _ = Monitor.Wait(gate, (int)Math.Min(remaining, GuestThreadBlocking.WaitSliceMilliseconds));
                }
            }
        }
        finally
        {
            GuestThreadBlocking.NoteUnblocked(guestThreadHandle);
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Bump the generation first so a wait that has checked but not yet
        // parked (it holds the gate for both) observes the change; then pulse
        // parked waiters. rsi's wake count degrades to wake-all — resumed
        // waiters re-evaluate their own condition, which futex callers tolerate.
        _wakeGenerations.AddOrUpdate(address, 1, static (_, current) => current + 1);

        if (_addressGates.TryGetValue(address, out var gate))
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
        }

        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }
}
