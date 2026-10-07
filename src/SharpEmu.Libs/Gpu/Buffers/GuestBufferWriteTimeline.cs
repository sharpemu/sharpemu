// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// Queue-thread-only byte ranges. Buffer allocation boundaries are not dependencies:
// unrelated writes can share both a cache allocation and a host page.
internal sealed class GuestBufferWriteTimeline
{
    private readonly record struct Write(ulong Begin, ulong End, ulong Tick, ulong? Value = null);
    private readonly List<Write> _writes = [];

    public void RetireThrough(ulong tick) => _writes.RemoveAll(write => write.Tick <= tick);

    public void Record(ulong address, ulong size, ulong tick) => Record(address, size, tick, null);

    public void RecordSignal(ulong address, ulong size, ulong tick, ulong value)
    {
        if (size is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(size));
        Record(address, size, tick, value);
    }

    // Only predict a write that has not been submitted. Once submitted, the guest
    // CPU could already have observed and reset it, so its live value must be read.
    public bool TryReadSignal(ulong address, ulong size, ulong recordingTick, out ulong value)
    {
        value = 0;
        var index = Find(address);
        if (size is not (4 or 8) || index == _writes.Count) return false;
        var write = _writes[index];
        if (write.Begin != address || write.End - address < size || write.Tick != recordingTick ||
            write.Value is not { } known) return false;
        value = size == 4 ? (uint)known : known;
        return true;
    }

    private void Record(ulong address, ulong size, ulong tick, ulong? value)
    {
        if (size == 0 || address > ulong.MaxValue - size) throw new ArgumentOutOfRangeException(nameof(size));
        var end = address + size;
        var first = Find(address);
        // Repeated draws commonly bind the same writable resource within one tick.
        if (first < _writes.Count && _writes[first].Begin <= address &&
            _writes[first].End >= end && _writes[first].Tick == tick &&
            value == null && _writes[first].Value == null) return;
        var last = first;
        while (last < _writes.Count && _writes[last].Begin < end) last++;
        Write? left = first < last && _writes[first].Begin < address ? _writes[first] with { End = address, Value = null } : null;
        Write? right = first < last && _writes[last - 1].End > end ? _writes[last - 1] with { Begin = end, Value = null } : null;
        _writes.RemoveRange(first, last - first);
        if (left is { } prefix) _writes.Insert(first++, prefix);
        if (tick != 0)
        {
            if (value == null && first > 0 && _writes[first - 1].End == address &&
                _writes[first - 1].Tick == tick && _writes[first - 1].Value == null)
            {
                address = _writes[--first].Begin;
                _writes.RemoveAt(first);
            }
            if (value == null && right is null && first < _writes.Count && _writes[first].Begin == end &&
                _writes[first].Tick == tick && _writes[first].Value == null)
            {
                end = _writes[first].End;
                _writes.RemoveAt(first);
            }
            _writes.Insert(first++, new Write(address, end, tick, value));
        }
        if (right is { } suffix) _writes.Insert(first, suffix);
    }

    public ulong LastWriter(ulong address, ulong size)
    {
        if (size == 0 || address > ulong.MaxValue - size) return 0;
        ulong tick = 0;
        var end = address + size;
        for (var i = Find(address); i < _writes.Count && _writes[i].Begin < end; i++)
            tick = Math.Max(tick, _writes[i].Tick);
        return tick;
    }

    private int Find(ulong address)
    {
        var low = 0;
        var high = _writes.Count;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (_writes[mid].End <= address) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
