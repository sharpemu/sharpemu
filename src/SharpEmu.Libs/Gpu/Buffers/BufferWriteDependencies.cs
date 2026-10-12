// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// A conservative latest writer of each byte range. A buffer can hold independently produced
// guest values; reading one must not depend on later writes to the others.
internal sealed class BufferWriteDependencies
{
    // Highly fragmented writers may lose precision, never a required dependency.
    // Bound bookkeeping independently of the size of the guest buffer.
    private const int MaxRanges = 256;
    private readonly record struct Range(ulong Begin, ulong End, ulong Tick);
    private readonly List<Range> _ranges = [];

    public void Record(ulong offset, ulong size, ulong tick)
    {
        if (size == 0) return;
        var end = checked(offset + size);
        var first = FindFirst(offset);
        var last = first;
        while (last < _ranges.Count && _ranges[last].Begin < end) last++;

        Range? left = first < last && _ranges[first].Begin < offset
            ? new Range(_ranges[first].Begin, offset, _ranges[first].Tick) : null;
        Range? right = first < last && _ranges[last - 1].End > end
            ? new Range(end, _ranges[last - 1].End, _ranges[last - 1].Tick) : null;
        _ranges.RemoveRange(first, last - first);
        if (left is { } before) _ranges.Insert(first++, before);
        _ranges.Insert(first, new Range(offset, end, tick));
        if (right is { } after) _ranges.Insert(first + 1, after);

        if (first > 0 && _ranges[first - 1].End == offset && _ranges[first - 1].Tick == tick)
        {
            _ranges[first - 1] = new Range(_ranges[first - 1].Begin, end, tick);
            _ranges.RemoveAt(first--);
        }
        if (first + 1 < _ranges.Count && _ranges[first].End == _ranges[first + 1].Begin &&
            _ranges[first + 1].Tick == tick)
        {
            _ranges[first] = new Range(_ranges[first].Begin, _ranges[first + 1].End, tick);
            _ranges.RemoveAt(first + 1);
        }
        if (_ranges.Count > MaxRanges)
        {
            var latest = 0UL;
            foreach (var range in _ranges) latest = Math.Max(latest, range.Tick);
            _ranges.Clear();
            _ranges.Add(new Range(0, ulong.MaxValue, latest));
        }
    }

    public ulong Latest(ulong offset, ulong size)
    {
        if (size == 0) return 0;
        var end = checked(offset + size);
        var tick = 0UL;
        for (var index = FindFirst(offset); index < _ranges.Count && _ranges[index].Begin < end; index++)
            tick = Math.Max(tick, _ranges[index].Tick);
        return tick;
    }

    private int FindFirst(ulong offset)
    {
        var first = 0;
        var last = _ranges.Count;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (_ranges[middle].End <= offset) first = middle + 1;
            else last = middle;
        }
        return first;
    }
}
