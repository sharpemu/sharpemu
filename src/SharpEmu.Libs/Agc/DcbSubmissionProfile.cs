// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Agc;

internal static class DcbSubmissionProfile
{
    private static readonly bool _enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_DCB_SUBMISSION"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    private static long _windowStartTicks = Stopwatch.GetTimestamp();
    private static long _calls;
    private static long _dwords;
    private static long _setupTicks;
    private static long _snapshotTicks;
    private static long _lockWaitTicks;
    private static long _queuePumpTicks;
    private static long _resumeDrainTicks;
    private static long _totalTicks;
    private static long _maxTotalTicks;
    private static long _maxQueuePumpTicks;
    private static int _reporting;

    public static bool Enabled => _enabled;

    public static void Record(
        uint dwordCount,
        long setupTicks,
        long snapshotTicks,
        long lockWaitTicks,
        long queuePumpTicks,
        long resumeDrainTicks,
        long totalTicks)
    {
        if (!_enabled)
        {
            return;
        }

        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _dwords, dwordCount);
        Interlocked.Add(ref _setupTicks, setupTicks);
        Interlocked.Add(ref _snapshotTicks, snapshotTicks);
        Interlocked.Add(ref _lockWaitTicks, lockWaitTicks);
        Interlocked.Add(ref _queuePumpTicks, queuePumpTicks);
        Interlocked.Add(ref _resumeDrainTicks, resumeDrainTicks);
        Interlocked.Add(ref _totalTicks, totalTicks);
        UpdateMaximum(ref _maxTotalTicks, totalTicks);
        UpdateMaximum(ref _maxQueuePumpTicks, queuePumpTicks);

        TryReport();
    }

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
            var calls = Interlocked.Exchange(ref _calls, 0);
            var dwords = Interlocked.Exchange(ref _dwords, 0);
            var setupTicks = Interlocked.Exchange(ref _setupTicks, 0);
            var snapshotTicks = Interlocked.Exchange(ref _snapshotTicks, 0);
            var lockWaitTicks = Interlocked.Exchange(ref _lockWaitTicks, 0);
            var queuePumpTicks = Interlocked.Exchange(ref _queuePumpTicks, 0);
            var resumeDrainTicks = Interlocked.Exchange(ref _resumeDrainTicks, 0);
            var totalTicks = Interlocked.Exchange(ref _totalTicks, 0);
            var maxTotalTicks = Interlocked.Exchange(ref _maxTotalTicks, 0);
            var maxQueuePumpTicks = Interlocked.Exchange(ref _maxQueuePumpTicks, 0);

            if (calls == 0)
            {
                return;
            }

            Console.Error.WriteLine(
                $"[PERF][DCB_SUBMIT] window={elapsedSeconds:F1}s calls={calls} " +
                $"calls_per_s={calls / elapsedSeconds:F1} dwords={dwords} " +
                $"dwords_per_s={dwords / elapsedSeconds:F0} " +
                $"total_ms={ToMilliseconds(totalTicks):F2} " +
                $"setup_ms={ToMilliseconds(setupTicks):F2} " +
                $"snapshot_ms={ToMilliseconds(snapshotTicks):F2} " +
                $"lock_wait_ms={ToMilliseconds(lockWaitTicks):F2} " +
                $"queue_pump_ms={ToMilliseconds(queuePumpTicks):F2} " +
                $"resume_drain_ms={ToMilliseconds(resumeDrainTicks):F2} " +
                $"other_ms={ToMilliseconds(Math.Max(0, totalTicks - setupTicks - snapshotTicks - lockWaitTicks - queuePumpTicks - resumeDrainTicks)):F2} " +
                $"avg_call_ms={ToMilliseconds(totalTicks) / calls:F3} " +
                $"max_call_ms={ToMilliseconds(maxTotalTicks):F3} " +
                $"max_queue_pump_ms={ToMilliseconds(maxQueuePumpTicks):F3}");
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
