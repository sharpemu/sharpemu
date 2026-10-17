// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Buffers;

// Page geometry for guest buffer ownership and device-address table updates.
public static class PageOwnerTable
{
    public const int PageBits = 14;
    public const int AddressSpaceBits = 40;
    public const ulong PageCount = 1UL << (AddressSpaceBits - PageBits);
    public const ulong AddressSpaceSize = 1UL << AddressSpaceBits;

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
