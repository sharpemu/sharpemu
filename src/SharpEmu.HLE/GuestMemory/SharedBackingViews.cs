// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.HLE.GuestMemory;

public readonly record struct ViewRecord(ulong Address, ulong Size, ulong Offset, HostPageProtection Protection);

public sealed unsafe class SharedBackingViews : IDisposable
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    private readonly IHostViewMemory _host;
    private readonly HostBackingObject? _backing;
    private readonly object _lock = new();
    private readonly SortedList<ulong, ViewRecord> _views = new();
    private bool _disposed;

    public SharedBackingViews(IHostViewMemory host, ulong size)
    {
        _host = host;
        _ = host.TryCreateBacking(size, out _backing, out _);
    }

    public bool IsAvailable => _backing != null && !_disposed;

    public ulong AliasBase => _backing?.AliasBase ?? 0;

    public ulong Size => _backing?.Size ?? 0;

    public bool Clear(ulong offset, ulong size)
    {
        lock (_lock)
        {
            if (!IsAvailable || !IsWithinBacking(offset, size))
            {
                return false;
            }

            NativeMemory.Clear((void*)(AliasBase + offset), (nuint)size);
            return true;
        }
    }

    public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (!TryCollectBackingSegments(address, (ulong)data.Length, out var pieces))
            {
                return false;
            }

            foreach (var (backing, dataOffset, bytes) in pieces)
            {
                data.Slice(dataOffset, bytes).CopyTo(new Span<byte>((void*)backing, bytes));
            }

            return true;
        }
    }

    public bool TryReadBacking(ulong address, Span<byte> data)
    {
        lock (_lock)
        {
            if (!TryCollectBackingSegments(address, (ulong)data.Length, out var pieces))
            {
                return false;
            }

            foreach (var (backing, dataOffset, bytes) in pieces)
            {
                new ReadOnlySpan<byte>((void*)backing, bytes).CopyTo(data.Slice(dataOffset, bytes));
            }

            return true;
        }
    }

    // Use temporary storage if a copy segment can overwrite another segment's source.
    public bool TryCopyBacking(ulong destination, ulong source, ulong size)
    {
        lock (_lock)
        {
            if (!TryCollectBackingSegments(source, size, out var from) || !TryCollectBackingSegments(destination, size, out var to))
            {
                return false;
            }

            var chunks = CreateCopySegments(from, to);
            if (!HasCrossSegmentOverlap(chunks))
            {
                foreach (var (fromPtr, toPtr, bytes) in chunks)
                {
                    Buffer.MemoryCopy((void*)fromPtr, (void*)toPtr, bytes, bytes);
                }

                return true;
            }

            var staging = new byte[size];
            foreach (var (backing, dataOffset, bytes) in from)
            {
                new ReadOnlySpan<byte>((void*)backing, bytes).CopyTo(staging.AsSpan(dataOffset, bytes));
            }

            foreach (var (backing, dataOffset, bytes) in to)
            {
                staging.AsSpan(dataOffset, bytes).CopyTo(new Span<byte>((void*)backing, bytes));
            }

            return true;
        }
    }

    public bool TryMapReservedRange(ulong address, ulong size, ulong offset, HostPageProtection protection, out HostViewFailure failure)
    {
        if (!IsAvailable || !IsWithinBacking(offset, size))
        {
            failure = IsAvailable ? HostViewFailure.OffsetOutOfBounds : HostViewFailure.BackingUnavailable;
            return false;
        }

        // Keep the mapping and its record under one lock to prevent disposal between them.
        lock (_lock)
        {
            if (!_host.TryMapView(_backing!, address, offset, size, protection, out failure))
            {
                return false;
            }

            if (_disposed)
            {
                _ = _host.UnmapView(address, size);
                failure = HostViewFailure.BackingUnavailable;
                return false;
            }

            if (_views.ContainsKey(address))
            {
                OnFatal($"A backing view record already exists at 0x{address:X16}.");
            }

            _views[address] = new ViewRecord(address, size, offset, protection);
        }

        failure = HostViewFailure.None;
        return true;
    }

    public bool Unmap(ulong address, ulong size, out bool holePreserved)
    {
        holePreserved = false;
        if (!IsAvailable || size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var end = address + size;
        var targets = new List<ViewRecord>();
        lock (_lock)
        {
            var current = address;
            while (current < end)
            {
                if (!TryFindRecord(current, 1, out var record))
                {
                    return false;
                }

                var partSize = Math.Min(end, record.Address + record.Size) - current;
                targets.Add(new ViewRecord(current, partSize, record.Offset + current - record.Address, record.Protection));
                current += partSize;
            }
        }

        var removed = new List<ViewRecord>();
        foreach (var target in targets)
        {
            if (!TryUnmapSingleView(target.Address, target.Size, out var partPreserved))
            {
                RestoreMappingsInReverseOrder(removed);
                return false;
            }

            if (!partPreserved)
            {
                OnFatal($"The address range was not kept reserved after unmapping the view at 0x{target.Address:X16}.");
            }

            removed.Add(target);
        }

        // Each removed view leaves a reserved range. Join these ranges into one free range.
        if (removed.Count > 1 && !_host.JoinHoles(address, size))
        {
            RestoreMappingsInReverseOrder(removed);
            return false;
        }

        holePreserved = true;
        return true;
    }

    private bool TryUnmapSingleView(ulong address, ulong size, out bool holePreserved)
    {
        holePreserved = false;
        var old = default(ViewRecord);
        lock (_lock)
        {
            var index = RangeSearch.FindLastIndexAtOrBelow(_views, address);
            if (index >= 0)
            {
                var record = _views.Values[index];
                if (address + size <= record.Address + record.Size)
                {
                    old = record;
                    _views.RemoveAt(index);
                }
            }
        }

        if (old.Size == 0)
        {
            return false;
        }

        if (!_host.UnmapView(old.Address, old.Size))
        {
            RestoreViewRecord(old);
            return false;
        }

        if (address == old.Address && size == old.Size)
        {
            holePreserved = true;
            return true;
        }

        var leftSize = address - old.Address;
        var rightAddress = address + size;
        var rightSize = old.Address + old.Size - rightAddress;
        var ok = true;
        if (leftSize != 0 && leftSize != old.Size)
        {
            ok = _host.SplitHole(old.Address, leftSize) && ok;
        }

        if (rightSize != 0)
        {
            ok = _host.SplitHole(address, size) && ok;
        }

        if (leftSize != 0)
        {
            ok = TryMapReservedRange(old.Address, leftSize, old.Offset, old.Protection, out _) && ok;
        }

        if (rightSize != 0)
        {
            ok = TryMapReservedRange(rightAddress, rightSize, old.Offset + (rightAddress - old.Address), old.Protection, out _) && ok;
        }

        if (ok)
        {
            holePreserved = true;
            return true;
        }

        if (leftSize != 0 && Contains(old.Address, leftSize) && !TryUnmapSingleView(old.Address, leftSize, out _))
        {
            OnFatal($"Could not remove the partial backing view at 0x{old.Address:X16} during recovery.");
        }

        if (rightSize != 0 && Contains(rightAddress, rightSize) && !TryUnmapSingleView(rightAddress, rightSize, out _))
        {
            OnFatal($"Could not remove the partial backing view at 0x{rightAddress:X16} during recovery.");
        }

        if (!_host.JoinHoles(old.Address, old.Size))
        {
            OnFatal($"Could not join the reserved ranges at 0x{old.Address:X16} during recovery.");
        }

        RestoreViewMapping(old);
        return false;
    }

    public bool Contains(ulong address, ulong size)
    {
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        lock (_lock)
        {
            var end = address + size;
            var current = address;
            while (current < end)
            {
                if (!TryFindRecord(current, 1, out var record))
                {
                    return false;
                }

                current = Math.Min(end, record.Address + record.Size);
            }

            return true;
        }
    }

    // Remove all recorded views before releasing the backing alias and object.
    public void Dispose()
    {
        List<ViewRecord> views;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            views = new List<ViewRecord>(_views.Values);
            _views.Clear();
        }

        foreach (var view in views)
        {
            if (!_host.UnmapView(view.Address, view.Size))
            {
                OnFatal($"Could not remove the backing view at 0x{view.Address:X16} during disposal.");
            }
        }

        _backing?.Dispose();
    }

    private bool TryCollectBackingSegments(ulong address, ulong size, out List<(ulong Backing, int DataOffset, int Bytes)> pieces)
    {
        pieces = new List<(ulong, int, int)>();
        if (!IsAvailable || size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var end = address + size;
        var current = address;
        while (current < end)
        {
            if (!TryFindRecord(current, 1, out var record))
            {
                return false;
            }

            var bytes = Math.Min(end, record.Address + record.Size) - current;
            var offset = record.Offset + current - record.Address;
            if (!IsWithinBacking(offset, bytes))
            {
                return false;
            }

            pieces.Add((AliasBase + offset, (int)(current - address), (int)bytes));
            current += bytes;
        }

        return true;
    }

    private static List<(ulong From, ulong To, int Bytes)> CreateCopySegments(
        List<(ulong Backing, int DataOffset, int Bytes)> from,
        List<(ulong Backing, int DataOffset, int Bytes)> to)
    {
        var chunks = new List<(ulong, ulong, int)>();
        var i = 0;
        var j = 0;
        var offset = 0;
        while (i < from.Count && j < to.Count)
        {
            var fromEnd = from[i].DataOffset + from[i].Bytes;
            var toEnd = to[j].DataOffset + to[j].Bytes;
            var end = Math.Min(fromEnd, toEnd);
            chunks.Add((
                from[i].Backing + (ulong)(offset - from[i].DataOffset),
                to[j].Backing + (ulong)(offset - to[j].DataOffset),
                end - offset));
            offset = end;
            i += fromEnd == end ? 1 : 0;
            j += toEnd == end ? 1 : 0;
        }

        return chunks;
    }

    private static bool HasCrossSegmentOverlap(List<(ulong From, ulong To, int Bytes)> chunks)
    {
        for (var a = 0; a < chunks.Count; a++)
        {
            for (var b = 0; b < chunks.Count; b++)
            {
                if (a != b && chunks[a].From < chunks[b].To + (ulong)chunks[b].Bytes &&
                    chunks[b].To < chunks[a].From + (ulong)chunks[a].Bytes)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryFindRecord(ulong address, ulong size, out ViewRecord record)
    {
        record = default;
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var index = RangeSearch.FindLastIndexAtOrBelow(_views, address);
        if (index < 0)
        {
            return false;
        }

        var candidate = _views.Values[index];
        if (address + size > candidate.Address + candidate.Size)
        {
            return false;
        }

        record = candidate;
        return true;
    }

    private bool IsWithinBacking(ulong offset, ulong size) =>
        size != 0 && offset < Size && size <= Size - offset;

    private void RestoreViewRecord(ViewRecord record)
    {
        lock (_lock)
        {
            _views[record.Address] = record;
        }
    }

    private void RestoreViewMapping(ViewRecord record)
    {
        if (!TryMapReservedRange(record.Address, record.Size, record.Offset, record.Protection, out _))
        {
            OnFatal($"Could not restore the backing view at 0x{record.Address:X16}.");
        }
    }

    private void RestoreMappingsInReverseOrder(List<ViewRecord> records)
    {
        for (var index = records.Count - 1; index >= 0; index--)
        {
            RestoreViewMapping(records[index]);
        }
    }
}
