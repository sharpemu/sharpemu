// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Kernel;

internal static class KernelSyncOnAddressProfile
{
    private sealed class AddressStats
    {
        public long Immediate;
        public long Blocked;
        public long WakeCalls;
        public long WakeRequested;
        public long WakeSelected;
        public long ResumedExplicit;
        public long ResumedChanged;
        public long ResumedTimedOut;
        public long ResumedFaulted;
        public long WaitTicks;
        public long MaxWaitTicks;
        public long LastWaitReturnRip;
        public long LastWaitCallerRip;
        public long LastWaitParentRip;
        public long LastWaitGuestThread;
        public int LastWaitManagedThread;
        public long LastWakeReturnRip;
        public long LastWakeCallerRip;
        public long LastWakeParentRip;
        public long LastWakeAncestorRip;
        public long LastWakeOuterRip1;
        public long LastWakeOuterRip2;
        public long LastWakeOuterRip3;
        public long LastWakeGuestThread;
        public int LastWakeManagedThread;
    }

    private sealed class WaiterStats
    {
        public long Blocked;
        public long ResumedExplicit;
        public long ResumedChanged;
        public long ResumedTimedOut;
        public long ResumedFaulted;
        public long WaitTicks;
        public long MaxWaitTicks;
        public long LastWaitReturnRip;
        public long LastWaitCallerRip;
        public long LastWaitParentRip;
        public long LastWaitGuestThread;
    }

    private readonly record struct WaiterKey(ulong Address, int ManagedThread);

    private readonly record struct WaiterSnapshot(
        ulong Address,
        int ManagedThread,
        long Blocked,
        long ResumedExplicit,
        long ResumedChanged,
        long ResumedTimedOut,
        long ResumedFaulted,
        long WaitTicks,
        long MaxWaitTicks,
        ulong LastWaitReturnRip,
        ulong LastWaitCallerRip,
        ulong LastWaitParentRip,
        ulong LastWaitGuestThread);

    private readonly record struct AddressSnapshot(
        ulong Address,
        long Immediate,
        long Blocked,
        long WakeCalls,
        long WakeRequested,
        long WakeSelected,
        long ResumedExplicit,
        long ResumedChanged,
        long ResumedTimedOut,
        long ResumedFaulted,
        long WaitTicks,
        long MaxWaitTicks,
        ulong LastWaitReturnRip,
        ulong LastWaitCallerRip,
        ulong LastWaitParentRip,
        ulong LastWaitGuestThread,
        int LastWaitManagedThread,
        ulong LastWakeReturnRip,
        ulong LastWakeCallerRip,
        ulong LastWakeParentRip,
        ulong LastWakeAncestorRip,
        ulong LastWakeOuterRip1,
        ulong LastWakeOuterRip2,
        ulong LastWakeOuterRip3,
        ulong LastWakeGuestThread,
        int LastWakeManagedThread);

    private static readonly bool _detailedEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_SYNC_ON_ADDRESS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool _enabled =
        _detailedEnabled ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    private static ConcurrentDictionary<ulong, AddressStats> _stats = new();
    private static ConcurrentDictionary<WaiterKey, WaiterStats> _waiterStats = new();
    private static long _windowStartTicks = Stopwatch.GetTimestamp();
    private static int _reporting;

    public static bool Enabled => _enabled;

    // The unified profile records wait and wake totals. Walking guest frame
    // chains is intentionally reserved for the focused profile because every
    // frame read touches guest memory on a synchronization hot path.
    public static bool DetailedEnabled => _detailedEnabled;

    public static void RecordImmediate(ulong address)
    {
        if (!_enabled)
        {
            return;
        }

        Interlocked.Increment(ref GetStats(address).Immediate);
        TryReport();
    }

    public static void RecordBlocked(
        ulong address,
        ulong returnRip,
        ulong callerRip,
        ulong parentRip,
        ulong guestThread,
        int managedThread)
    {
        if (!_enabled)
        {
            return;
        }

        var stats = GetStats(address);
        Interlocked.Increment(ref stats.Blocked);
        Volatile.Write(ref stats.LastWaitReturnRip, unchecked((long)returnRip));
        Volatile.Write(ref stats.LastWaitCallerRip, unchecked((long)callerRip));
        Volatile.Write(ref stats.LastWaitParentRip, unchecked((long)parentRip));
        Volatile.Write(ref stats.LastWaitGuestThread, unchecked((long)guestThread));
        Volatile.Write(ref stats.LastWaitManagedThread, managedThread);

        var waiterStats = GetWaiterStats(address, managedThread);
        Interlocked.Increment(ref waiterStats.Blocked);
        Volatile.Write(ref waiterStats.LastWaitReturnRip, unchecked((long)returnRip));
        Volatile.Write(ref waiterStats.LastWaitCallerRip, unchecked((long)callerRip));
        Volatile.Write(ref waiterStats.LastWaitParentRip, unchecked((long)parentRip));
        Volatile.Write(ref waiterStats.LastWaitGuestThread, unchecked((long)guestThread));
        TryReport();
    }

    public static void RecordWake(
        ulong address,
        int requested,
        int selected,
        ulong returnRip,
        ulong callerRip,
        ulong parentRip,
        ulong ancestorRip,
        ulong outerRip1,
        ulong outerRip2,
        ulong outerRip3,
        ulong guestThread,
        int managedThread)
    {
        if (!_enabled)
        {
            return;
        }

        var stats = GetStats(address);
        Interlocked.Increment(ref stats.WakeCalls);
        Interlocked.Add(ref stats.WakeRequested, requested);
        Interlocked.Add(ref stats.WakeSelected, selected);
        Volatile.Write(ref stats.LastWakeReturnRip, unchecked((long)returnRip));
        Volatile.Write(ref stats.LastWakeCallerRip, unchecked((long)callerRip));
        Volatile.Write(ref stats.LastWakeParentRip, unchecked((long)parentRip));
        Volatile.Write(ref stats.LastWakeAncestorRip, unchecked((long)ancestorRip));
        Volatile.Write(ref stats.LastWakeOuterRip1, unchecked((long)outerRip1));
        Volatile.Write(ref stats.LastWakeOuterRip2, unchecked((long)outerRip2));
        Volatile.Write(ref stats.LastWakeOuterRip3, unchecked((long)outerRip3));
        Volatile.Write(ref stats.LastWakeGuestThread, unchecked((long)guestThread));
        Volatile.Write(ref stats.LastWakeManagedThread, managedThread);
        TryReport();
    }

    public static void RecordResume(
        ulong address,
        int managedThread,
        long waitTicks,
        bool explicitWake,
        bool valueChanged,
        bool timedOut,
        bool faulted)
    {
        if (!_enabled)
        {
            return;
        }

        var stats = GetStats(address);
        if (explicitWake)
        {
            Interlocked.Increment(ref stats.ResumedExplicit);
        }
        else if (valueChanged)
        {
            Interlocked.Increment(ref stats.ResumedChanged);
        }
        else if (timedOut)
        {
            Interlocked.Increment(ref stats.ResumedTimedOut);
        }
        else if (faulted)
        {
            Interlocked.Increment(ref stats.ResumedFaulted);
        }

        Interlocked.Add(ref stats.WaitTicks, waitTicks);
        UpdateMaximum(ref stats.MaxWaitTicks, waitTicks);

        var waiterStats = GetWaiterStats(address, managedThread);
        if (explicitWake)
        {
            Interlocked.Increment(ref waiterStats.ResumedExplicit);
        }
        else if (valueChanged)
        {
            Interlocked.Increment(ref waiterStats.ResumedChanged);
        }
        else if (timedOut)
        {
            Interlocked.Increment(ref waiterStats.ResumedTimedOut);
        }
        else if (faulted)
        {
            Interlocked.Increment(ref waiterStats.ResumedFaulted);
        }

        Interlocked.Add(ref waiterStats.WaitTicks, waitTicks);
        UpdateMaximum(ref waiterStats.MaxWaitTicks, waitTicks);
        TryReport();
    }

    private static AddressStats GetStats(ulong address) =>
        Volatile.Read(ref _stats).GetOrAdd(address, static _ => new AddressStats());

    private static WaiterStats GetWaiterStats(ulong address, int managedThread) =>
        Volatile.Read(ref _waiterStats).GetOrAdd(
            new WaiterKey(address, managedThread),
            static _ => new WaiterStats());

    private static void TryReport()
    {
        var now = Stopwatch.GetTimestamp();
        var windowStart = Volatile.Read(ref _windowStartTicks);
        if (now - windowStart < Stopwatch.Frequency * 5L ||
            Interlocked.CompareExchange(ref _reporting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            now = Stopwatch.GetTimestamp();
            windowStart = Interlocked.Exchange(ref _windowStartTicks, now);
            var elapsedSeconds = Math.Max(
                (now - windowStart) / (double)Stopwatch.Frequency,
                double.Epsilon);
            var statsWindow = Interlocked.Exchange(
                ref _stats,
                new ConcurrentDictionary<ulong, AddressStats>());
            var waiterStatsWindow = Interlocked.Exchange(
                ref _waiterStats,
                new ConcurrentDictionary<WaiterKey, WaiterStats>());
            var snapshots = new List<AddressSnapshot>(statsWindow.Count);
            foreach (var pair in statsWindow)
            {
                var stats = pair.Value;
                var snapshot = new AddressSnapshot(
                    pair.Key,
                    Interlocked.Exchange(ref stats.Immediate, 0),
                    Interlocked.Exchange(ref stats.Blocked, 0),
                    Interlocked.Exchange(ref stats.WakeCalls, 0),
                    Interlocked.Exchange(ref stats.WakeRequested, 0),
                    Interlocked.Exchange(ref stats.WakeSelected, 0),
                    Interlocked.Exchange(ref stats.ResumedExplicit, 0),
                    Interlocked.Exchange(ref stats.ResumedChanged, 0),
                    Interlocked.Exchange(ref stats.ResumedTimedOut, 0),
                    Interlocked.Exchange(ref stats.ResumedFaulted, 0),
                    Interlocked.Exchange(ref stats.WaitTicks, 0),
                    Interlocked.Exchange(ref stats.MaxWaitTicks, 0),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitReturnRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitCallerRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitParentRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitGuestThread)),
                    Volatile.Read(ref stats.LastWaitManagedThread),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeReturnRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeCallerRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeParentRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeAncestorRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeOuterRip1)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeOuterRip2)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeOuterRip3)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWakeGuestThread)),
                    Volatile.Read(ref stats.LastWakeManagedThread));
                if (snapshot.Immediate != 0 ||
                    snapshot.Blocked != 0 ||
                    snapshot.WakeCalls != 0 ||
                    snapshot.WaitTicks != 0)
                {
                    snapshots.Add(snapshot);
                }
            }

            Console.Error.WriteLine(
                $"[PERF][SYNC_ADDR] window={elapsedSeconds:F1}s addresses={snapshots.Count} " +
                $"blocked={snapshots.Sum(static item => item.Blocked)} " +
                $"wake_calls={snapshots.Sum(static item => item.WakeCalls)} " +
                $"wait_ms={ToMilliseconds(snapshots.Sum(static item => item.WaitTicks)):F2}");
            foreach (var item in snapshots
                .OrderByDescending(static item => item.WaitTicks)
                .ThenByDescending(static item => item.Blocked + item.WakeCalls)
                .Take(8))
            {
                var resumed = item.ResumedExplicit + item.ResumedChanged +
                    item.ResumedTimedOut + item.ResumedFaulted;
                Console.Error.WriteLine(
                    $"[PERF][SYNC_ADDR] addr=0x{item.Address:X16} " +
                    $"immediate={item.Immediate} blocked={item.Blocked} " +
                    $"wake_calls={item.WakeCalls} requested={item.WakeRequested} " +
                    $"selected={item.WakeSelected} resumed={resumed} " +
                    $"explicit={item.ResumedExplicit} changed={item.ResumedChanged} " +
                    $"timeout={item.ResumedTimedOut} fault={item.ResumedFaulted} " +
                    $"wait_ms={ToMilliseconds(item.WaitTicks):F2} " +
                    $"avg_wait_ms={(resumed == 0 ? 0.0 : ToMilliseconds(item.WaitTicks) / resumed):F3} " +
                    $"max_wait_ms={ToMilliseconds(item.MaxWaitTicks):F3} " +
                    $"wait_ret=0x{item.LastWaitReturnRip:X16} " +
                    $"wait_caller=0x{item.LastWaitCallerRip:X16} " +
                    $"wait_parent=0x{item.LastWaitParentRip:X16} " +
                    $"wait_guest=0x{item.LastWaitGuestThread:X16} " +
                    $"wait_managed={item.LastWaitManagedThread} " +
                    $"wake_ret=0x{item.LastWakeReturnRip:X16} " +
                    $"wake_caller=0x{item.LastWakeCallerRip:X16} " +
                    $"wake_parent=0x{item.LastWakeParentRip:X16} " +
                    $"wake_ancestor=0x{item.LastWakeAncestorRip:X16} " +
                    $"wake_outer1=0x{item.LastWakeOuterRip1:X16} " +
                    $"wake_outer2=0x{item.LastWakeOuterRip2:X16} " +
                    $"wake_outer3=0x{item.LastWakeOuterRip3:X16} " +
                    $"wake_guest=0x{item.LastWakeGuestThread:X16} " +
                    $"wake_managed={item.LastWakeManagedThread}");
            }

            var waiterSnapshots = new List<WaiterSnapshot>(waiterStatsWindow.Count);
            foreach (var pair in waiterStatsWindow)
            {
                var stats = pair.Value;
                var snapshot = new WaiterSnapshot(
                    pair.Key.Address,
                    pair.Key.ManagedThread,
                    Interlocked.Exchange(ref stats.Blocked, 0),
                    Interlocked.Exchange(ref stats.ResumedExplicit, 0),
                    Interlocked.Exchange(ref stats.ResumedChanged, 0),
                    Interlocked.Exchange(ref stats.ResumedTimedOut, 0),
                    Interlocked.Exchange(ref stats.ResumedFaulted, 0),
                    Interlocked.Exchange(ref stats.WaitTicks, 0),
                    Interlocked.Exchange(ref stats.MaxWaitTicks, 0),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitReturnRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitCallerRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitParentRip)),
                    unchecked((ulong)Volatile.Read(ref stats.LastWaitGuestThread)));
                if (snapshot.Blocked != 0 || snapshot.WaitTicks != 0)
                {
                    waiterSnapshots.Add(snapshot);
                }
            }

            foreach (var item in waiterSnapshots
                .OrderByDescending(static item => item.WaitTicks)
                .ThenByDescending(static item => item.Blocked)
                .Take(12))
            {
                var resumed = item.ResumedExplicit + item.ResumedChanged +
                    item.ResumedTimedOut + item.ResumedFaulted;
                Console.Error.WriteLine(
                    $"[PERF][SYNC_THREAD] managed={item.ManagedThread} " +
                    $"guest=0x{item.LastWaitGuestThread:X16} addr=0x{item.Address:X16} " +
                    $"blocked={item.Blocked} resumed={resumed} " +
                    $"explicit={item.ResumedExplicit} changed={item.ResumedChanged} " +
                    $"timeout={item.ResumedTimedOut} fault={item.ResumedFaulted} " +
                    $"wait_ms={ToMilliseconds(item.WaitTicks):F2} " +
                    $"avg_wait_ms={(resumed == 0 ? 0.0 : ToMilliseconds(item.WaitTicks) / resumed):F3} " +
                    $"max_wait_ms={ToMilliseconds(item.MaxWaitTicks):F3} " +
                    $"wait_ret=0x{item.LastWaitReturnRip:X16} " +
                    $"wait_caller=0x{item.LastWaitCallerRip:X16} " +
                    $"wait_parent=0x{item.LastWaitParentRip:X16}");
            }
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
