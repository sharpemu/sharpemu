// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

// The caller holds the kernel memory lock for all queries and changes.
internal sealed class DirectMemoryAllocationMap
{
    internal readonly record struct Allocation(ulong Start, ulong Length, int MemoryType, bool IsAutomatic = false);
    internal readonly record struct PhysicalRange(ulong Start, ulong Length);

    private readonly SortedList<ulong, Allocation> _allocations = new();
    private readonly SortedList<ulong, ulong> _freeRanges = new();
    private readonly SortedList<ulong, ulong> _automaticFreeRanges = new();
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
        _automaticFreeRanges.Clear();
        if (_capacity != 0)
            _freeRanges.Add(0, _capacity);
        AvailableBytes = _capacity;
    }

    public bool TryAllocate(ulong searchStart, ulong searchEnd, ulong length, ulong alignment,
        int memoryType, out ulong address, bool isAutomatic = false)
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
            _allocations.Add(candidate, new Allocation(candidate, length, memoryType, isAutomatic));
            if (isAutomatic)
                AddRange(_automaticFreeRanges, candidate, length);
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

    public bool TryReserveAutomatic(ulong length, out PhysicalRange[] ranges)
    {
        ranges = [];
        if (length == 0)
            return false;

        var remaining = length;
        var plan = new List<PhysicalRange>();
        for (var index = 0; index < _automaticFreeRanges.Count && remaining != 0; index++)
        {
            var available = _automaticFreeRanges.Values[index];
            var take = Math.Min(available, remaining);
            if (take == 0)
                continue;
            plan.Add(new PhysicalRange(_automaticFreeRanges.Keys[index], take));
            remaining -= take;
        }

        if (remaining != 0)
            return false;

        foreach (var range in plan)
            RemoveRange(_automaticFreeRanges, range.Start, range.Length);
        ranges = plan.ToArray();
        return true;
    }

    public void ReturnAutomatic(ulong address, ulong length)
    {
        if (!ContainsAutomaticAllocatedRange(address, length))
            throw new InvalidOperationException("The returned automatic range is not allocated as automatic memory.");
        AddRange(_automaticFreeRanges, address, length);
    }

    public void RemoveAutomaticAvailability(ulong address, ulong length)
    {
        foreach (var range in GetAutomaticRanges(address, length))
            RemoveRange(_automaticFreeRanges, range.Start, range.Length, requireCovered: false);
    }

    public PhysicalRange[] GetAutomaticRanges(ulong address, ulong length)
    {
        if (length == 0 || address > _capacity || length > _capacity - address)
            return [];

        var end = address + length;
        var ranges = new List<PhysicalRange>();
        for (var index = Math.Max(0, FindLastIndexAtOrBelow(_allocations.Keys, address));
             index < _allocations.Count;
             index++)
        {
            var allocation = _allocations.Values[index];
            if (allocation.Start >= end)
                break;
            var allocationEnd = allocation.Start + allocation.Length;
            if (!allocation.IsAutomatic || allocationEnd <= address)
                continue;
            var start = Math.Max(address, allocation.Start);
            ranges.Add(new PhysicalRange(start, Math.Min(end, allocationEnd) - start));
        }
        return ranges.ToArray();
    }

    // Host aliases must be removed before their physical storage becomes available.
    public void ReleaseRange(ulong address, ulong length)
    {
        if (!ContainsAllocatedRange(address, length))
            throw new InvalidOperationException("The physical release range is not fully allocated.");

        RemoveRange(_automaticFreeRanges, address, length, requireCovered: false);
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
        AddRange(_freeRanges, address, length);
        AvailableBytes += length;
    }

    private bool ContainsAutomaticAllocatedRange(ulong address, ulong length)
    {
        if (!ContainsAllocatedRange(address, length))
            return false;
        var end = address + length;
        var index = FindLastIndexAtOrBelow(_allocations.Keys, address);
        var current = address;
        for (; index < _allocations.Count && current < end; index++)
        {
            var allocation = _allocations.Values[index];
            if (!allocation.IsAutomatic || allocation.Start > current || allocation.Start + allocation.Length <= current)
                return false;
            current = Math.Min(end, allocation.Start + allocation.Length);
        }
        return current == end;
    }

    private static void AddRange(SortedList<ulong, ulong> ranges, ulong address, ulong length)
    {
        if (length == 0)
            return;

        if (address > ulong.MaxValue - length)
            throw new InvalidOperationException("The free range wraps the physical address space.");

        var end = address + length;
        var index = Math.Max(0, FindLastIndexAtOrBelow(ranges.Keys, address));
        if (index < ranges.Count && ranges.Keys[index] + ranges.Values[index] < address)
            index++;

        if (index > 0 && ranges.Keys[index - 1] + ranges.Values[index - 1] >= address)
            index--;

        while (index < ranges.Count)
        {
            var rangeStart = ranges.Keys[index];
            if (rangeStart > end)
                break;

            var rangeEnd = rangeStart + ranges.Values[index];
            address = Math.Min(address, rangeStart);
            end = Math.Max(end, rangeEnd);
            ranges.RemoveAt(index);
        }

        ranges.Add(address, end - address);
    }

    private static void RemoveRange(
        SortedList<ulong, ulong> ranges,
        ulong address,
        ulong length,
        bool requireCovered = true)
    {
        if (length == 0)
            return;

        var end = address + length;
        var covered = 0UL;
        for (var index = Math.Max(0, FindLastIndexAtOrBelow(ranges.Keys, address)); index < ranges.Count;)
        {
            var rangeStart = ranges.Keys[index];
            var rangeEnd = rangeStart + ranges.Values[index];
            if (rangeStart >= end)
                break;
            if (rangeEnd <= address)
            {
                index++;
                continue;
            }

            var cutStart = Math.Max(address, rangeStart);
            var cutEnd = Math.Min(end, rangeEnd);
            covered += cutEnd - cutStart;
            ranges.RemoveAt(index);
            if (rangeStart < cutStart)
            {
                ranges.Add(rangeStart, cutStart - rangeStart);
                index++;
            }
            if (cutEnd < rangeEnd)
            {
                ranges.Add(cutEnd, rangeEnd - cutEnd);
                break;
            }
        }

        if (requireCovered && covered != length)
            throw new InvalidOperationException("The automatic range is not completely free.");
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
