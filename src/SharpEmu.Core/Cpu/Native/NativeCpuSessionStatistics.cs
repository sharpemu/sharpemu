// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Core.Cpu.Native;

internal readonly record struct NativeCpuSessionStatistics(int ImportsHit, int UniqueNidsHit);

internal interface INativeCpuSessionStatisticsProvider
{
    NativeCpuSessionStatistics LastSessionStatistics { get; }
}

internal sealed class NativeImportSessionCounters
{
    private readonly ConcurrentDictionary<string, byte> _uniqueNids = new(StringComparer.Ordinal);
    private int _importsHit;

    public void Record(string nid)
    {
        Interlocked.Increment(ref _importsHit);
        _uniqueNids.TryAdd(nid, 0);
    }

    public NativeCpuSessionStatistics Snapshot() =>
        new(Volatile.Read(ref _importsHit), _uniqueNids.Count);
}
