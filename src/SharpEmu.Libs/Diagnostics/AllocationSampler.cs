// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace SharpEmu.Libs.Diagnostics;

/// <summary>
/// Samples managed allocations by type, enabled with SHARPEMU_TRACE_ALLOC=1. The runtime raises
/// a GCAllocationTick event about every 100 KB allocated on a thread, naming the type of the
/// object that crossed the mark, so summing the ticks per type gives a cheap picture of which
/// types drive the GC pauses. The report is printed with the render profile window.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private const int TopTypes = 25;
    private const long GcKeyword = 0x1;

    internal static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_ALLOC"), "1", StringComparison.Ordinal);

    private static AllocationSampler? _instance;

    // Bytes allocated at a few known per-draw call sites, so a large System.Byte[] total can be traced to its source.
    internal enum Site
    {
        VertexCapture,
        RestartIndexConversion,
        Index8Expansion,
        Count,
    }

    private static readonly long[] _siteBytes = new long[(int)Site.Count];

    internal static void Note(Site site, long bytes)
    {
        if (Enabled)
        {
            Interlocked.Add(ref _siteBytes[(int)site], bytes);
        }
    }

    private readonly ConcurrentDictionary<string, long> _bytesByType = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _bytesByThread = new(StringComparer.Ordinal);

    internal static void EnsureStarted()
    {
        if (Enabled && _instance is null)
        {
            Interlocked.CompareExchange(ref _instance, new AllocationSampler(), null);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName is null || !eventData.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            return;
        }

        var payload = eventData.Payload;
        var names = eventData.PayloadNames;
        if (payload is null || names is null)
        {
            return;
        }

        var amount = 0L;
        var type = "?";
        for (var index = 0; index < names.Count; index++)
        {
            switch (names[index])
            {
                case "AllocationAmount64":
                    amount = Convert.ToInt64(payload[index]);
                    break;
                case "TypeName":
                    type = payload[index] as string ?? "?";
                    break;
            }
        }

        _bytesByType.AddOrUpdate(type, amount, (_, total) => total + amount);
        var thread = Thread.CurrentThread.Name ?? $"tid{Environment.CurrentManagedThreadId}";
        _bytesByThread.AddOrUpdate(thread, amount, (_, total) => total + amount);
    }

    internal static string? TakeReport()
    {
        var instance = _instance;
        if (instance is null)
        {
            return null;
        }

        var sites = string.Join(" ", Enumerable.Range(0, (int)Site.Count).Select(static index =>
            FormattableString.Invariant($"{(Site)index}={Interlocked.Exchange(ref _siteBytes[index], 0) / 1048576.0:F1}")));
        return "[PERF][ALLOC_TYPES] " + Drain(instance._bytesByType) + "\n[PERF][ALLOC_THREADS] " + Drain(instance._bytesByThread) +
            "\n[PERF][ALLOC_SITES] " + sites;
    }

    private static string Drain(ConcurrentDictionary<string, long> totals)
    {
        var snapshot = new List<KeyValuePair<string, long>>();
        foreach (var key in totals.Keys)
        {
            if (totals.TryRemove(key, out var bytes))
            {
                snapshot.Add(new(key, bytes));
            }
        }

        snapshot.Sort(static (left, right) => right.Value.CompareTo(left.Value));
        var total = snapshot.Sum(static pair => pair.Value);
        return FormattableString.Invariant($"total_mb={total / 1048576.0:F0} ") + string.Join(" ",
            snapshot.Take(TopTypes).Select(static pair =>
                FormattableString.Invariant($"{pair.Key.Replace(' ', '_')}={pair.Value / 1048576.0:F1}")));
    }
}
