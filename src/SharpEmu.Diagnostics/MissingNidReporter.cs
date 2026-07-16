// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Tracks every unresolved NID (missing HLE export) and produces a final summary
/// at the end of execution. Saves reading through thousands of log lines.
///
/// Output:
///   Missing HLE exports (5 unique NIDs, 137 total calls):
///     libSceAgc:
///       bRujIheWlB0 (90 calls) — _ZSt14_Throw_C_errori
///       iS4aWbUonl0 (18 calls) — _Mtx_lock
///     libc:
///       ...
/// </summary>
public sealed class MissingNidReporter
{
    private readonly ConcurrentDictionary<string, MissingNidEntry> _missing = new();

    private static readonly Lazy<MissingNidReporter> _instance = new(() => new MissingNidReporter());
    public static MissingNidReporter Instance => _instance.Value;

    public void RecordUnresolved(string nid, string? resolvedName, string? libraryName, ulong returnRip)
    {
        _missing.AddOrUpdate(nid,
            _ => new MissingNidEntry(nid, resolvedName, libraryName, CallCount: 1, FirstReturnRip: returnRip, FirstSeen: DateTimeOffset.UtcNow),
            (_, existing) => existing with { CallCount = existing.CallCount + 1 });
    }

    public IReadOnlyCollection<MissingNidEntry> GetMissing() => _missing.Values.ToArray();

    public int UniqueCount => _missing.Count;

    public long TotalCalls => _missing.Values.Sum(e => e.CallCount);

    public string RenderReport()
    {
        var sb = new System.Text.StringBuilder();
        var missing = _missing.Values.OrderByDescending(e => e.CallCount).ToArray();
        sb.AppendLine($"========== Missing NID Report ==========");
        sb.AppendLine($"Unique missing: {missing.Length} | Total unresolved calls: {missing.Sum(e => e.CallCount)}");
        sb.AppendLine();

        if (missing.Length == 0)
        {
            sb.AppendLine("  All imports resolved! No missing HLE exports.");
            return sb.ToString();
        }

        var byLibrary = missing.GroupBy(e => e.LibraryName ?? "unknown").OrderByDescending(g => g.Sum(e => e.CallCount));
        foreach (var libGroup in byLibrary)
        {
            sb.AppendLine($"  {libGroup.Key}:");
            foreach (var entry in libGroup.OrderByDescending(e => e.CallCount))
            {
                var name = entry.ResolvedName ?? "<unknown>";
                sb.AppendLine($"    {entry.Nid,-14} ({entry.CallCount,4} calls) — {name}");
                sb.AppendLine($"      first caller RIP: 0x{entry.FirstReturnRip:X16}");
            }
        }

        return sb.ToString();
    }

    public void Reset() => _missing.Clear();
}

public readonly record struct MissingNidEntry(
    string Nid,
    string? ResolvedName,
    string? LibraryName,
    long CallCount,
    ulong FirstReturnRip,
    DateTimeOffset FirstSeen);
