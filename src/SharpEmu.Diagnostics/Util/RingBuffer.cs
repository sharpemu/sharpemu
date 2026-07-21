// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;

namespace SharpEmu.Diagnostics.Util;

/// <summary>
/// Generic lock-free ring buffer for bounded event collection.
/// Used by CpuTrace, ImportTimeline, and other plugins that need
/// "last N events" semantics without unbounded memory growth.
/// </summary>
public sealed class RingBuffer<T> where T : struct
{
    private readonly T[] _buffer;
    private int _writeIndex;
    private long _totalCount;

    public int Capacity => _buffer.Length;
    public long TotalCount => Interlocked.Read(ref _totalCount);

    public RingBuffer(int capacity)
    {
        _buffer = new T[capacity];
    }

    public void Add(in T item)
    {
        Interlocked.Increment(ref _totalCount);
        var idx = Interlocked.Increment(ref _writeIndex) - 1;
        _buffer[idx % _buffer.Length] = item;
    }

    /// <summary>Returns up to <paramref name="count"/> most recent items in chronological order.</summary>
    public T[] GetRecent(int count)
    {
        var total = (int)TotalCount;
        if (total == 0) return Array.Empty<T>();
        var actual = Math.Min(count, Math.Min(total, _buffer.Length));
        var result = new T[actual];
        var start = (_writeIndex - actual + _buffer.Length * 2) % _buffer.Length;
        for (int i = 0; i < actual; i++)
            result[i] = _buffer[(start + i) % _buffer.Length];
        return result;
    }

    public void Clear()
    {
        _writeIndex = 0;
        Interlocked.Exchange(ref _totalCount, 0);
    }
}
