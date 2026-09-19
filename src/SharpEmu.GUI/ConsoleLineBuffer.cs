// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.GUI;

/// <summary>
/// The hand-off between the threads that read the emulator's output and the UI timer that renders
/// it.
///
/// The window drains a fixed number of lines per tick, so a title that logs faster than that
/// outruns the display. Held in an unbounded queue the backlog is retained in full, even though
/// the two lists it eventually feeds are both capped - which is how a title polling an unresolved
/// import in a tight loop turns console output into tens of gigabytes of process memory and
/// eventually an out-of-memory kill.
///
/// This bounds the backlog instead. Past capacity the oldest lines are dropped, because the newest
/// output is the part worth keeping when a title is stuck, and the number dropped is counted so
/// the gap can be shown rather than silently hidden.
/// </summary>
public sealed class ConsoleLineBuffer
{
    private readonly ConcurrentQueue<(string Line, bool IsError)> _lines = new();
    private int _count;
    private long _dropped;

    public ConsoleLineBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>Lines currently queued. Never exceeds <see cref="Capacity"/>.</summary>
    public int Count => Volatile.Read(ref _count);

    public bool IsEmpty => Count == 0;

    public void Enqueue(string line, bool isError)
    {
        _lines.Enqueue((line, isError));
        if (Interlocked.Increment(ref _count) <= Capacity)
        {
            return;
        }

        // Over capacity: make room by discarding from the front. Only account for a dequeue that
        // actually removed something - the UI may have drained this line already, and in that
        // case it has done the decrement itself.
        if (_lines.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _dropped);
        }
    }

    public bool TryDequeue(out string line, out bool isError)
    {
        if (!_lines.TryDequeue(out var entry))
        {
            line = string.Empty;
            isError = false;
            return false;
        }

        Interlocked.Decrement(ref _count);
        line = entry.Line;
        isError = entry.IsError;
        return true;
    }

    /// <summary>
    /// Returns how many lines have been dropped since the last call and resets the tally, so a
    /// caller reports each drop exactly once.
    /// </summary>
    public long ExchangeDroppedCount() => Interlocked.Exchange(ref _dropped, 0);
}
