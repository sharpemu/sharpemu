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

    bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress);

    bool TryProtect(ulong address, ulong size, GuestPageProtection protection);
}
