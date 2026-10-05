// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.Agc;

internal static class TexturePreparationProfile
{
    internal enum BindingOutcome
    {
        Reused,
        Empty,
        Linear,
        Tiled,
        Fallback,
        Skipped,
    }

    internal enum ContentCacheStatus
    {
        Absent,
        Stale,
    }

    private readonly record struct CacheMissKey(
        ulong Address,
        uint Width,
        uint Height,
        uint Format,
        uint Type,
        ContentCacheStatus Status);

    private static readonly bool _enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_TEXTURE_PREPARATION"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _reporting;
    private static readonly long[] OutcomeCalls = new long[6];
    private static readonly long[] OutcomeBytes = new long[6];
    private static readonly long[] OutcomeTicks = new long[6];
    private static long _uploadKnownCalls;
    private static long _uploadKnownHits;
    private static long _uploadKnownTicks;
    private static long _contentCacheCalls;
    private static long _contentCacheHits;
    private static long _contentCacheTicks;
    private static ConcurrentDictionary<CacheMissKey, long> _cacheMisses = new();

    public static bool Enabled => _enabled;

    public static long Begin() => _enabled ? Stopwatch.GetTimestamp() : 0;

    public static void RecordBinding(
        BindingOutcome outcome,
        long startedAt,
        long payloadBytes = 0)
    {
        if (!_enabled)
        {
            return;
        }

        var index = (int)outcome;
        Interlocked.Increment(ref OutcomeCalls[index]);
        Interlocked.Add(ref OutcomeBytes[index], payloadBytes);
        Interlocked.Add(ref OutcomeTicks[index], Stopwatch.GetTimestamp() - startedAt);
        TryReport();
    }

    public static void RecordUploadKnown(long startedAt, bool hit)
    {
        if (!_enabled)
        {
            return;
        }

        Interlocked.Increment(ref _uploadKnownCalls);
        Interlocked.Add(ref _uploadKnownTicks, Stopwatch.GetTimestamp() - startedAt);
        if (hit)
        {
            Interlocked.Increment(ref _uploadKnownHits);
        }
    }

    public static void RecordContentCache(long startedAt, bool hit)
    {
        if (!_enabled)
        {
            return;
        }

        Interlocked.Increment(ref _contentCacheCalls);
        Interlocked.Add(ref _contentCacheTicks, Stopwatch.GetTimestamp() - startedAt);
        if (hit)
        {
            Interlocked.Increment(ref _contentCacheHits);
        }
    }

    public static void RecordContentCacheStatus(
        in TextureContentIdentity identity,
        ContentCacheStatus status)
    {
        if (!_enabled)
        {
            return;
        }

        var key = new CacheMissKey(
            identity.Address,
            identity.Width,
            identity.Height,
            identity.Format,
            identity.Type,
            status);
        Volatile.Read(ref _cacheMisses).AddOrUpdate(
            key,
            1,
            static (_, count) => count + 1);
    }

    private static void TryReport()
    {
        var now = Stopwatch.GetTimestamp();
        var windowStart = Volatile.Read(ref _windowStart);
        if (now - windowStart < 5L * Stopwatch.Frequency ||
            Interlocked.CompareExchange(ref _reporting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            now = Stopwatch.GetTimestamp();
            windowStart = Interlocked.Exchange(ref _windowStart, now);
            var seconds = Math.Max(
                (now - windowStart) / (double)Stopwatch.Frequency,
                double.Epsilon);
            var outcomeCalls = ExchangeAll(OutcomeCalls);
            var outcomeBytes = ExchangeAll(OutcomeBytes);
            var outcomeTicks = ExchangeAll(OutcomeTicks);
            var uploadKnownCalls = Interlocked.Exchange(ref _uploadKnownCalls, 0);
            var uploadKnownHits = Interlocked.Exchange(ref _uploadKnownHits, 0);
            var uploadKnownTicks = Interlocked.Exchange(ref _uploadKnownTicks, 0);
            var contentCacheCalls = Interlocked.Exchange(ref _contentCacheCalls, 0);
            var contentCacheHits = Interlocked.Exchange(ref _contentCacheHits, 0);
            var contentCacheTicks = Interlocked.Exchange(ref _contentCacheTicks, 0);
            var cacheMisses = Interlocked.Exchange(
                ref _cacheMisses,
                new ConcurrentDictionary<CacheMissKey, long>());

            Console.Error.WriteLine(
                $"[PERF][TEXTURE_PREP] {seconds:F1}s " +
                Describe(BindingOutcome.Reused, outcomeCalls, outcomeBytes, outcomeTicks) + " " +
                Describe(BindingOutcome.Empty, outcomeCalls, outcomeBytes, outcomeTicks) + " " +
                Describe(BindingOutcome.Linear, outcomeCalls, outcomeBytes, outcomeTicks) + " " +
                Describe(BindingOutcome.Tiled, outcomeCalls, outcomeBytes, outcomeTicks) + " " +
                Describe(BindingOutcome.Fallback, outcomeCalls, outcomeBytes, outcomeTicks) + " " +
                Describe(BindingOutcome.Skipped, outcomeCalls, outcomeBytes, outcomeTicks) + " " +
                $"upload_known_ms={ToMilliseconds(uploadKnownTicks):F2}/" +
                $"n{uploadKnownCalls}/hit{uploadKnownHits} " +
                $"content_cache_ms={ToMilliseconds(contentCacheTicks):F2}/" +
                $"n{contentCacheCalls}/hit{contentCacheHits} " +
                DescribeCacheMisses(cacheMisses));
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }

    private static string Describe(
        BindingOutcome outcome,
        IReadOnlyList<long> calls,
        IReadOnlyList<long> bytes,
        IReadOnlyList<long> ticks)
    {
        var index = (int)outcome;
        return
            $"{outcome.ToString().ToLowerInvariant()}=" +
            $"{ToMilliseconds(ticks[index]):F2}ms/" +
            $"n{calls[index]}/" +
            $"{bytes[index] / (1024.0 * 1024.0):F1}MiB";
    }

    private static string DescribeCacheMisses(
        IReadOnlyDictionary<CacheMissKey, long> cacheMisses)
    {
        if (cacheMisses.Count == 0)
        {
            return "misses=none";
        }

        return "misses=" + string.Join(
            ",",
            cacheMisses
                .OrderByDescending(static pair => pair.Value)
                .Take(6)
                .Select(static pair =>
                    $"0x{pair.Key.Address:X}:" +
                    $"{pair.Key.Width}x{pair.Key.Height}:" +
                    $"f{pair.Key.Format}:t{pair.Key.Type}:" +
                    $"{pair.Key.Status.ToString().ToLowerInvariant()}:" +
                    $"n{pair.Value}"));
    }

    private static long[] ExchangeAll(long[] counters)
    {
        var snapshot = new long[counters.Length];
        for (var index = 0; index < counters.Length; index++)
        {
            snapshot[index] = Interlocked.Exchange(ref counters[index], 0);
        }

        return snapshot;
    }

    private static double ToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;
}
