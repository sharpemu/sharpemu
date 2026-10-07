// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GuestMemory;

public static class ReservationDiagnostics
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    private static readonly object Gate = new();
    private static readonly Dictionary<Sample, (long Calls, long Ticks)> Samples = new();
    private static readonly Dictionary<string, (long Calls, long Ticks)> Totals = new();
    private static readonly Dictionary<string, int> SampleCounts = new();
    private static long _omitted;
    private const int Capacity = 32;

    private readonly record struct Sample(string Stage, ulong Address, ulong Size, ulong Alignment,
        ulong RegionBase, ulong RegionSize, uint State, int Error, bool Success);

    public static void Record(string stage, ulong address, ulong size = 0, ulong alignment = 0,
        ulong regionBase = 0, ulong regionSize = 0, uint state = 0, int error = 0,
        bool success = false, long started = 0)
    {
        if (!Enabled) return;
        var ticks = started == 0 ? 0 : Stopwatch.GetTimestamp() - started;
        var sample = new Sample(stage, address, size, alignment, regionBase, regionSize, state, error, success);
        lock (Gate)
        {
            Totals.TryGetValue(stage, out var total);
            Totals[stage] = (total.Calls + 1, total.Ticks + ticks);
            var found = Samples.TryGetValue(sample, out var value);
            SampleCounts.TryGetValue(stage, out var count);
            if (!found && (Samples.Count == Capacity || count >= 4))
            {
                _omitted++;
                return;
            }
            if (!found) SampleCounts[stage] = count + 1;
            Samples[sample] = (value.Calls + 1, value.Ticks + ticks);
        }
    }

    public static void WriteReport()
    {
        if (!Enabled) return;
        KeyValuePair<Sample, (long Calls, long Ticks)>[] samples;
        long omitted;
        KeyValuePair<string, (long Calls, long Ticks)>[] totals;
        lock (Gate)
        {
            samples = Samples.ToArray();
            totals = Totals.ToArray();
            Totals.Clear();
            omitted = _omitted;
            Samples.Clear();
            SampleCounts.Clear();
            _omitted = 0;
        }
        foreach (var (sample, value) in samples)
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][RESERVATION_DETAIL] stage={sample.Stage} address=0x{sample.Address:X} size=0x{sample.Size:X} alignment=0x{sample.Alignment:X} region_base=0x{sample.RegionBase:X} region_size=0x{sample.RegionSize:X} state=0x{sample.State:X} error={sample.Error} success={sample.Success} calls={value.Calls} inclusive_ms={value.Ticks * 1000.0 / Stopwatch.Frequency:F3}"));
        foreach (var (stage, value) in totals)
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][RESERVATION_TOTAL] stage={stage} calls={value.Calls} inclusive_ms={value.Ticks * 1000.0 / Stopwatch.Frequency:F3}"));
        if (omitted != 0)
            Console.Error.WriteLine($"[PERF][RESERVATION_DETAIL] omitted_calls={omitted}");
    }
}
