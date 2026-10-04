// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The page granularity and address width every emitter uses for device addresses.
public static class DeviceAddressPaging
{
    // Guest pages are mapped to device buffers at this granularity; the host's cache uses the same.
    public const int PageBits = 14;
    public const ulong PageSize = 1UL << PageBits;
    public const ulong PageOffsetMask = PageSize - 1;

    // Sparse host ownership uses the raw 44-bit guest page number.  The dense
    // device page table packs the extended aperture directly after the low
    // one, so the unmapped multi-terabyte gap consumes no GPU memory.
    public const ulong LowerAddressSize = 1UL << 40;
    public const ulong ExtendedAddressBase = 0x0000_0800_0000_0000UL;
    public const ulong ExtendedAddressSize = 512UL * 1024 * 1024 * 1024;
    public const ulong ExtendedAddressLimit = ExtendedAddressBase + ExtendedAddressSize;
    public const ulong ExtendedAddressBias = ExtendedAddressBase - LowerAddressSize;
    public const ulong PackedAddressSize = LowerAddressSize + ExtendedAddressSize;
    public const ulong PageCount = PackedAddressSize >> PageBits;

    // Address handles carry 48 bits; the upper bits are ignored.
    public const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    public static bool IsValidAddress(ulong address) =>
        address < LowerAddressSize ||
        address >= ExtendedAddressBase && address < ExtendedAddressLimit;

    public static ulong PackAddress(ulong address)
    {
        if (!IsValidAddress(address))
        {
            throw new ArgumentOutOfRangeException(nameof(address), "The address is outside the guest GPU apertures.");
        }

        return address < LowerAddressSize ? address : address - ExtendedAddressBias;
    }

    public static ulong PageIndex(ulong address) => PackAddress(address) >> PageBits;

    public static ulong GuestAddress(ulong packedAddress)
    {
        if (packedAddress >= PackedAddressSize)
        {
            throw new ArgumentOutOfRangeException(nameof(packedAddress), "The packed address is outside the dense device-address table.");
        }

        return packedAddress < LowerAddressSize ? packedAddress : packedAddress + ExtendedAddressBias;
    }
}
