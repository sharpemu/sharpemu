// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Tracks the FIRST failure (bad return value) and the LAST N successful HLE calls.
/// "First Failure Wins" — 90% of subsequent crashes are downstream of the first error.
///
/// Output format:
///   Last successful calls:
///     #1638 OK sceKernelMapDirectMemory
///     #1639 OK scePthreadSelf
///     #1640 OK sceKernelAllocateDirectMemory
///   First failure:
///     #1641 FAIL sceAgcDriverRegisterOwner returned INVALID_ARGUMENT
///   (all subsequent crashes are likely downstream of this)
/// </summary>
public sealed class ReturnAnalyzer
{
    private const int DefaultWindowSize = 100;

    private readonly CallRecord[] _recentCalls;
    private int _writeIndex;
    private int _count;
    private readonly object _gate = new();

    private CallRecord? _firstFailure;
    private long _totalCalls;
    private long _totalSuccess;
    private long _totalFailures;
    private readonly ConcurrentDictionary<string, long> _successByFunction = new();
    private readonly ConcurrentDictionary<string, long> _failureByFunction = new();

    private static readonly Lazy<ReturnAnalyzer> _instance =
        new(() => new ReturnAnalyzer(DefaultWindowSize));

    public static ReturnAnalyzer Instance => _instance.Value;

    public ReturnAnalyzer(int windowSize)
    {
        _recentCalls = new CallRecord[windowSize];
    }

    public void RecordSuccess(long sequence, string nid, string? functionName, ulong returnRip)
    {
        var record = new CallRecord(sequence, nid, functionName, "OK", null, returnRip, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _recentCalls[_writeIndex] = record;
            _writeIndex = (_writeIndex + 1) % _recentCalls.Length;
            if (_count < _recentCalls.Length) _count++;
        }
        _totalCalls++;
        _totalSuccess++;
        if (functionName != null)
            _successByFunction.AddOrUpdate(functionName, 1, (_, v) => v + 1);
    }

    public void RecordFailure(long sequence, string nid, string? functionName, string errorCode, ulong returnRip)
    {
        var record = new CallRecord(sequence, nid, functionName, "FAIL", errorCode, returnRip, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _recentCalls[_writeIndex] = record;
            _writeIndex = (_writeIndex + 1) % _recentCalls.Length;
            if (_count < _recentCalls.Length) _count++;
        }
        _totalCalls++;
        _totalFailures++;
        if (functionName != null)
            _failureByFunction.AddOrUpdate(functionName, 1, (_, v) => v + 1);

        // First failure wins
        if (_firstFailure == null)
        {
            _firstFailure = record;
            Console.Error.WriteLine(
                $"[FIRST_FAILURE] #{sequence} {functionName ?? nid} returned {errorCode} " +
                $"(after {_totalSuccess} successful calls) — this is likely the root cause");
        }
    }

    public CallRecord? GetFirstFailure() => _firstFailure;

    public IReadOnlyList<CallRecord> GetRecentCalls(int count)
    {
        lock (_gate)
        {
            if (_count == 0) return Array.Empty<CallRecord>();
            var take = Math.Min(count, _count);
            var result = new CallRecord[take];
            var start = (_writeIndex - take + _recentCalls.Length) % _recentCalls.Length;
            for (var i = 0; i < take; i++)
                result[i] = _recentCalls[(start + i) % _recentCalls.Length];
            return result;
        }
    }

    public string RenderReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========== Return Value Analyzer ==========");
        sb.AppendLine($"Total calls: {_totalCalls} (success={_totalSuccess} failures={_totalFailures})");
        sb.AppendLine();

        // First failure
        if (_firstFailure.HasValue)
        {
            var f = _firstFailure.Value;
            sb.AppendLine("*** FIRST FAILURE (root cause candidate) ***");
            sb.AppendLine($"  #{f.Sequence} {f.FunctionName ?? f.Nid}");
            sb.AppendLine($"  Returned: {f.ErrorCode}");
            sb.AppendLine($"  Caller RIP: 0x{f.ReturnRip:X16}");
            sb.AppendLine($"  After {f.Sequence - 1} prior calls");
            sb.AppendLine();
        }

        // Last N calls
        var recent = GetRecentCalls(30);
        sb.AppendLine($"Last {recent.Count} calls:");
        foreach (var c in recent)
        {
            var status = c.Status == "OK" ? "OK  " : "FAIL";
            var err = c.ErrorCode != null ? $" -> {c.ErrorCode}" : "";
            sb.AppendLine($"  #{c.Sequence,-6} {status} {c.FunctionName ?? c.Nid}{err}");
        }
        sb.AppendLine();

        // Most-called functions (loop detector)
        sb.AppendLine("Top 10 most-called functions (loop detector):");
        var top = _successByFunction.Concat(_failureByFunction)
            .GroupBy(kvp => kvp.Key)
            .Select(g => (Function: g.Key, Count: g.Sum(k => k.Value)))
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToArray();
        foreach (var (fn, count) in top)
        {
            var failures = _failureByFunction.TryGetValue(fn, out var f) ? f : 0;
            var marker = count > 5000 ? " *** LOOP SUSPECT" : "";
            sb.AppendLine($"  {count,-8} {fn,-40} (failures: {failures}){marker}");
        }

        return sb.ToString();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _writeIndex = 0;
            _count = 0;
        }
        _firstFailure = null;
        _totalCalls = 0;
        _totalSuccess = 0;
        _totalFailures = 0;
        _successByFunction.Clear();
        _failureByFunction.Clear();
    }
}

public readonly record struct CallRecord(
    long Sequence,
    string Nid,
    string? FunctionName,
    string Status,
    string? ErrorCode,
    ulong ReturnRip,
    DateTimeOffset Timestamp);
