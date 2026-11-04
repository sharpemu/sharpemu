// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Scheduling;

// Accumulates how long the host GPU was busy, from the timestamp intervals of the submitted command buffers.
// Time the GPU idled while the emulator recorded, and time covered by two buffers at once, do not count: the
// result is the length of the union of the intervals, taken in submission order. Pure accounting, no Vulkan.
internal sealed class GpuBusyLedger
{
    internal const int Capacity = 4096;
    internal const int StampCapacity = 16384;

    private readonly (ulong Tick, ulong Busy)[] _ring = new (ulong, ulong)[Capacity];
    private readonly Dictionary<ulong, ulong> _stamps = new();
    private readonly Queue<ulong> _stampOrder = new();
    private int _count;
    private int _next;
    private ulong _lastEnd;

    public ulong TotalBusy { get; private set; }

    // Intervals are in GPU timestamp ticks, already masked to the valid bits; a buffer whose values were not
    // available is simply left out by the caller.
    public void AddSubmission(ulong tick, ReadOnlySpan<(ulong Begin, ulong End)> bufferIntervals)
    {
        foreach (var (begin, end) in bufferIntervals)
        {
            AddBuffer(begin, end, default);
        }

        CompleteSubmission(tick);
    }

    // Adds one buffer's interval and resolves the stamps written inside it. A stamp is worth the busy time
    // before the buffer plus the part of the buffer's own contribution that lies before the stamp: time the GPU
    // spent earlier on an overlapping buffer is not the stamp's to claim, and a stamp cannot exceed its buffer.
    public void AddBuffer(ulong begin, ulong end, ReadOnlySpan<(ulong Id, ulong Timestamp)> stamps)
    {
        var busyBefore = TotalBusy;
        var lastEndBefore = _lastEnd;
        var start = Math.Max(begin, lastEndBefore);
        var contribution = end > start ? end - start : 0;
        TotalBusy += contribution;
        _lastEnd = Math.Max(lastEndBefore, end);

        foreach (var (id, timestamp) in stamps)
        {
            var elapsed = timestamp > start ? timestamp - start : 0;
            StoreStamp(id, busyBefore + Math.Min(elapsed, contribution));
        }
    }

    // Records the cumulative busy time at the end of the submission signalled with this tick.
    public void CompleteSubmission(ulong tick)
    {
        _ring[_next] = (tick, TotalBusy);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    public bool TryStampBusy(ulong id, out ulong busyTicks) => _stamps.TryGetValue(id, out busyTicks);

    private void StoreStamp(ulong id, ulong busy)
    {
        if (_stamps.Count >= StampCapacity)
        {
            _stamps.Remove(_stampOrder.Dequeue());
        }

        _stamps[id] = busy;
        _stampOrder.Enqueue(id);
    }

    // A tick held back by the scheduler is folded into a later submission signalled with a larger tick, so the
    // first stored submission at or above the requested tick is where that work finished.
    public bool TryBusyAt(ulong tick, out ulong busyTicks)
    {
        var first = (_next - _count + Capacity) % Capacity;
        for (var index = 0; index < _count; index++)
        {
            var entry = _ring[(first + index) % Capacity];
            if (entry.Tick >= tick)
            {
                busyTicks = entry.Busy;
                return true;
            }
        }

        busyTicks = 0;
        return false;
    }
}
