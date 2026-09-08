// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Images;

// The images that own one 1 MiB page; the first sixteen stay inline, more move to a list.
public sealed class PageOwnerList
{
    public const int InlineCapacity = 16;

    private readonly ResourceSlotIdentifier[] _inline = new ResourceSlotIdentifier[InlineCapacity];
    private List<ResourceSlotIdentifier>? _overflow;
    private int _inlineCount;

    public int Count => _overflow?.Count ?? _inlineCount;

    public bool IsEmpty => Count == 0;

    public bool UsesOverflow => _overflow != null;

    public ResourceSlotIdentifier this[int index] => _overflow != null ? _overflow[index] : _inline[index];

    public void Add(ResourceSlotIdentifier owner)
    {
        if (_overflow != null)
        {
            _overflow.Add(owner);
            return;
        }

        if (_inlineCount < InlineCapacity)
        {
            _inline[_inlineCount++] = owner;
            return;
        }

        _overflow = new List<ResourceSlotIdentifier>(InlineCapacity * 2);
        _overflow.AddRange(_inline);
        _overflow.Add(owner);
    }

    public bool Contains(ResourceSlotIdentifier owner) =>
        _overflow != null ? _overflow.Contains(owner) : Array.IndexOf(_inline, owner, 0, _inlineCount) >= 0;

    // Removes only the requested owner; the other owners of the page stay in place.
    public bool Remove(ResourceSlotIdentifier owner)
    {
        if (_overflow != null)
        {
            if (!_overflow.Remove(owner))
            {
                return false;
            }

            if (_overflow.Count == InlineCapacity)
            {
                _overflow.CopyTo(_inline);
                _inlineCount = InlineCapacity;
                _overflow = null;
            }

            return true;
        }

        var found = Array.IndexOf(_inline, owner, 0, _inlineCount);
        if (found < 0)
        {
            return false;
        }

        Array.Copy(_inline, found + 1, _inline, found, _inlineCount - found - 1);
        _inlineCount--;
        return true;
    }

    public void ForEach(Action<ResourceSlotIdentifier> visit)
    {
        if (_overflow != null)
        {
            foreach (var owner in _overflow)
            {
                visit(owner);
            }

            return;
        }

        for (var index = 0; index < _inlineCount; index++)
        {
            visit(_inline[index]);
        }
    }
}

// Sparse two-level owner lookup by 1 MiB guest page; queries never allocate a bucket.
public sealed class ImagePageOwnerTable
{
    public const int PageBits = 20;
    public const int AddressSpaceBits = 40;
    public const int FirstLevelBits = 10;
    public const int SecondLevelBits = AddressSpaceBits - FirstLevelBits - PageBits;
    public const int BucketEntries = 1 << SecondLevelBits;
    public const ulong PageCount = 1UL << (AddressSpaceBits - PageBits);
    public const ulong AddressSpaceSize = 1UL << AddressSpaceBits;

    private readonly PageOwnerList?[]?[] _firstLevel = new PageOwnerList?[]?[1 << FirstLevelBits];

    public int AllocatedBucketCount { get; private set; }

    public static bool IsValidPage(ulong page) => page < PageCount;

    public PageOwnerList? Find(ulong page) =>
        IsValidPage(page) && _firstLevel[page >> SecondLevelBits] is { } bucket ? bucket[page & (BucketEntries - 1)] : null;

    public PageOwnerList GetOrCreate(ulong page)
    {
        if (!IsValidPage(page))
        {
            throw SubmissionScheduler.Fatal($"The owner-table page is outside the guest address space: page={page}.");
        }

        ref var bucket = ref _firstLevel[page >> SecondLevelBits];
        if (bucket == null)
        {
            bucket = new PageOwnerList?[BucketEntries];
            AllocatedBucketCount++;
        }

        return bucket[page & (BucketEntries - 1)] ??= new PageOwnerList();
    }

    // The half-open page interval of a non-empty byte range inside the address space.
    public static bool TryGetPageRange(ulong address, ulong size, out ulong first, out ulong lastExclusive)
    {
        first = 0;
        lastExclusive = 0;
        if (size == 0 || address >= AddressSpaceSize || size > AddressSpaceSize - address)
        {
            return false;
        }

        first = address >> PageBits;
        lastExclusive = ((address + size - 1) >> PageBits) + 1;
        return true;
    }
}
