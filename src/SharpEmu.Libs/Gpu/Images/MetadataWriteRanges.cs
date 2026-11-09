// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

// The image lock serializes writers. Readers only inspect immutable snapshots.
internal sealed class MetadataWriteRanges
{
    private readonly record struct Interval(ulong First, ulong Last);
    private Interval[] _ranges = [];

    public void Include(ulong address, ulong size)
    {
        var last = address + Math.Min(Math.Max(1UL, size) - 1, ulong.MaxValue - address);
        var previous = _ranges;
        var first = 0;
        while (first < previous.Length && previous[first].Last < address && address - previous[first].Last > 1)
            first++;

        if (first < previous.Length && previous[first].First <= address && previous[first].Last >= last)
            return;

        var end = first;
        while (end < previous.Length && (previous[end].First <= last || previous[end].First - last == 1))
        {
            address = Math.Min(address, previous[end].First);
            last = Math.Max(last, previous[end].Last);
            end++;
        }

        var next = new Interval[previous.Length - (end - first) + 1];
        Array.Copy(previous, 0, next, 0, first);
        next[first] = new Interval(address, last);
        Array.Copy(previous, end, next, first + 1, previous.Length - end);
        // Keep prior coverage after removal so an old registration cannot hide a write.
        System.Threading.Volatile.Write(ref _ranges, next);
    }

    public bool Overlaps(ulong address, ulong size)
    {
        if (size == 0) return false;
        var last = address + Math.Min(size - 1, ulong.MaxValue - address);
        var ranges = System.Threading.Volatile.Read(ref _ranges);
        var low = First(ranges, address);
        return low < ranges.Length && ranges[low].First <= last;
    }

    public bool Contains(ulong address, ulong size)
    {
        var last = address + Math.Min(Math.Max(1UL, size) - 1, ulong.MaxValue - address);
        var ranges = System.Threading.Volatile.Read(ref _ranges);
        var low = First(ranges, address);
        return low < ranges.Length && ranges[low].First <= address && ranges[low].Last >= last;
    }

    private static int First(Interval[] ranges, ulong address)
    {
        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (ranges[middle].Last < address) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
