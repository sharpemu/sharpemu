// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Diagnostics;

internal sealed class CallSiteTally(string tag, int sampleEvery)
{
    private const int SiteFrames = 6;

    private static readonly HashSet<string> PlumbingTypes = new(StringComparer.Ordinal)
    {
        "CallSiteTally",
        "SubmitSiteTally",
        "PromotionSiteTally",
        "RecordingBuffer",
        "TickTimeline",
        "SubmissionScheduler",
        "VulkanTickDevice",
        "GpuWorkerRelay",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sites = new(StringComparer.Ordinal);
    private long _calls;

    public void Note()
    {
        if (sampleEvery > 1 && Interlocked.Increment(ref _calls) % sampleEvery != 0)
        {
            return;
        }

        var names = new List<string>(SiteFrames);
        foreach (var frame in new StackTrace(1, false).GetFrames())
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

        var site = string.Join(" < ", names);
        lock (_gate)
        {
            _sites.TryGetValue(site, out var count);
            _sites[site] = count + 1;
        }
    }

    public void Report()
    {
        List<KeyValuePair<string, long>> ranked;
        lock (_gate)
        {
            if (_sites.Count == 0)
            {
                return;
            }

            ranked = _sites.OrderByDescending(static pair => pair.Value).Take(10).ToList();
            _sites.Clear();
        }

        foreach (var (site, count) in ranked)
        {
            Console.Error.WriteLine(FormattableString.Invariant($"[PERF][{tag}] n={count * sampleEvery} sample_every={sampleEvery} site={site}"));
        }
    }
}

internal static class SubmitSiteTally
{
    public static readonly bool Enabled = VideoOut.RenderPhaseProfile.Enabled;

    private static readonly CallSiteTally Tally = new("SUBMIT_SITE", 64);

    public static void Note() => Tally.Note();

    public static void Report() => Tally.Report();
}

internal static class PromotionSiteTally
{
    public static readonly bool Enabled = VideoOut.RenderPhaseProfile.Enabled;

    private static readonly CallSiteTally Tally = new("PROMOTION_SITE", 64);

    public static void Note() => Tally.Note();

    public static void Report() => Tally.Report();
}
