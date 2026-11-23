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

    // Address handles carry 48 bits; the upper bits are ignored.
    public const ulong AddressMask = 0x0000_FFFF_FFFF_FFFFul;

    // The fault buffer holds FaultBitmaps bitmaps, one bit per page-table entry each, then the trace record:
    //   0  missing pages (the entry is zero);
    //   1  stores through a mapped entry: the bytes changed in the cache buffer behind the entry and guest
    //      memory became the stale copy.
    // The host learns of a store through an address a shader computes at run time in no other way.
    public const uint FaultBitmaps = 2;
    public const uint StoreBitmap = 1;
}
