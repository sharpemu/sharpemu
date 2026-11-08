// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Diagnostics;

public static class ThreadCounters
{
    public const int Capacity = 4096;

    [ThreadStatic]
    private static long[]? _local;

    private static readonly List<long[]> _threads = [];
    private static int _registered;

    internal static int Register()
    {
        var id = Interlocked.Increment(ref _registered) - 1;
        return id < Capacity ? id : throw new InvalidOperationException("Too many thread counters are registered.");
    }

    internal static void Add(int id, long value)
    {
        var local = _local ?? Attach();
        Volatile.Write(ref local[id], local[id] + value);
    }

    internal static long Sum(int id)
    {
        var total = 0L;
        lock (_threads)
        {
            foreach (var counts in _threads)
            {
                total += Volatile.Read(ref counts[id]);
            }
        }

        return total;
    }

    private static long[] Attach()
    {
        var counts = new long[Capacity];
        lock (_threads)
        {
            _threads.Add(counts);
        }

        _local = counts;
        return counts;
    }
}

public sealed class ThreadCounter
{
    private readonly int _id = ThreadCounters.Register();
    private long _taken;

    public void Increment() => ThreadCounters.Add(_id, 1);

    public void Add(long value) => ThreadCounters.Add(_id, value);

    public long Total => ThreadCounters.Sum(_id);

    public long Take()
    {
        var total = ThreadCounters.Sum(_id);
        return total - Interlocked.Exchange(ref _taken, total);
    }
}
