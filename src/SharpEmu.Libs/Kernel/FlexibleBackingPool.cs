// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

internal sealed class FlexibleBackingPool
{
    internal readonly record struct Block(ulong Address, ulong Size, ulong Offset);

    private readonly SortedList<ulong, ulong> _free = new();
    private readonly List<Block> _blocks = new();
    private readonly ulong _offset;
    private readonly ulong _size;

    public FlexibleBackingPool(ulong offset, ulong size)
    {
        _offset = offset;
        _size = size;
        Reset();
    }

    public ulong Used { get; private set; }
    public ulong Available => _size - Used;

    public void Reset()
    {
        _free.Clear();
        _free.Add(_offset, _size);
        _blocks.Clear();
        Used = 0;
    }

    public bool TryMap(IGuestBackedSpace space, ulong address, ulong size,
        GuestPageProtection protection, out Block[] blocks)
    {
        blocks = [];
        if (size == 0 || size > Available || address > ulong.MaxValue - size)
            return false;

        var planned = new List<Block>();
        // One mapping wants one run of backing bytes. The guest address range is
        // contiguous either way, but a mapping split across free ranges is not
        // contiguous in the backing, and a host that maps the backing no-copy can
        // then neither bind nor write back any buffer or image that crosses the seam:
        // it stages a copy per submission instead, and a GPU-written image over such
        // a range has nowhere to go. Smallest range that fits, so the big ranges stay
        // whole for the next large mapping.
        var best = -1;
        for (var index = 0; index < _free.Count; index++)
        {
            if (_free.Values[index] < size || (best >= 0 && _free.Values[index] >= _free.Values[best]))
                continue;
            best = index;
        }

        if (best >= 0)
        {
            planned.Add(new Block(address, size, _free.Keys[best]));
        }
        else
        {
            // Nothing holds it whole; fall back to filling it from several ranges.
            var remaining = size;
            var current = address;
            foreach (var range in _free)
            {
                var count = Math.Min(remaining, range.Value);
                planned.Add(new Block(current, count, range.Key));
                remaining -= count;
                current += count;
                if (remaining == 0)
                    break;
            }

            if (remaining != 0)
                return false;
        }

        for (var index = 0; index < planned.Count; index++)
        {
            var block = planned[index];
            if (space.TryClearBacking(block.Offset, block.Size) &&
                space.TryMapBacked(block.Address, block.Size, block.Offset, protection, out _))
                continue;

            for (var rollback = index - 1; rollback >= 0; rollback--)
                if (!space.TryUnmapBacked(planned[rollback].Address, planned[rollback].Size))
                    throw new InvalidOperationException("Cannot restore a failed flexible mapping.");
            return false;
        }

        foreach (var block in planned)
        {
            var freeSize = _free[block.Offset];
            _free.Remove(block.Offset);
            if (block.Size < freeSize)
                _free.Add(block.Offset + block.Size, freeSize - block.Size);
            _blocks.Add(block);
        }
        _blocks.Sort((left, right) => left.Address.CompareTo(right.Address));
        Used += size;
        blocks = planned.ToArray();
        return true;
    }

    // The caller must remove the host views before it returns their backing.
    public void Release(ulong address, ulong size)
    {
        var end = checked(address + size);
        var remaining = new List<Block>();
        ulong removed = 0;
        foreach (var block in _blocks)
        {
            var blockEnd = block.Address + block.Size;
            var start = Math.Max(address, block.Address);
            var stop = Math.Min(end, blockEnd);
            if (start >= stop)
            {
                remaining.Add(block);
                continue;
            }
            AddFree(block.Offset + start - block.Address, stop - start);
            removed += stop - start;
            if (block.Address < start)
                remaining.Add(block with { Size = start - block.Address });
            if (stop < blockEnd)
                remaining.Add(new Block(stop, blockEnd - stop, block.Offset + stop - block.Address));
        }
        if (removed != size || removed > Used)
            throw new InvalidOperationException("Flexible backing ownership is incomplete.");
        _blocks.Clear();
        _blocks.AddRange(remaining);
        Used -= removed;
    }

    private void AddFree(ulong start, ulong size)
    {
        var end = start + size;
        for (var index = _free.Count - 1; index >= 0; index--)
        {
            var rangeStart = _free.Keys[index];
            var rangeEnd = rangeStart + _free.Values[index];
            if (rangeStart > end || rangeEnd < start)
                continue;
            start = Math.Min(start, rangeStart);
            end = Math.Max(end, rangeEnd);
            _free.RemoveAt(index);
        }
        _free.Add(start, end - start);
    }
}
