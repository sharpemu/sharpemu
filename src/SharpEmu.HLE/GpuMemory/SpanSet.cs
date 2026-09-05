// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

public sealed class SpanSet
{
    private readonly SortedList<ulong, ulong> _spans = new();

    public bool IsEmpty => _spans.Count == 0;

    public void Add(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var start = address;
        var end = address + size;

        var index = UpperBound(start);
        if (index > 0 && _spans.GetValueAtIndex(index - 1) >= start)
        {
            index--;
        }

        while (index < _spans.Count && _spans.GetKeyAtIndex(index) <= end)
        {
            start = Math.Min(start, _spans.GetKeyAtIndex(index));
            end = Math.Max(end, _spans.GetValueAtIndex(index));
            _spans.RemoveAt(index);
        }

        _spans[start] = end;
    }

    public void Remove(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var start = address;
        var end = address + size;

        var index = UpperBound(start);
        if (index > 0 && _spans.GetValueAtIndex(index - 1) > start)
        {
            index--;
        }

        while (index < _spans.Count && _spans.GetKeyAtIndex(index) < end)
        {
            var spanStart = _spans.GetKeyAtIndex(index);
            var spanEnd = _spans.GetValueAtIndex(index);
            _spans.RemoveAt(index);

            if (spanStart < start)
            {
                _spans[spanStart] = start;
                index++;
            }

            if (spanEnd > end)
            {
                _spans[end] = spanEnd;
                index++;
            }
        }
    }

    public void Clear() => _spans.Clear();

    public bool Contains(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var index = UpperBound(address);
        return index > 0 && _spans.GetValueAtIndex(index - 1) >= address + size;
    }

    public bool Overlaps(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var index = LowerBound(address + size);
        return index > 0 && _spans.GetValueAtIndex(index - 1) > address;
    }

    public void ForEach(Action<ulong, ulong> visit)
    {
        foreach (var (start, end) in _spans)
        {
            visit(start, end - start);
        }
    }

    public List<GuestSpan> GetOverlappingRanges(ulong address, ulong size)
    {
        ValidateRange(address, size);
        var result = new List<GuestSpan>();
        var end = address + size;
        var index = UpperBound(address);
        if (index > 0 && _spans.GetValueAtIndex(index - 1) > address)
        {
            index--;
        }

        for (; index < _spans.Count && _spans.GetKeyAtIndex(index) < end; index++)
        {
            var start = Math.Max(address, _spans.GetKeyAtIndex(index));
            var stop = Math.Min(end, _spans.GetValueAtIndex(index));
            result.Add(new GuestSpan(start, stop - start));
        }

        return result;
    }

    private int LowerBound(ulong key)
    {
        var low = 0;
        var high = _spans.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_spans.GetKeyAtIndex(mid) < key)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int UpperBound(ulong key)
    {
        var low = 0;
        var high = _spans.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_spans.GetKeyAtIndex(mid) <= key)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static void ValidateRange(ulong address, ulong size)
    {
        if (size == 0 || address + size < address)
        {
            Environment.FailFast($"The address range is invalid: 0x{address:X16}, 0x{size:X16}.");
        }
    }
}
