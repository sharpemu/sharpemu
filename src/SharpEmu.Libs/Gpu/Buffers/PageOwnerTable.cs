// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Buffers;

// Two-level owner lookup by 16 KiB guest page; queries never allocate a bucket.
public sealed class PageOwnerTable
{
    public const int PageBits = 14;
    public const int AddressSpaceBits = 40;
    public const int FirstLevelBits = 16;
    public const int SecondLevelBits = AddressSpaceBits - FirstLevelBits - PageBits;
    public const int BucketEntries = 1 << SecondLevelBits;
    public const ulong PageCount = 1UL << (AddressSpaceBits - PageBits);
    public const ulong AddressSpaceSize = 1UL << AddressSpaceBits;

    private readonly BufferSlot[]?[] _firstLevel = new BufferSlot[]?[1 << FirstLevelBits];

    public int AllocatedBucketCount { get; private set; }

    public BufferSlot Find(ulong page) =>
        page < PageCount && _firstLevel[page >> SecondLevelBits] is { } bucket
            ? bucket[page & (BucketEntries - 1)]
            : BufferSlot.Invalid;

    public void Set(ulong page, BufferSlot owner)
    {
        if (page >= PageCount)
        {
            throw SubmissionScheduler.Fatal("The owner-table page is outside the guest address space.");
        }

        ref var bucket = ref _firstLevel[page >> SecondLevelBits];
        if (bucket == null)
        {
            bucket = new BufferSlot[BucketEntries];
            AllocatedBucketCount++;
        }

        bucket[page & (BucketEntries - 1)] = owner;
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
