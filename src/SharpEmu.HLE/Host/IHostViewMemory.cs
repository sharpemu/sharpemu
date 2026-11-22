// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

public readonly record struct HostAddressRange(ulong Address, ulong Size);

public readonly record struct HostPageAccess(ulong Address, ulong Size, HostPageProtection Protection);

// The caller must own each range. The host does not check this on all platforms.
public interface IHostViewMemory
{
    ulong PageSize { get; }

    ulong Granularity { get; }

    bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure);

    ulong ReserveHole(ulong address, ulong size);

    IReadOnlyList<HostAddressRange> ReserveFreeAddressRanges(ulong start, ulong end, ulong minimumSize) => [];

    bool SplitHole(ulong address, ulong size);

    bool JoinHoles(ulong address, ulong size);

    bool FreeHole(ulong address, ulong size);

    bool TryMapView(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure);

    bool UnmapView(ulong address, ulong size);

    bool CommitPrivate(ulong address, ulong size, HostPageProtection protection);

    bool ReleasePrivate(ulong address, ulong size);

    bool ChangeAccess(ulong address, ulong size, HostPageProtection protection);

    // The access of the range's committed pages, one entry per run of equal access, in address order. A page the host cannot
    // describe (not committed, or an access HostPageProtection has no value for) has no entry. No default: a wrapper that
    // left it out would silently report nothing.
    IReadOnlyList<HostPageAccess> QueryAccess(ulong address, ulong size);

    // Releases every private commit and placeholder that remains inside an owned range.
    bool FreeOwnedRange(ulong address, ulong size);
}
