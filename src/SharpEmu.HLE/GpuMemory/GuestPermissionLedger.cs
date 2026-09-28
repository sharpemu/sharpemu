// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// The guest's own page permissions, written only by map and mprotect events.
public sealed class GuestPermissionLedger
{
    private readonly object _gate = new();
    private readonly PermissionRangeTree _spans = new();

    public void Set(ulong address, ulong size, GuestPageProtection protection)
    {
        lock (_gate)
        {
            RemoveRangeRecords(address, address + size);
            _spans[address] = new PermissionRange(address, size, protection);
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
            return _spans.FindAtOrBelow(address) is { } span && span.Address + span.Size > address
                ? span.Protection
                : GuestPageProtection.Read | GuestPageProtection.Write;
        }
    }

    private void RemoveRangeRecords(ulong start, ulong end)
    {
        var candidate = _spans.FindAtOrBelow(start);
        if (candidate is not { } preceding || preceding.Address + preceding.Size <= start)
            candidate = start == ulong.MaxValue ? null : _spans.FindAtOrAbove(start + 1);
        while (candidate is { } span && span.Address < end)
        {
            var spanStart = span.Address;
            var spanEnd = span.Address + span.Size;
            var protection = span.Protection;
            _spans.Remove(span);
            if (spanStart < start)
            {
                _spans[spanStart] = new PermissionRange(spanStart, start - spanStart, protection);
            }

            if (spanEnd > end)
            {
                _spans[end] = new PermissionRange(end, spanEnd - end, protection);
                break;
            }
            candidate = spanEnd > spanStart ? _spans.FindAtOrAbove(spanEnd)
                : spanStart == ulong.MaxValue ? null : _spans.FindAtOrAbove(spanStart + 1);
        }
    }
}
