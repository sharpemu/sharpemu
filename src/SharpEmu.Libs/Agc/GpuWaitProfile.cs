// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Aggregate accounting for suspended WAIT_REG_MEM packets, enabled with
/// SHARPEMU_PROFILE_GPU_WAIT=1 or the unified performance profile.
///
/// The existing <c>agc.wait_suspended</c> warning is deduplicated per label, so
/// a label that suspends every frame is reported once and then goes silent —
/// which makes the log useless for judging whether GPU waits cost frame time.
/// This counts every suspension and every resume, and reports how long the
/// queues actually sat blocked.
/// </summary>
internal static class GpuWaitProfile
{
    public static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GPU_WAIT"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    private static readonly double _reportSeconds =
        double.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GPU_WAIT_REPORT_S"),
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? seconds
            : 5.0;

    private static readonly object _gate = new();
    private static readonly Dictionary<ulong, (long Count, double Milliseconds)> _byLabel = new();
    private static long _suspensions;
    private static long _resumes;
    private static long _producerless;
    private static long _monitorPolls;
    private static long _monitorEmptyPolls;
    private static double _totalWaitMilliseconds;
    private static double _maxWaitMilliseconds;
    private static long _windowStart = Stopwatch.GetTimestamp();

    public static void RecordSuspend(bool hasProducer)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _suspensions++;
            if (!hasProducer)
            {
                _producerless++;
            }
        }
    }

    public static void RecordResume(ulong label, double waitedMilliseconds)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _resumes++;
            _totalWaitMilliseconds += waitedMilliseconds;
            if (waitedMilliseconds > _maxWaitMilliseconds)
            {
                _maxWaitMilliseconds = waitedMilliseconds;
            }

            if (_byLabel.Count < 4096)
            {
                var existing = _byLabel.TryGetValue(label, out var entry) ? entry : default;
                _byLabel[label] = (existing.Count + 1, existing.Milliseconds + waitedMilliseconds);
            }
        }
    }

    /// <summary>
    /// Called once per wake of the wait monitor. An empty poll means the monitor
    /// burned a wakeup without resuming anything, which is the cost of the
    /// backoff loop rather than of the wait itself.
    /// </summary>
    public static void RecordMonitorPoll(bool resumedAny)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _monitorPolls++;
            if (!resumedAny)
            {
                _monitorEmptyPolls++;
            }
        }
    }

    public static void ReportIfDue(int remainingWaiters)
    {
        if (!Enabled)
        {
            return;
        }

        string line;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedTicks = now - _windowStart;
            if (elapsedTicks < _reportSeconds * Stopwatch.Frequency)
            {
                return;
            }

            _windowStart = now;
            var seconds = elapsedTicks / (double)Stopwatch.Frequency;

            // Total blocked time across all queues. Above 1000ms/s the queues are
            // overlapping their stalls, so compare it against the frame budget,
            // not against wall time.
            var top = _byLabel
                .OrderByDescending(entry => entry.Value.Milliseconds)
                .Take(5)
                .Select(entry =>
                    $"0x{entry.Key:X}={entry.Value.Milliseconds / seconds:F0}ms/s" +
                    $"/n{entry.Value.Count}")
                .ToArray();

            line =
                $"[PERF][GPUWAIT] {seconds:F1}s suspend/s={_suspensions / seconds:F0} " +
                $"resume/s={_resumes / seconds:F0} producerless/s={_producerless / seconds:F0} " +
                $"blocked_ms/s={_totalWaitMilliseconds / seconds:F0} " +
                $"avg_ms={(_resumes > 0 ? _totalWaitMilliseconds / _resumes : 0):F2} " +
                $"max_ms={_maxWaitMilliseconds:F1} " +
                $"monitor_polls/s={_monitorPolls / seconds:F0} " +
                $"empty={(_monitorPolls > 0 ? _monitorEmptyPolls * 100.0 / _monitorPolls : 0):F0}% " +
                $"outstanding={remainingWaiters} top: {string.Join(" | ", top)}";

            _suspensions = 0;
            _resumes = 0;
            _producerless = 0;
            _monitorPolls = 0;
            _monitorEmptyPolls = 0;
            _totalWaitMilliseconds = 0;
            _maxWaitMilliseconds = 0;
            _byLabel.Clear();
        }

        Console.Error.WriteLine(line);
    }
}
