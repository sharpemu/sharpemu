// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.GnmMode.Shaders;

internal enum NormalizationInvalidation
{
    Reset,
    Color,
    Depth,
    ColorCopy,
    DepthCopy,
    Linear,
    DetileMiss,
    DetileMoved,
    Display,
}

internal enum TexturePreparation
{
    Ready,
    Geometry,
    GpuShadowChanged,
    GpuWritten,
    NoShadow,
    StaleEpoch,
}

internal static class GnmImageProfile
{
    private const int WindowSeconds = 5;
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1";
    private static readonly Timer? Reporter = Enabled
        ? new Timer(_ => Report(), null, TimeSpan.FromSeconds(WindowSeconds), TimeSpan.FromSeconds(WindowSeconds))
        : null;
    private static long _normalizations;
    private static long _normalizeTicks;
    private static long _detileHits;
    private static long _hashes;
    private static long _hashedBytes;
    private static long _hashTicks;
    private static long _cpuDetiles;
    private static long _cpuDetileTicks;
    private static long _gpuDetiles;
    private static long _gpuReused;
    private static long _abandoned;
    private static long _invalidations;
    private static readonly long[] _invalidationReasons = new long[Enum.GetValues<NormalizationInvalidation>().Length];
    private static readonly long[] _unprepared = new long[Enum.GetValues<TexturePreparation>().Length];

    internal static long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Normalized(long start)
    {
        if (start == 0) return;
        Interlocked.Increment(ref _normalizations);
        Interlocked.Add(ref _normalizeTicks, Stopwatch.GetTimestamp() - start);
    }

    internal static void DetileHit()
    {
        if (Enabled) Interlocked.Increment(ref _detileHits);
    }

    internal static void Hashed(long start, ulong bytes)
    {
        if (start == 0) return;
        Interlocked.Increment(ref _hashes);
        Interlocked.Add(ref _hashedBytes, (long)bytes);
        Interlocked.Add(ref _hashTicks, Stopwatch.GetTimestamp() - start);
    }

    internal static void CpuDetiled(long start)
    {
        if (start == 0) return;
        Interlocked.Increment(ref _cpuDetiles);
        Interlocked.Add(ref _cpuDetileTicks, Stopwatch.GetTimestamp() - start);
    }

    internal static void GpuDetiled()
    {
        if (Enabled) Interlocked.Increment(ref _gpuDetiles);
    }

    internal static void GpuReused()
    {
        if (Enabled) Interlocked.Increment(ref _gpuReused);
    }

    internal static void Abandoned()
    {
        if (Enabled) Interlocked.Increment(ref _abandoned);
    }

    internal static void NormalizationInvalidated(NormalizationInvalidation reason)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _invalidations);
        Interlocked.Increment(ref _invalidationReasons[(int)reason]);
    }

    internal static void Unprepared(TexturePreparation reason)
    {
        if (Enabled) Interlocked.Increment(ref _unprepared[(int)reason]);
    }

    private static string TakeInvalidationReasons()
    {
        var parts = new List<string>();
        for (var index = 0; index < _invalidationReasons.Length; index++)
        {
            var count = Interlocked.Exchange(ref _invalidationReasons[index], 0);
            if (count != 0)
                parts.Add($"{(NormalizationInvalidation)index}:{count}");
        }
        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

    private static string TakeUnprepared()
    {
        var parts = new List<string>();
        for (var index = 0; index < _unprepared.Length; index++)
        {
            var count = Interlocked.Exchange(ref _unprepared[index], 0);
            if (count != 0)
                parts.Add($"{(TexturePreparation)index}:{count}");
        }
        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

    private static void Report()
    {
        GC.KeepAlive(Reporter);
        var normalizations = Interlocked.Exchange(ref _normalizations, 0);
        var normalizeMs = Milliseconds(Interlocked.Exchange(ref _normalizeTicks, 0));
        var hits = Interlocked.Exchange(ref _detileHits, 0);
        var hashes = Interlocked.Exchange(ref _hashes, 0);
        var hashedMb = Interlocked.Exchange(ref _hashedBytes, 0) / (1024.0 * 1024.0);
        var hashMs = Milliseconds(Interlocked.Exchange(ref _hashTicks, 0));
        var cpuDetiles = Interlocked.Exchange(ref _cpuDetiles, 0);
        var cpuDetileMs = Milliseconds(Interlocked.Exchange(ref _cpuDetileTicks, 0));
        var gpuDetiles = Interlocked.Exchange(ref _gpuDetiles, 0);
        var gpuReused = Interlocked.Exchange(ref _gpuReused, 0);
        var abandoned = Interlocked.Exchange(ref _abandoned, 0);
        var invalidations = Interlocked.Exchange(ref _invalidations, 0);
        if (normalizations == 0 && hashes == 0 && gpuDetiles == 0 && gpuReused == 0 && abandoned == 0 &&
            invalidations == 0)
            return;
        Console.Error.WriteLine($"[PERF][GNM_IMAGES] window_s={WindowSeconds} normalize={normalizations}/{normalizeMs:F1}ms " +
            $"detile_hits={hits} hashes={hashes}/{hashedMb:F1}MB/{hashMs:F1}ms cpu_detiles={cpuDetiles}/{cpuDetileMs:F1}ms " +
            $"gpu_detiles={gpuDetiles} gpu_reused={gpuReused} abandoned={abandoned} invalidations={invalidations} " +
            $"invalidation_reasons={TakeInvalidationReasons()} worker_unprepared={TakeUnprepared()}");
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
