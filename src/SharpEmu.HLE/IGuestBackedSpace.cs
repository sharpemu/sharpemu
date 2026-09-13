// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.HLE;

public interface IGuestBackedSpace
{
    bool TryHoldRange(ulong address, ulong size);
    bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address);
    bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure);
    bool TryUnmapBacked(ulong address, ulong size);
    bool TryClearBacking(ulong offset, ulong size);
    bool IsBackedView(ulong address);
    bool IsBackedRange(ulong address, ulong size);

    // Wait for view replacement, then confirm that the restored page allows the access.
    bool CanRetryRestoredViewAccess(ulong address, GuestPageProtection access) => false;

    // Copies through the backing alias only: no protection change, no store notification.
    bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data);
    bool TryReadBacking(ulong address, Span<byte> data);
}
