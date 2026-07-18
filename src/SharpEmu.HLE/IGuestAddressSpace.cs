// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Guest address-space manipulation beyond plain allocation: fixed-address
/// mapping and page-protection changes. Guest addresses are identity-mapped
/// onto host pages by the implementing memory, so HLE exports (mmap, mprotect)
/// reach these operations through <c>ctx.Memory</c> instead of calling host
/// APIs directly. Member signatures deliberately mirror the implementation in
/// SharpEmu.Core so existing call sites migrate call-for-call.
/// </summary>
public interface IGuestAddressSpace : IGuestMemoryAllocator
{
    ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true);

    ulong ReserveAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true);

    bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress);

    bool TryReserveAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress);

    bool TryProtect(ulong address, ulong size, GuestPageProtection protection);

    bool TryMapDirectMemory(
        ulong desiredAddress,
        ulong size,
        ulong directMemoryOffset,
        ulong directMemorySize,
        GuestPageProtection protection,
        ulong alignment,
        bool allowSearch,
        out ulong actualAddress);

    /// <summary>
    /// Transactionally replaces every direct-memory view page in the supplied
    /// fixed virtual range, preserving unaffected prefixes and suffixes. A
    /// failed replacement restores the previous mappings before returning.
    /// </summary>
    bool TryReplaceDirectMemory(
        ulong address,
        ulong size,
        ulong directMemoryOffset,
        ulong directMemorySize,
        GuestPageProtection protection,
        out ulong actualAddress);

    bool TryUnmapDirectMemory(ulong address, ulong size);

    /// <summary>
    /// Removes every direct-memory view page in the supplied virtual range,
    /// preserving and remapping any non-overlapping prefix or suffix.
    /// </summary>
    bool TryUnmapDirectMemoryRange(ulong address, ulong size) => false;

    bool TryUnmapReservedMemory(ulong address, ulong size);

    bool IsAccessible(ulong virtualAddress, ulong size);
}
