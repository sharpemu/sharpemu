// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Diagnostics;

/// <summary>
/// Tracks where pointer values originate. When a crash happens at address X,
/// instead of guessing, we query: "who first produced this address?"
///
/// Every HLE export that returns a pointer value is recorded. When the crash
/// instruction loads RAX from memory, we can trace back: which HLE call
/// returned the value that eventually ended up in RAX?
///
/// Usage:
///   PointerOriginTracker.Instance.RecordReturn("sceKernelMapDirectMemory",
///       returnValue, callerRip, args);
///   ...
///   var origin = PointerOriginTracker.Instance.FindOrigin(0x1FE000000);
///   // origin = "sceKernelMapDirectMemory returned 0x1FE000000 at import #1640"
/// </summary>
public sealed class PointerOriginTracker
{
    // Key = pointer value, Value = who produced it
    private readonly ConcurrentDictionary<ulong, PointerOrigin> _originsByValue = new();
    // Also track by address range (for GPU addresses that might be offset)
    private readonly ConcurrentQueue<PointerOrigin> _recentOrigins = new();
    private const int MaxRecentOrigins = 5000;

    private static readonly Lazy<PointerOriginTracker> _instance =
        new(() => new PointerOriginTracker());

    public static PointerOriginTracker Instance => _instance.Value;

    /// <summary>
    /// Records that an HLE function returned a pointer value.
    /// Only records non-zero values that look like addresses.
    /// </summary>
    public void RecordReturn(string functionName, ulong returnValue, ulong callerRip,
        ulong arg0 = 0, ulong arg1 = 0, ulong arg2 = 0, long importSequence = 0)
    {
        if (returnValue == 0) return;
        if (!LooksLikeAddress(returnValue)) return;

        var origin = new PointerOrigin(
            FunctionName: functionName,
            Value: returnValue,
            CallerRip: callerRip,
            Arg0: arg0,
            Arg1: arg1,
            Arg2: arg2,
            ImportSequence: importSequence,
            Timestamp: DateTimeOffset.UtcNow,
            RegionKind: MemoryRegionClassifier.Classify(returnValue));

        _originsByValue[returnValue] = origin;
        _recentOrigins.Enqueue(origin);
        while (_recentOrigins.Count > MaxRecentOrigins)
        {
            _recentOrigins.TryDequeue(out _);
        }

        // Special alert for GPU addresses
        if (origin.RegionKind == MemoryRegionClassifier.RegionKind.GpuMemory)
        {
            Console.Error.WriteLine(
                $"[POINTER_ORIGIN][GPU] {functionName} returned GPU address 0x{returnValue:X16} " +
                $"(caller=0x{callerRip:X16} import#{importSequence})");
        }

        // Special alert for host pointer leaks
        if (origin.RegionKind == MemoryRegionClassifier.RegionKind.HostLeak)
        {
            Console.Error.WriteLine(
                $"[POINTER_ORIGIN][HOST_LEAK] {functionName} returned HOST address 0x{returnValue:X16} " +
                $"(caller=0x{callerRip:X16} import#{importSequence})");
        }
    }

    /// <summary>
    /// Finds who produced a specific pointer value. Returns null if not tracked.
    /// </summary>
    public PointerOrigin? FindOrigin(ulong value)
    {
        return _originsByValue.TryGetValue(value, out var origin) ? origin : null;
    }

    /// <summary>
    /// Finds who produced any pointer in the given range (for partial matches).
    /// </summary>
    public PointerOrigin? FindOriginInRange(ulong value, ulong range = 0x1000)
    {
        // Exact match first
        if (_originsByValue.TryGetValue(value, out var exact))
            return exact;

        // Range search
        foreach (var kvp in _originsByValue)
        {
            if (value >= kvp.Key && value - kvp.Key < range)
                return kvp.Value;
        }
        return null;
    }

    /// <summary>
    /// Returns all GPU-range pointers that were returned by HLE functions.
    /// </summary>
    public IReadOnlyList<PointerOrigin> GetGpuPointers()
    {
        return _recentOrigins
            .Where(o => o.RegionKind == MemoryRegionClassifier.RegionKind.GpuMemory)
            .ToList();
    }

    /// <summary>
    /// Returns all host-range pointers (leaks) returned by HLE functions.
    /// </summary>
    public IReadOnlyList<PointerOrigin> GetHostPointers()
    {
        return _recentOrigins
            .Where(o => o.RegionKind == MemoryRegionClassifier.RegionKind.HostLeak)
            .ToList();
    }

    private static bool LooksLikeAddress(ulong value)
    {
        // Must be non-zero and in a plausible address range
        return value >= 0x10000 && value < 0x800000000000UL;
    }

    public string RenderReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("========== Pointer Origin Tracker ==========");

        var gpu = GetGpuPointers();
        var host = GetHostPointers();

        sb.AppendLine($"GPU-range pointers returned by HLE: {gpu.Count}");
        foreach (var p in gpu.Take(20))
        {
            sb.AppendLine($"  0x{p.Value:X16} from {p.FunctionName} (import#{p.ImportSequence} caller=0x{p.CallerRip:X16})");
        }

        sb.AppendLine();
        sb.AppendLine($"HOST-range pointers (leaks): {host.Count}");
        foreach (var p in host.Take(20))
        {
            sb.AppendLine($"  0x{p.Value:X16} from {p.FunctionName} (import#{p.ImportSequence} caller=0x{p.CallerRip:X16})");
        }

        return sb.ToString();
    }

    public void Reset()
    {
        _originsByValue.Clear();
        while (_recentOrigins.TryDequeue(out _)) { }
    }
}

public readonly record struct PointerOrigin(
    string FunctionName,
    ulong Value,
    ulong CallerRip,
    ulong Arg0,
    ulong Arg1,
    ulong Arg2,
    long ImportSequence,
    DateTimeOffset Timestamp,
    MemoryRegionClassifier.RegionKind RegionKind);
