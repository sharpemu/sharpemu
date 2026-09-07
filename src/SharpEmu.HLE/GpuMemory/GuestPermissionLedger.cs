// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// The guest's own page permissions, written only by map and mprotect events.
public sealed class GuestPermissionLedger
{
    private readonly object _gate = new();
    private readonly SortedList<ulong, (ulong End, GuestPageProtection Protection)> _spans = new();

    public void Set(ulong address, ulong size, GuestPageProtection protection)
    {
        lock (_gate)
        {
            RemoveRangeRecords(address, address + size);
            _spans[address] = (address + size, protection);
        }
    }

    public void Clear(ulong address, ulong size)
    {
        lock (_gate)
        {
            RemoveRangeRecords(address, address + size);
        }
    }

    // A span without a record is read-write: every caller is gated by a registered span first.
    public GuestPageProtection Lookup(ulong address)
    {
        lock (_gate)
        {
            var index = FindFirstStartAfter(address) - 1;
            return index >= 0 && _spans.Values[index].End > address
                ? _spans.Values[index].Protection
                : GuestPageProtection.Read | GuestPageProtection.Write;
        }
    }

    private void RemoveRangeRecords(ulong start, ulong end)
    {
        var index = FindFirstStartAfter(start);
        if (index > 0 && _spans.Values[index - 1].End > start)
        {
            index--;
        }

        while (index < _spans.Count && _spans.Keys[index] < end)
        {
            var spanStart = _spans.Keys[index];
            var (spanEnd, protection) = _spans.Values[index];
            _spans.RemoveAt(index);
            if (spanStart < start)
            {
                _spans[spanStart] = (start, protection);
                index++;
            }

            if (spanEnd > end)
            {
                _spans[end] = (spanEnd, protection);
                index++;
            }
        }
    }

    private int FindFirstStartAfter(ulong key)
    {
        var low = 0;
        var high = _spans.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_spans.Keys[mid] <= key)
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
}
