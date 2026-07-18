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
// This implements wait/wake over the existing cooperative-block scheduler,
// keyed on the address. The real primitive takes a compare value so the wait
// only sleeps while the address still holds the expected value; that exact
// value is not recovered here, so each wait is given a bounded deadline and
// treated as a spurious-wakeup-tolerant park: a genuinely missed wake
// self-heals when the deadline expires and the guest re-checks its own
// condition, which futex callers already tolerate. A matching wake releases
// waiters immediately through the same key.
public static class KernelSyncOnAddressCompatExports
{
    // Safety-net poll interval. Real releases come from the wake side (generation
    // bump + WakeBlockedThreads); this only bounds how long a wait that genuinely
    // raced/missed its wake stays parked before the guest re-evaluates. Kept
    // large: a short interval turns every parked waiter into a hot re-poll that
    // steals scheduler bandwidth from the threads that actually make progress
    // (including the ones that would issue the wake), so it must be a rare last
    // resort, not a spin substitute.
    private static readonly TimeSpan WaitSelfHealTimeout = TimeSpan.FromMilliseconds(100);

    // Per-address host gate for the non-cooperative (host main thread) fallback,
    // which cannot use the guest-thread scheduler's block mechanism.
    private static readonly ConcurrentDictionary<ulong, object> _hostAddressGates = new();

    // Per-address wake generation. A wait captures the current generation and
    // its wake predicate stays unsatisfied (keeps the thread parked) until a
    // wake bumps it. This is what actually holds the thread blocked: a bare
    // "always satisfied" predicate is treated as an immediate late-arrival by
    // the dispatcher's race guard and never yields, leaving the guest to
    // busy-spin. The generation also closes the register-vs-park race for free:
    // a wake landing in that window bumps the generation, so the predicate is
    // already satisfied and the guest correctly resumes at once.
    private static readonly ConcurrentDictionary<ulong, long> _wakeGenerations = new();

    private static long CurrentGeneration(ulong address) =>
        _wakeGenerations.TryGetValue(address, out var generation) ? generation : 0;

    private static string WakeKey(ulong address) => $"sceKernelSyncOnAddress:{address:X16}";

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
        var deadline = GuestThreadExecution.ComputeDeadlineTimestamp(WaitSelfHealTimeout);

        // Cooperative path: stay parked until a wake bumps this address's
        // generation (or the deadline expires as a self-heal). The guest
        // re-evaluates its own condition after resuming.
        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelSyncOnAddressWait",
                WakeKey(address),
                resumeHandler: () => (int)OrbisGen2Result.ORBIS_GEN2_OK,
                wakeHandler: () => CurrentGeneration(address) != observedGeneration,
                deadline))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // Non-cooperative caller (host main thread): bounded host wait so a
        // missed wake self-heals instead of hanging.
        var gate = _hostAddressGates.GetOrAdd(address, static _ => new object());
        lock (gate)
        {
            if (CurrentGeneration(address) == observedGeneration)
            {
                Monitor.Wait(gate, WaitSelfHealTimeout);
            }
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

        // rsi carries the number of waiters to release (1 = wake-one, a large
        // value = wake-all); default to all if it looks unset.
        var requested = unchecked((long)ctx[CpuRegister.Rsi]);
        var wakeCount = requested is > 0 and < int.MaxValue ? (int)requested : int.MaxValue;

        // Bump the generation first so a wait that has registered but not yet
        // parked sees the change and resumes instead of missing this wake.
        _wakeGenerations.AddOrUpdate(address, 1, static (_, current) => current + 1);

        GuestThreadExecution.Scheduler?.WakeBlockedThreads(WakeKey(address), wakeCount);

        if (_hostAddressGates.TryGetValue(address, out var gate))
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
