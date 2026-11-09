// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GuestMemory;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private sealed class MappedRegionTable
    {
        private sealed class Entry(ulong address, MappedRegion region = default)
        {
            public ulong Address { get; } = address;
            public MappedRegion Region = region;
        }

        private static readonly Entry Minimum = new(0);
        private static readonly Entry Maximum = new(ulong.MaxValue);
        private readonly SortedSet<Entry> _entries = new(
            Comparer<Entry>.Create((left, right) => left.Address.CompareTo(right.Address)));
        private readonly DirectMappingIndex _directMappings = new();
        private readonly AllocationGapTree _allocationGaps = new();
        private int _invalidRangeCount;

        public IEnumerable<MappedRegion> Values => _entries.Select(entry => entry.Region);

        public MappedRegion this[ulong address]
        {
            set
            {
                var key = new Entry(address, value);
                if (_entries.TryGetValue(key, out var existing))
                {
                    RemoveAllocationRange(existing.Region);
                    if (existing.Region.IsDirect)
                        _directMappings.Remove(existing.Region);
                    existing.Region = value;
                }
                else
                    _entries.Add(key);
                if (value.IsDirect)
                    _directMappings.Add(value);
                if (IsValidRange(value))
                    _allocationGaps.Set(address, value.Length);
                else
                    _invalidRangeCount++;
            }
        }

        public bool TryGetValue(ulong address, out MappedRegion region)
        {
            var found = _entries.TryGetValue(new Entry(address), out var entry);
            region = found ? entry!.Region : default;
            return found;
        }

        public bool TryFindAtOrBelow(ulong address, out MappedRegion region)
        {
            var entry = _entries.GetViewBetween(Minimum, new Entry(address)).Max;
            region = entry?.Region ?? default;
            return entry is not null;
        }

        public bool TryFindAtOrAbove(ulong address, out MappedRegion region)
        {
            var entry = _entries.GetViewBetween(new Entry(address), Maximum).Min;
            region = entry?.Region ?? default;
            return entry is not null;
        }

        // Include the preceding entry because its range can overlap the start.
        public IEnumerable<MappedRegion> FromAddress(ulong address)
        {
            var start = TryFindAtOrBelow(address, out var preceding) ? preceding.Address : address;
            foreach (var entry in _entries.GetViewBetween(new Entry(start), Maximum))
                yield return entry.Region;
        }

        public MappedRegion[] FindDirectOverlaps(ulong start, ulong length) =>
            _directMappings.FindOverlaps(start, length);

        public void Remove(ulong address)
        {
            if (TryGetValue(address, out var region))
            {
                RemoveAllocationRange(region);
                if (region.IsDirect)
                    _directMappings.Remove(region);
            }
            _entries.Remove(new Entry(address));
        }

        public bool TryFindAvailableAddress(ulong start, ulong size, ulong alignment, out ulong address)
        {
            // Preserve the legacy scan for records outside the gap index contract.
            address = _invalidRangeCount == 0 ? _allocationGaps.Find(start, size, alignment, ulong.MaxValue) : 0;
            return _invalidRangeCount == 0;
        }

        private static bool IsValidRange(MappedRegion region) =>
            region.Length != 0 && region.Length <= ulong.MaxValue - region.Address;

        private void RemoveAllocationRange(MappedRegion region)
        {
            if (IsValidRange(region))
                _allocationGaps.Remove(region.Address);
            else
                _invalidRangeCount--;
        }

        public void Clear()
        {
            _entries.Clear();
            _directMappings.Clear();
            _allocationGaps.Clear();
            _invalidRangeCount = 0;
        }
    }
}
