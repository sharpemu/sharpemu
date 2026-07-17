// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Detects infinite loops caused by bad HLE stub return values.
/// When a NID is called more than LoopThreshold times from the same caller,
/// it's flagged as a loop suspect and the emulator can take action.
///
/// Also includes Stub Learning: tries different return values for unknown
/// NIDs and remembers which one works.
/// </summary>
public sealed class ImportLoopDetector
{
    public const int LoopThreshold = 10000;
    public const int LoopThresholdPerSecond = 5000;

    private readonly ConcurrentDictionary<string, LoopStats> _stats = new();
    private readonly ConcurrentDictionary<string, LearnedStub> _learnedStubs = new();
    private readonly Stopwatch _timer = Stopwatch.StartNew();

    private static readonly Lazy<ImportLoopDetector> _instance = new(() => new ImportLoopDetector());
    public static ImportLoopDetector Instance => _instance.Value;

    public (bool IsLoop, LoopStats? Stats) CheckLoop(string nid, ulong callerRip)
    {
        var key = $"{nid}@{callerRip:X16}";
        var stats = _stats.AddOrUpdate(
            key,
            _ => new LoopStats(nid, callerRip, 1, _timer.ElapsedMilliseconds),
            (_, existing) => existing with { CallCount = existing.CallCount + 1 });

        if (stats.CallCount >= LoopThreshold)
        {
            var elapsedSec = Math.Max(1, (_timer.ElapsedMilliseconds - stats.FirstCallMs) / 1000.0);
            var callsPerSec = stats.CallCount / elapsedSec;

            if (callsPerSec > LoopThresholdPerSecond || stats.CallCount > LoopThreshold)
            {
                if (stats.CallCount == LoopThreshold || stats.CallCount % 100000 == 0)
                {
                    Console.Error.WriteLine(
                        $"[LOOP_DETECTOR] NID '{nid}' called {stats.CallCount:N0} times " +
                        $"from 0x{callerRip:X16} ({callsPerSec:F0} calls/sec) — INFINITE LOOP SUSPECTED");
                }
                return (true, stats);
            }
        }

        return (false, stats);
    }

    /// <summary>
    /// For unknown NIDs, tries different return values to find one that doesn't loop.
    /// Order: 0 → -1 (0xFFFFFFFF) → 1 → small fake pointer (0x1000)
    /// </summary>
    public ulong GetLearnedReturnValue(string nid)
    {
        // Check if we already learned a working value
        if (_learnedStubs.TryGetValue(nid, out var learned))
        {
            return learned.ReturnValue;
        }

        // Default: return 0
        return 0;
    }

    /// <summary>
    /// Records that a particular return value caused a loop, so next time
    /// we try a different value.
    /// </summary>
    public void RecordLoopFailure(string nid, ulong returnValue)
    {
        var existing = _learnedStubs.GetValueOrDefault(nid);
        var triedValues = existing?.TriedValues ?? new List<ulong>();
        triedValues.Add(returnValue);

        // Pick next value to try
        ulong nextValue = triedValues.Count switch
        {
            0 => 0,              // already tried 0
            1 => unchecked((ulong)-1L),  // try -1
            2 => 1,              // try 1
            3 => 0x1000,         // try fake pointer
            _ => 0x1000          // keep fake pointer
        };

        _learnedStubs[nid] = new LearnedStub(nid, nextValue, triedValues, "learning");
        Console.Error.WriteLine(
            $"[STUB_LEARNING] NID '{nid}': return 0x{returnValue:X16} caused loop. " +
            $"Trying 0x{nextValue:X16} next.");
    }

    public string RenderReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("========== Import Loop Detector ==========");

        var loops = _stats.Where(kvp => kvp.Value.CallCount >= LoopThreshold)
            .OrderByDescending(kvp => kvp.Value.CallCount)
            .Take(10)
            .ToArray();

        if (loops.Length == 0)
        {
            sb.AppendLine("  No loops detected.");
        }
        else
        {
            sb.AppendLine($"  {"NID",-16} {"Calls",-12} {"Caller RIP",-18} {"Calls/sec",-12}");
            sb.AppendLine(new string('-', 70));
            foreach (var kvp in loops)
            {
                var s = kvp.Value;
                var elapsedSec = Math.Max(1, (_timer.ElapsedMilliseconds - s.FirstCallMs) / 1000.0);
                var cps = s.CallCount / elapsedSec;
                sb.AppendLine($"  {s.Nid,-16} {s.CallCount,-12:N0} 0x{s.CallerRip:X16} {cps,-12:F0}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("========== Learned Stubs ==========");
        if (_learnedStubs.IsEmpty)
        {
            sb.AppendLine("  No stubs learned yet.");
        }
        else
        {
            foreach (var kvp in _learnedStubs)
            {
                sb.AppendLine($"  {kvp.Key,-16} return=0x{kvp.Value.ReturnValue:X16} status={kvp.Value.Status} tried={kvp.Value.TriedValues.Count}");
            }
        }

        return sb.ToString();
    }

    public void SaveLearnedStubs(string path)
    {
        try
        {
            var data = _learnedStubs.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }

    public void LoadLearnedStubs(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, LearnedStub>>(json);
            if (data != null)
            {
                foreach (var kvp in data)
                    _learnedStubs[kvp.Key] = kvp.Value;
            }
        }
        catch { }
    }

    public void Reset()
    {
        _stats.Clear();
        _timer.Restart();
    }
}

public readonly record struct LoopStats(
    string Nid,
    ulong CallerRip,
    long CallCount,
    long FirstCallMs);

public record class LearnedStub(
    string Nid,
    ulong ReturnValue,
    List<ulong> TriedValues,
    string Status);
