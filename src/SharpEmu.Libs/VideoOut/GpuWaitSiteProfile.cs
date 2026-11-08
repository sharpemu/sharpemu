// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

internal static class GpuWaitSiteProfile
{
    private const int SiteFrames = 9;

    private static readonly object _gate = new();
    private static readonly Dictionary<string, (long Count, long Ticks)> _sites = new(StringComparer.Ordinal);

    private static readonly HashSet<string> PlumbingTypes = new(StringComparer.Ordinal)
    {
        "GpuWaitSiteProfile",
        "TickTimeline",
        "SubmissionScheduler",
        "VulkanTickDevice",
        "VulkanAsyncReadback",
        "GpuWorkerRelay",
        "GpuWaitSpin",
    };

    public static long Begin() => RenderPhaseProfile.DetailMeasurementsEnabled ? Stopwatch.GetTimestamp() : 0;

    public static void End(long start)
    {
        if (start == 0)
        {
            return;
        }

        var elapsed = Stopwatch.GetTimestamp() - start;
        var site = Site();
        lock (_gate)
        {
            _sites.TryGetValue(site, out var entry);
            _sites[site] = (entry.Count + 1, entry.Ticks + elapsed);
        }
    }

    private static string Site()
    {
        var names = new List<string>(SiteFrames);
        foreach (var frame in new StackTrace(2, false).GetFrames())
        {
            if (frame.GetMethod() is not { } method)
            {
                continue;
            }

            var type = method.DeclaringType;
            while (type is { DeclaringType: not null } && type.Name.StartsWith('<'))
            {
                type = type.DeclaringType;
            }

            var typeName = type?.Name ?? "?";
            if (PlumbingTypes.Contains(typeName) || method.Name.StartsWith('<'))
            {
                continue;
            }

            names.Add($"{typeName}.{method.Name}");
            if (names.Count == SiteFrames)
            {
                break;
            }
        }

        return string.Join(" < ", names);
    }

    public static void Report()
    {
        List<KeyValuePair<string, (long Count, long Ticks)>> ranked;
        lock (_gate)
        {
            if (_sites.Count == 0)
            {
                return;
            }

            ranked = _sites.OrderByDescending(pair => pair.Value.Ticks).Take(12).ToList();
            _sites.Clear();
        }

        foreach (var (site, (count, ticks)) in ranked)
        {
            Console.Error.WriteLine(
                $"[PERF][GPU_WAIT_SITE] ms={ticks * 1000.0 / Stopwatch.Frequency:F1} n={count} site={site}");
        }
    }
}
