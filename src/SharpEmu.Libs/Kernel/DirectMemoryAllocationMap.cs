// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

// The caller holds the kernel memory lock for all queries and changes.
internal sealed class DirectMemoryAllocationMap
{
    internal readonly record struct Allocation(ulong Start, ulong Length, int MemoryType);

    private readonly SortedList<ulong, Allocation> _allocations = new();
    private readonly SortedList<ulong, ulong> _freeRanges = new();
    private readonly ulong _capacity;

    public DirectMemoryAllocationMap(ulong capacity)
    {
        _capacity = capacity;
        Reset();
    }

    public ulong AvailableBytes { get; private set; }

    public void Reset()
    {
        _allocations.Clear();
        _freeRanges.Clear();
        if (_capacity != 0)
            _freeRanges.Add(0, _capacity);
        AvailableBytes = _capacity;
    }

    public bool TryAllocate(ulong searchStart, ulong searchEnd, ulong length, ulong alignment,
        int memoryType, out ulong address)
    {
        address = 0;
        searchEnd = Math.Min(searchEnd, _capacity);
        if (length == 0 || alignment == 0 || searchStart >= searchEnd)
            return false;

        var firstIndex = Math.Max(0, FindLastIndexAtOrBelow(_freeRanges.Keys, searchStart));
        for (var index = firstIndex; index < _freeRanges.Count; index++)
        {
            var rangeStart = _freeRanges.Keys[index];
            if (rangeStart >= searchEnd)
                break;
            var rangeEnd = rangeStart + _freeRanges.Values[index];
            var limit = Math.Min(rangeEnd, searchEnd);
            if (!TryAlignWithinRange(Math.Max(searchStart, rangeStart), limit, alignment, out var candidate) ||
                length > limit - candidate)
                continue;

            _freeRanges.RemoveAt(index);
            if (rangeStart < candidate)
                _freeRanges.Add(rangeStart, candidate - rangeStart);
            if (candidate + length < rangeEnd)
                _freeRanges.Add(candidate + length, rangeEnd - candidate - length);
            _allocations.Add(candidate, new Allocation(candidate, length, memoryType));
            AvailableBytes -= length;
            address = candidate;
            return true;
        }
        return false;
    }

    public bool TryFindAvailableRange(ulong searchStart, ulong searchEnd, ulong alignment,
        out ulong address, out ulong length)
    {
        address = 0;
        length = 0;
        searchEnd = Math.Min(searchEnd, _capacity);
        if (alignment == 0 || searchStart >= searchEnd)
            return false;

        var firstIndex = Math.Max(0, FindLastIndexAtOrBelow(_freeRanges.Keys, searchStart));
        for (var index = firstIndex; index < _freeRanges.Count; index++)
        {
            var rangeStart = _freeRanges.Keys[index];
            if (rangeStart >= searchEnd)
                break;
            var limit = Math.Min(rangeStart + _freeRanges.Values[index], searchEnd);
            if (!TryAlignWithinRange(Math.Max(searchStart, rangeStart), limit, alignment, out var candidate) ||
                limit - candidate <= length)
                continue;
            address = candidate;
            length = limit - candidate;
        }
        return length != 0;
    }

    public bool TryFindAllocation(ulong address, bool findNext, out Allocation allocation)
    {
        var index = FindLastIndexAtOrBelow(_allocations.Keys, address);
        if (index >= 0)
        {
            allocation = _allocations.Values[index];
            if (address - allocation.Start < allocation.Length)
                return true;
        }
        if (findNext && index + 1 < _allocations.Count)
        {
            allocation = _allocations.Values[index + 1];
            return true;
        }
        allocation = default;
        return false;
    }

    public bool ContainsAllocatedRange(ulong address, ulong length)
    {
        if (length == 0 || address > _capacity || length > _capacity - address)
            return false;
        var index = FindLastIndexAtOrBelow(_allocations.Keys, address);
        if (index < 0)
            return false;

        var current = address;
        var end = address + length;
        for (; index < _allocations.Count; index++)
        {
            var allocation = _allocations.Values[index];
            var allocationEnd = allocation.Start + allocation.Length;
            if (allocation.Start > current || allocationEnd <= current)
                return false;
            current = Math.Min(end, allocationEnd);
            if (current == end)
                return true;
        }
        return false;
    }

    public void SetMemoryType(ulong allocationStart, int memoryType)
        => _allocations[allocationStart] = _allocations[allocationStart] with { MemoryType = memoryType };

    // Host aliases must be removed before their physical storage becomes available.
    public void ReleaseRange(ulong address, ulong length)
    {
        if (!ContainsAllocatedRange(address, length))
            throw new InvalidOperationException("The physical release range is not fully allocated.");

        var end = address + length;
        var index = FindLastIndexAtOrBelow(_allocations.Keys, address);
        while (index < _allocations.Count)
        {
            var allocation = _allocations.Values[index];
            if (allocation.Start >= end)
                break;
            var allocationEnd = allocation.Start + allocation.Length;
            _allocations.RemoveAt(index);
            if (allocation.Start < address)
            {
                _allocations.Add(allocation.Start, allocation with { Length = address - allocation.Start });
                index++;
            }
            if (end < allocationEnd)
            {
                _allocations.Add(end, allocation with { Start = end, Length = allocationEnd - end });
                break;
            }
        }
        AddFreeRange(address, length);
        AvailableBytes += length;
    }

    private void AddFreeRange(ulong address, ulong length)
    {
        var end = address + length;
        var previousIndex = FindLastIndexAtOrBelow(_freeRanges.Keys, address);
        var nextIndex = previousIndex + 1;
        if (previousIndex >= 0 &&
            _freeRanges.Keys[previousIndex] + _freeRanges.Values[previousIndex] == address)
        {
            address = _freeRanges.Keys[previousIndex];
            _freeRanges.RemoveAt(previousIndex);
            nextIndex = previousIndex;
        }
        if (nextIndex < _freeRanges.Count && _freeRanges.Keys[nextIndex] == end)
        {
            end += _freeRanges.Values[nextIndex];
            _freeRanges.RemoveAt(nextIndex);
        }
        _freeRanges.Add(address, end - address);
    }

    private static bool TryAlignWithinRange(ulong address, ulong end, ulong alignment, out ulong aligned)
    {
        aligned = 0;
        if (address >= end)
            return false;
        var remainder = address % alignment;
        var padding = remainder == 0 ? 0 : alignment - remainder;
        if (padding >= end - address)
            return false;
        aligned = address + padding;
        return true;
    }

    private static int FindLastIndexAtOrBelow(IList<ulong> keys, ulong address)
    {
        var lower = 0;
        var upper = keys.Count;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            if (keys[middle] <= address)
                lower = middle + 1;
            else
                upper = middle;
        }
        return lower - 1;
    }
}
