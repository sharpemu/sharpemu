// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Buffers;

// Page geometry for guest buffer ownership and device-address table updates.
public static class PageOwnerTable
{
    public const int PageBits = 14;
    public const int AddressSpaceBits = 44;
    public const ulong PageCount = 1UL << (AddressSpaceBits - PageBits);
    public const ulong AddressSpaceSize = 1UL << AddressSpaceBits;
    public const ulong LowerAddressSpaceSize = DeviceAddressPaging.LowerAddressSize;
    public const ulong ExtendedAddressStart = GuestMemoryLayout.GuestExtendedAddressStart;
    public const ulong ExtendedAddressLimit = GuestMemoryLayout.GuestExtendedAddressLimit;
    public const ulong PackedPageCount = DeviceAddressPaging.PageCount;
    public const ulong PackedAddressSpaceSize = DeviceAddressPaging.PackedAddressSize;

    public static ulong PageIndex(ulong address) => DeviceAddressPaging.PageIndex(address);

    public static ulong GuestAddress(ulong packedAddress) => DeviceAddressPaging.GuestAddress(packedAddress);

    public static ulong ApertureStart(ulong address) =>
        address < LowerAddressSpaceSize ? 0 : ExtendedAddressStart;

    public static ulong ApertureLimit(ulong address) =>
        address < LowerAddressSpaceSize ? LowerAddressSpaceSize : ExtendedAddressLimit;

    // The half-open page interval of a non-empty byte range inside the address space.
    public static bool TryGetPageRange(ulong address, ulong size, out ulong first, out ulong lastExclusive)
    {
        first = 0;
        lastExclusive = 0;
        if (size == 0 || !new GuestSpan(address, size).IsValid ||
            address >= AddressSpaceSize || size > AddressSpaceSize - address)
        {
            return false;
        }

        first = address >> PageBits;
        lastExclusive = ((address + size - 1) >> PageBits) + 1;
        return true;
    }
}
