// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Pipelines;

/// <summary>
/// SHARPEMU_PROFILE_PIPELINE_CACHE=1: lookups, misses and miss time of the program, permutation, translation and
/// pipeline caches. Every second one `[PERF][PIPELINE_CACHE]` line gives the counts of that second; a miss that
/// takes 1 ms or more also prints `[PERF][PIPELINE_MISS]` with the time since the first lookup, so compile stalls
/// can be lined up with hitches and load phases. Measurement only: no behaviour change.
/// </summary>
public static class PipelineCacheStats
{
    public enum Kind { ProgramEntry, Permutation, Translate, GraphicsPipeline, ComputePipeline, Count }

    public static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PIPELINE_CACHE"), "1", StringComparison.Ordinal);

    private static readonly long[] Lookups = new long[(int)Kind.Count];
    private static readonly long[] Misses = new long[(int)Kind.Count];
    private static readonly long[] MissTicks = new long[(int)Kind.Count];
    private static readonly long[] MaxMissTicks = new long[(int)Kind.Count];
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _reporting;

    public static void Lookup(Kind kind, bool hit)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref Lookups[(int)kind]);
        if (!hit)
        {
            Interlocked.Increment(ref Misses[(int)kind]);
        }

        MaybeReport();
    }

    /// <summary>Start of a miss that is about to be filled; 0 when the profile is off.</summary>
    public static long MissBegin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void MissEnd(Kind kind, long started, string detail)
    {
        if (started == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var ticks = now - started;
        Interlocked.Add(ref MissTicks[(int)kind], ticks);
        long seen;
        while (ticks > (seen = Interlocked.Read(ref MaxMissTicks[(int)kind])) &&
               Interlocked.CompareExchange(ref MaxMissTicks[(int)kind], ticks, seen) != seen)
        {
        }

        var milliseconds = ticks * 1000.0 / Stopwatch.Frequency;
        if (milliseconds >= 1.0)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][PIPELINE_MISS] t={(started - Origin) / (double)Stopwatch.Frequency:F3}s kind={kind} ms={milliseconds:F2} thread='{Thread.CurrentThread.Name}' {detail}"));
        }

        SharpEmu.HLE.Diagnostics.FrameTimeline.End(
            SharpEmu.HLE.Diagnostics.FrameTimeline.Begin() == 0 ? 0 : started, $"pipeline:{kind} miss ({detail})", 200);
        MaybeReport();
    }

    private static void MaybeReport()
    {
        var now = Stopwatch.GetTimestamp();
        var started = Interlocked.Read(ref _windowStart);
        if (now - started < Stopwatch.Frequency || Interlocked.Exchange(ref _reporting, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _windowStart, now);
            var parts = new string[(int)Kind.Count];
            for (var index = 0; index < parts.Length; index++)
            {
                var lookups = Interlocked.Exchange(ref Lookups[index], 0);
                var misses = Interlocked.Exchange(ref Misses[index], 0);
                var ticks = Interlocked.Exchange(ref MissTicks[index], 0);
                var max = Interlocked.Exchange(ref MaxMissTicks[index], 0);
                parts[index] = FormattableString.Invariant(
                    $"{(Kind)index}={lookups}/{misses}miss/{ticks * 1000.0 / Stopwatch.Frequency:F1}ms(max{max * 1000.0 / Stopwatch.Frequency:F1})");
            }

            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][PIPELINE_CACHE] t={(now - Origin) / (double)Stopwatch.Frequency:F1}s lookups/misses/miss_ms: {string.Join(" ", parts)}"));
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }
}
