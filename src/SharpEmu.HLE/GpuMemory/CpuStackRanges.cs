// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// Guest memory a thread runs on as its stack, such as a fiber's context, in whole pages. Windows writes an exception's
// record below the stack pointer before any handler runs, so a page watch there leaves the thread's next fault
// undeliverable, and Windows ends the process: page watches take no access from these pages (PageGuard.ApplyAccess).
public sealed class CpuStackRanges
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, (ulong Start, ulong End)> _owners = [];
    private (ulong Start, ulong End)[] _ranges = [];

    // Sorted and merged; read without a lock, also while a fault is resolved.
    public (ulong Start, ulong End)[] Snapshot => Volatile.Read(ref _ranges);

    // False when the owner already has this range. previous is the owner's earlier range.
    public bool Set(ulong owner, ulong start, ulong end, out (ulong Start, ulong End)? previous)
    {
        lock (_gate)
        {
            previous = null;
            if (_owners.TryGetValue(owner, out var existing))
            {
                if (existing == (start, end))
                {
                    return false;
                }

                previous = existing;
            }

            _owners[owner] = (start, end);
            Publish();
            return true;
        }
    }

    public bool Remove(ulong owner, out (ulong Start, ulong End) range)
    {
        lock (_gate)
        {
            if (!_owners.Remove(owner, out range))
            {
                return false;
            }

            Publish();
            return true;
        }
    }

    // The index of the first range that ends above the address.
    public static int FirstEndingAfter((ulong Start, ulong End)[] ranges, ulong address)
    {
        int low = 0, high = ranges.Length;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (ranges[middle].End <= address)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private void Publish()
    {
        var merged = new List<(ulong Start, ulong End)>(_owners.Count);
        foreach (var range in _owners.Values.OrderBy(static range => range.Start))
        {
            if (merged.Count != 0 && range.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        Volatile.Write(ref _ranges, [.. merged]);
    }
}
