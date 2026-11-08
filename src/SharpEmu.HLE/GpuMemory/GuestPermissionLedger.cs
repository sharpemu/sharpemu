// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

// The guest's own page permissions, written only by map and mprotect events.
public sealed class GuestPermissionLedger
{
    private readonly object _gate = new();
    private readonly PermissionRangeTree _spans = new();

    // The records as of the last whole change. Page tracking looks pages up on the render thread and in
    // the fault handlers of the guest threads at once, so a lookup takes no lock: it reads this snapshot.
    private PermissionRangeTree _published = new();

    public void Set(ulong address, ulong size, GuestPageProtection protection)
    {
        lock (_gate)
        {
            RemoveRangeRecords(address, address + size);
            _spans[address] = new PermissionRange(address, size, protection);
            Volatile.Write(ref _published, _spans.Snapshot());
        }
    }

    public void Clear(ulong address, ulong size)
    {
        lock (_gate)
        {
            RemoveRangeRecords(address, address + size);
            Volatile.Write(ref _published, _spans.Snapshot());
        }
    }

    // A span without a record is read-write: every caller is gated by a registered span first.
    // A lookup during a change answers from the records before it, as a locked one just before would.
    public GuestPageProtection Lookup(ulong address) => Lookup(address, out _);

    // validEnd is where the answer stops holding: the end of the record that holds the address, or the
    // start of the next record in a gap. A caller walking pages upward reuses the answer below it.
    public GuestPageProtection Lookup(ulong address, out ulong validEnd)
    {
        if (Volatile.Read(ref _published).FindAtOrBelow(address, out var nextAddress) is { } span &&
            span.Address + span.Size > address)
        {
            validEnd = span.Address + span.Size;
            return span.Protection;
        }

        validEnd = nextAddress;
        return GuestPageProtection.Read | GuestPageProtection.Write;
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
