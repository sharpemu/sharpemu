// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.Buffers;

/// <summary>
/// SHARPEMU_LOG_CPU_WRITE_READBACKS=1: a CPU write into GPU-dirty guest memory stalls the writing guest
/// thread on a GPU readback. Every five seconds this reports which 64 KiB regions caused those stalls and
/// how many writes and bytes each took, so the writer can be identified.
/// </summary>
internal static class CpuWriteReadbackReport
{
    private const int WindowSeconds = 5;
    private const ulong RegionShift = 16;

    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_CPU_WRITE_READBACKS"), "1", StringComparison.Ordinal);

    private static readonly ConcurrentDictionary<ulong, (long Writes, long Bytes, ulong MinAddress, ulong MaxEnd)> _regions = new();
    private static readonly ConcurrentDictionary<ulong, (long Writes, long Bytes, long MaxSize, string Name, ulong SampleAddress)> _callers = new();
    private static long _windowStart = Stopwatch.GetTimestamp();
    private static int _reporting;

    internal static bool Active => Enabled;

    private static long _discards;

    internal static void RecordDiscard()
    {
        if (Enabled)
        {
            Interlocked.Increment(ref _discards);
        }
    }

    // How the bytes the GPU modified inside the written pages relate to the CPU write:
    // 0 no GPU span, 1 every span inside the write, 2 spans only outside it, 3 both.
    private static readonly long[] _shapeCount = new long[4];
    private static readonly long[] _shapeGpuBytes = new long[4];

    internal static void RecordShape(int shape, long gpuBytes)
    {
        Interlocked.Increment(ref _shapeCount[shape]);
        Interlocked.Add(ref _shapeGpuBytes[shape], gpuBytes);
    }

    internal static void Record(ulong address, ulong size)
    {
        if (!Enabled)
        {
            return;
        }

        _regions.AddOrUpdate(
            address >> (int)RegionShift,
            _ => (1, (long)size, address, address + size),
            (_, current) => (current.Writes + 1, current.Bytes + (long)size,
                Math.Min(current.MinAddress, address), Math.Max(current.MaxEnd, address + size)));

        var callerRip = SharpEmu.HLE.Diagnostics.GuestImportCallSite.ReturnAddress;
        _callers.AddOrUpdate(
            callerRip,
            _ => (1, (long)size, (long)size, SharpEmu.HLE.Diagnostics.GuestImportCallSite.Name ?? "(guest write)", address),
            (_, current) => (current.Writes + 1, current.Bytes + (long)size, Math.Max(current.MaxSize, (long)size), current.Name, current.SampleAddress));

        var now = Stopwatch.GetTimestamp();
        var started = Interlocked.Read(ref _windowStart);
        if (now - started < Stopwatch.Frequency * WindowSeconds || Interlocked.Exchange(ref _reporting, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _windowStart, now);
            var snapshot = _regions.ToArray();
            _regions.Clear();
            var shapes = string.Join(",", Enumerable.Range(0, 4).Select(i =>
                $"{new[] { "no_span", "inside", "outside", "both" }[i]}={Interlocked.Exchange(ref _shapeCount[i], 0)}/{Interlocked.Exchange(ref _shapeGpuBytes[i], 0)}B"));
            Console.Error.WriteLine($"[PERF][CPU_WRITE_READBACK_SHAPE] {shapes} discarded_without_readback={Interlocked.Exchange(ref _discards, 0)}");
            var callerSnapshot = _callers.ToArray();
            _callers.Clear();
            var seconds = (now - started) / (double)Stopwatch.Frequency;
            var writes = snapshot.Sum(pair => pair.Value.Writes);
            var text = string.Join(" | ", snapshot
                .OrderByDescending(pair => pair.Value.Writes)
                .Take(6)
                .Select(pair => $"0x{pair.Value.MinAddress:X}-0x{pair.Value.MaxEnd:X}=n{pair.Value.Writes}/{pair.Value.Bytes}B"));
            Console.Error.WriteLine(
                $"[PERF][CPU_WRITE_READBACK] window={seconds:F1}s writes={writes} regions={snapshot.Length} {text}");
            Console.Error.WriteLine("[PERF][CPU_WRITE_READBACK_CALLERS] " + string.Join(" | ", callerSnapshot
                .OrderByDescending(pair => pair.Value.Writes)
                .Take(8)
                .Select(pair => $"{pair.Value.Name}@0x{pair.Key:X}=n{pair.Value.Writes}/{pair.Value.Bytes}B max{pair.Value.MaxSize} e.g.0x{pair.Value.SampleAddress:X}")));
        }
        finally
        {
            Volatile.Write(ref _reporting, 0);
        }
    }
}
