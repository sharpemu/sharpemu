// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

internal static class AgcRegisterPacketProfile
{
    internal enum Phase { ImportDispatch, Emitter, PacketSetup, BulkCopy, ScalarCopy, Count }
    internal enum PayloadPath { BulkCopied, BulkDeclined, NullSource, Overlap, WrappedRange, Count }
    internal readonly record struct PhaseSnapshot(long Calls, long Ticks);
    internal readonly record struct PathSnapshot(long Calls, long Words);

    internal sealed class Counters
    {
        private readonly long[] _calls = new long[(int)Phase.Count];
        private readonly long[] _ticks = new long[(int)Phase.Count];
        private readonly long[] _pathCalls = new long[(int)PayloadPath.Count];
        private readonly long[] _pathWords = new long[(int)PayloadPath.Count];
        private long _succeeded;
        private long _failed;

        internal void RecordPhase(Phase phase, long ticks)
        {
            Interlocked.Add(ref _ticks[(int)phase], ticks);
            Interlocked.Increment(ref _calls[(int)phase]);
        }

        internal void RecordPath(PayloadPath path, uint words)
        {
            Interlocked.Add(ref _pathWords[(int)path], words);
            Interlocked.Increment(ref _pathCalls[(int)path]);
        }

        internal void RecordResult(bool succeeded)
        {
            if (succeeded) Interlocked.Increment(ref _succeeded);
            else Interlocked.Increment(ref _failed);
        }

        internal PhaseSnapshot ReadPhase(Phase phase) =>
            new(Interlocked.Read(ref _calls[(int)phase]), Interlocked.Read(ref _ticks[(int)phase]));
        internal PathSnapshot ReadPath(PayloadPath path) =>
            new(Interlocked.Read(ref _pathCalls[(int)path]), Interlocked.Read(ref _pathWords[(int)path]));
        internal long Succeeded => Interlocked.Read(ref _succeeded);
        internal long Failed => Interlocked.Read(ref _failed);
    }

    internal static readonly bool Enabled = IsEnabled(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE"));

    internal static bool IsEnabled(string? performance, string? frameTrace) => performance == "1" && frameTrace == "1";

    private static class Storage
    {
        internal static readonly Counters Totals = new();
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly Counters? _counters;
        private readonly Phase _phase;
        private readonly long _started;

        internal Scope(Counters counters, Phase phase)
        {
            _counters = counters;
            _phase = phase;
            _started = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_counters is not null)
                _counters.RecordPhase(_phase, Stopwatch.GetTimestamp() - _started);
        }
    }

    internal static Scope Measure(Phase phase) => Enabled ? new(Storage.Totals, phase) : default;
    internal static Scope MeasureImport(string nid) =>
        Enabled && nid == "n2fD4A+pb+g" ? Measure(Phase.ImportDispatch) : default;

    internal static void RecordPath(PayloadPath path, uint words)
    {
        if (Enabled) Storage.Totals.RecordPath(path, words);
    }

    internal static void RecordResult(bool succeeded)
    {
        if (Enabled) Storage.Totals.RecordResult(succeeded);
    }

    // Inclusive cumulative times overlap. Reports can observe calls that are still in progress.
    internal static void WriteReport()
    {
        if (!Enabled) return;
        var counters = Storage.Totals;
        if (counters.ReadPhase(Phase.Emitter).Calls == 0) return;
        var parts = new List<string>();
        for (var index = 0; index < (int)Phase.Count; index++)
        {
            var phase = (Phase)index;
            var snapshot = counters.ReadPhase(phase);
            parts.Add(FormattableString.Invariant($"{phase}={snapshot.Ticks * 1000.0 / Stopwatch.Frequency:F3}ms/n{snapshot.Calls}"));
        }
        for (var index = 0; index < (int)PayloadPath.Count; index++)
        {
            var path = (PayloadPath)index;
            var snapshot = counters.ReadPath(path);
            parts.Add($"{path}={snapshot.Calls}/words{snapshot.Words}");
        }
        Console.Error.WriteLine($"[PERF][AGC_REGISTER_PACKET] cumulative=1 inclusive=1 succeeded={counters.Succeeded} failed={counters.Failed} {string.Join(" ", parts)}");
    }
}
