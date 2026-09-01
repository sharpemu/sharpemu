// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Core.Cpu.Native;

public sealed unsafe partial class DirectExecutionBackend
{
    private sealed class GuestSchedulerThreadStats
    {
        public string Name = string.Empty;
        public long ReadyTimestamp;
        public long BlockTimestamp;
        public long ScheduledTimestamp;
        public long RunTimestamp;
        public long ReadyCount;
        public long ClaimCount;
        public long ScheduleCount;
        public long RunCount;
        public long BlockCount;
        public long ResumeCount;
        public long ReadyTicks;
        public long BlockTicks;
        public long ScheduleTicks;
        public long RunTicks;
        public long MaxReadyTicks;
        public long MaxBlockTicks;
        public long MaxScheduleTicks;
        public long MaxRunTicks;
        public string? LastBlockReason;
        public int Completed;
    }

    private static readonly bool _profileGuestScheduler =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GUEST_SCHEDULER"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    private readonly ConcurrentDictionary<ulong, GuestSchedulerThreadStats> _guestSchedulerStats = new();
    private long _guestSchedulerProfileWindowStart = Stopwatch.GetTimestamp();
    private long _guestSchedulerProfileNextReport =
        Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 5L);
    private int _guestSchedulerProfileReporting;

    private GuestSchedulerThreadStats GetGuestSchedulerStats(GuestThreadState thread)
    {
        var stats = _guestSchedulerStats.GetOrAdd(
            thread.ThreadHandle,
            static _ => new GuestSchedulerThreadStats());
        stats.Name = thread.Name;
        return stats;
    }

    private void ProfileGuestThreadReady(GuestThreadState thread)
    {
        if (!_profileGuestScheduler)
        {
            return;
        }

        var stats = GetGuestSchedulerStats(thread);
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref stats.ReadyTimestamp, now);
        Interlocked.Increment(ref stats.ReadyCount);
        var blockTimestamp = Interlocked.Exchange(ref stats.BlockTimestamp, 0);
        if (blockTimestamp != 0 && now >= blockTimestamp)
        {
            var ticks = now - blockTimestamp;
            Interlocked.Increment(ref stats.ResumeCount);
            Interlocked.Add(ref stats.BlockTicks, ticks);
            UpdateGuestSchedulerMaximum(ref stats.MaxBlockTicks, ticks);
        }
    }

    private void ProfileGuestThreadClaimed(GuestThreadState thread)
    {
        if (!_profileGuestScheduler)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var stats = GetGuestSchedulerStats(thread);
        var readyTimestamp = Interlocked.Exchange(ref stats.ReadyTimestamp, 0);
        Interlocked.Increment(ref stats.ClaimCount);
        if (readyTimestamp != 0 && now >= readyTimestamp)
        {
            var ticks = now - readyTimestamp;
            Interlocked.Add(ref stats.ReadyTicks, ticks);
            UpdateGuestSchedulerMaximum(ref stats.MaxReadyTicks, ticks);
        }
    }

    private void ProfileGuestThreadScheduled(GuestThreadState thread)
    {
        if (!_profileGuestScheduler)
        {
            return;
        }

        var stats = GetGuestSchedulerStats(thread);
        Volatile.Write(ref stats.ScheduledTimestamp, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref stats.ScheduleCount);
    }

    private void ProfileGuestThreadRunStarted(GuestThreadState thread)
    {
        if (!_profileGuestScheduler)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var stats = GetGuestSchedulerStats(thread);
        var scheduledTimestamp = Interlocked.Exchange(ref stats.ScheduledTimestamp, 0);
        Volatile.Write(ref stats.RunTimestamp, now);
        Interlocked.Increment(ref stats.RunCount);
        if (scheduledTimestamp != 0 && now >= scheduledTimestamp)
        {
            var ticks = now - scheduledTimestamp;
            Interlocked.Add(ref stats.ScheduleTicks, ticks);
            UpdateGuestSchedulerMaximum(ref stats.MaxScheduleTicks, ticks);
        }
    }

    private void ProfileGuestThreadRunStopped(GuestThreadState thread)
    {
        if (!_profileGuestScheduler)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var stats = GetGuestSchedulerStats(thread);
        var runTimestamp = Interlocked.Exchange(ref stats.RunTimestamp, 0);
        if (runTimestamp != 0 && now >= runTimestamp)
        {
            var ticks = now - runTimestamp;
            Interlocked.Add(ref stats.RunTicks, ticks);
            UpdateGuestSchedulerMaximum(ref stats.MaxRunTicks, ticks);
        }

        if (thread.State == GuestThreadRunState.Blocked)
        {
            Interlocked.Increment(ref stats.BlockCount);
            Volatile.Write(ref stats.BlockTimestamp, now);
            Volatile.Write(ref stats.LastBlockReason, thread.BlockReason);
        }
        else if (thread.State is GuestThreadRunState.Exited or GuestThreadRunState.Faulted)
        {
            Volatile.Write(ref stats.Completed, 1);
        }

        TryReportGuestSchedulerProfile(now);
    }

    private void TryReportGuestSchedulerProfile(long now)
    {
        var deadline = Volatile.Read(ref _guestSchedulerProfileNextReport);
        if (now < deadline ||
            Interlocked.CompareExchange(ref _guestSchedulerProfileReporting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Volatile.Write(
                ref _guestSchedulerProfileNextReport,
                now + (Stopwatch.Frequency * 5L));
            var windowStart = Interlocked.Exchange(ref _guestSchedulerProfileWindowStart, now);
            var seconds = Math.Max(
                (now - windowStart) / (double)Stopwatch.Frequency,
                double.Epsilon);

            var rows = new List<(long Rank, string Text)>();
            foreach (var pair in _guestSchedulerStats)
            {
                var stats = pair.Value;
                var readyCount = Interlocked.Exchange(ref stats.ReadyCount, 0);
                var claimCount = Interlocked.Exchange(ref stats.ClaimCount, 0);
                var scheduleCount = Interlocked.Exchange(ref stats.ScheduleCount, 0);
                var runCount = Interlocked.Exchange(ref stats.RunCount, 0);
                var blockCount = Interlocked.Exchange(ref stats.BlockCount, 0);
                var resumeCount = Interlocked.Exchange(ref stats.ResumeCount, 0);
                var readyTicks = Interlocked.Exchange(ref stats.ReadyTicks, 0);
                var blockTicks = Interlocked.Exchange(ref stats.BlockTicks, 0);
                var scheduleTicks = Interlocked.Exchange(ref stats.ScheduleTicks, 0);
                var runTicks = Interlocked.Exchange(ref stats.RunTicks, 0);
                var maxReadyTicks = Interlocked.Exchange(ref stats.MaxReadyTicks, 0);
                var maxBlockTicks = Interlocked.Exchange(ref stats.MaxBlockTicks, 0);
                var maxScheduleTicks = Interlocked.Exchange(ref stats.MaxScheduleTicks, 0);
                var maxRunTicks = Interlocked.Exchange(ref stats.MaxRunTicks, 0);
                if (readyCount == 0 && claimCount == 0 && scheduleCount == 0 && runCount == 0)
                {
                    continue;
                }

                rows.Add((Math.Max(blockTicks, Math.Max(readyTicks, runTicks)),
                    $"name='{stats.Name}' handle=0x{pair.Key:X16} " +
                    $"ready={readyCount} claimed={claimCount} scheduled={scheduleCount} " +
                    $"runs={runCount} blocked={blockCount} resumed={resumeCount} " +
                    $"blocked_ms={GuestSchedulerMilliseconds(blockTicks):F2} " +
                    $"blocked_avg_ms={(resumeCount == 0 ? 0.0 : GuestSchedulerMilliseconds(blockTicks) / resumeCount):F3} " +
                    $"blocked_max_ms={GuestSchedulerMilliseconds(maxBlockTicks):F3} " +
                    $"ready_ms={GuestSchedulerMilliseconds(readyTicks):F2} " +
                    $"ready_avg_ms={(claimCount == 0 ? 0.0 : GuestSchedulerMilliseconds(readyTicks) / claimCount):F3} " +
                    $"ready_max_ms={GuestSchedulerMilliseconds(maxReadyTicks):F3} " +
                    $"dispatch_ms={GuestSchedulerMilliseconds(scheduleTicks):F2} " +
                    $"dispatch_avg_ms={(runCount == 0 ? 0.0 : GuestSchedulerMilliseconds(scheduleTicks) / runCount):F3} " +
                    $"dispatch_max_ms={GuestSchedulerMilliseconds(maxScheduleTicks):F3} " +
                    $"run_ms={GuestSchedulerMilliseconds(runTicks):F2} " +
                    $"run_avg_ms={(runCount == 0 ? 0.0 : GuestSchedulerMilliseconds(runTicks) / runCount):F3} " +
                    $"run_max_ms={GuestSchedulerMilliseconds(maxRunTicks):F3} " +
                    $"last_block={Volatile.Read(ref stats.LastBlockReason) ?? "none"}"));

                if (Volatile.Read(ref stats.Completed) != 0)
                {
                    _guestSchedulerStats.TryRemove(pair.Key, out _);
                }
            }

            Console.Error.WriteLine(
                $"[PERF][GUEST_SCHED] window={seconds:F1}s active_threads={rows.Count}");
            foreach (var row in rows
                .OrderByDescending(static item => item.Rank)
                .Take(32))
            {
                Console.Error.WriteLine($"[PERF][GUEST_SCHED] {row.Text}");
            }
        }
        finally
        {
            Volatile.Write(ref _guestSchedulerProfileReporting, 0);
        }
    }

    private static double GuestSchedulerMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private static void UpdateGuestSchedulerMaximum(ref long target, long candidate)
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
