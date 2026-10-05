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
    private static long _vertexSnapshotMisses;
    private static long _vertexSnapshotMismatches;
    private static long _vertexSnapshotReuses;
    private static readonly long[] _captureSelections = new long[3];
    private static int _reportedCaptureRejection;

    internal static void RecordCaptureSelection(AgcExports.GeometrySnapshotDecision decision)
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _captureSelections[(int)decision]);
        }
    }

    internal static void RecordCaptureRejection(string field, ulong captured, ulong current)
    {
        if (Enabled && Interlocked.Exchange(ref _reportedCaptureRejection, 1) == 0)
        {
            Console.Error.WriteLine($"[PERF][GEOMETRY_CAPTURE_REJECTION] field={field} captured=0x{captured:X} current=0x{current:X}");
        }
    }

    public static bool Enabled => _enabled;

    internal enum SnapshotPhase { IndexCapture, Evaluation, RetainedCopy, Registers, Count }
    private static readonly long[] _snapshotPhaseTicks = new long[(int)SnapshotPhase.Count];
    private static readonly long[] _snapshotPhaseCalls = new long[(int)SnapshotPhase.Count];
    private static long _retainedVertexBytes;

    internal readonly struct SnapshotScope(SnapshotPhase phase) : IDisposable
    {
        private readonly long _started = Enabled ? Stopwatch.GetTimestamp() : 0;
        public void Dispose()
        {
            if (_started != 0) RecordSnapshotPhase(phase, Stopwatch.GetTimestamp() - _started);
        }
    }

    internal static void RecordSnapshotPhase(SnapshotPhase phase, long ticks)
    {
        if (!Enabled) return;
        Interlocked.Add(ref _snapshotPhaseTicks[(int)phase], ticks);
        Interlocked.Increment(ref _snapshotPhaseCalls[(int)phase]);
    }

    internal static long SnapshotRemainder(long total, ReadOnlySpan<long> phases)
    {
        long accounted = 0;
        for (var index = 0; index < phases.Length; index++)
        {
            accounted += phases[index];
        }
        return Math.Max(0, total - accounted);
    }

    internal static void RecordRetainedVertexBytes(long bytes)
    {
        if (Enabled) Interlocked.Add(ref _retainedVertexBytes, bytes);
    }

    public static void RecordVertexSnapshot(bool available, bool matched)
    {
        if (!_enabled)
        {
            return;
        }
        if (!available)
        {
            Interlocked.Increment(ref _vertexSnapshotMisses);
        }
        else if (!matched)
        {
            Interlocked.Increment(ref _vertexSnapshotMismatches);
        }
        else
        {
            Interlocked.Increment(ref _vertexSnapshotReuses);
        }
    }

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
            var snapshotMisses = Interlocked.Exchange(ref _vertexSnapshotMisses, 0);
            var snapshotMismatches = Interlocked.Exchange(ref _vertexSnapshotMismatches, 0);
            var snapshotReuses = Interlocked.Exchange(ref _vertexSnapshotReuses, 0);
            var captureMissing = Interlocked.Exchange(ref _captureSelections[0], 0);
            var captureAccepted = Interlocked.Exchange(ref _captureSelections[1], 0);
            var captureRejected = Interlocked.Exchange(ref _captureSelections[2], 0);
            Interlocked.Exchange(ref _reportedCaptureRejection, 0);
            var phaseTicks = new long[_snapshotPhaseTicks.Length];
            var phaseCalls = new long[_snapshotPhaseCalls.Length];
            for (var index = 0; index < phaseTicks.Length; index++)
            {
                phaseTicks[index] = Interlocked.Exchange(ref _snapshotPhaseTicks[index], 0);
                phaseCalls[index] = Interlocked.Exchange(ref _snapshotPhaseCalls[index], 0);
            }
            var retainedBytes = Interlocked.Exchange(ref _retainedVertexBytes, 0);

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
            Console.Error.WriteLine(
                $"[PERF][VERTEX_SNAPSHOT] window={elapsedSeconds:F1}s reused={snapshotReuses} " +
                $"missing={snapshotMisses} mismatch={snapshotMismatches} " +
                $"capture_missing={captureMissing} capture_accepted={captureAccepted} capture_rejected={captureRejected}");
            Console.Error.WriteLine(
                $"[PERF][SNAPSHOT_PREPASS] window={elapsedSeconds:F1}s total_ms={ToMilliseconds(snapshotTicks):F2} " +
                $"packet_other_ms={ToMilliseconds(SnapshotRemainder(snapshotTicks, phaseTicks)):F2} " +
                $"index_capture_ms={ToMilliseconds(phaseTicks[(int)SnapshotPhase.IndexCapture]):F2}/n{phaseCalls[(int)SnapshotPhase.IndexCapture]} " +
                $"evaluation_ms={ToMilliseconds(phaseTicks[(int)SnapshotPhase.Evaluation]):F2}/n{phaseCalls[(int)SnapshotPhase.Evaluation]} " +
                $"retained_copy_ms={ToMilliseconds(phaseTicks[(int)SnapshotPhase.RetainedCopy]):F2}/n{phaseCalls[(int)SnapshotPhase.RetainedCopy]} retained_bytes={retainedBytes} " +
                $"registers_ms={ToMilliseconds(phaseTicks[(int)SnapshotPhase.Registers]):F2}/n{phaseCalls[(int)SnapshotPhase.Registers]}");
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
