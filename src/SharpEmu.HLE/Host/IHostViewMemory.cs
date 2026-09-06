// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

// The caller must own each range. The host does not check this on all platforms.
public interface IHostViewMemory
{
    ulong PageSize { get; }

    ulong Granularity { get; }

    bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure);

    ulong ReserveHole(ulong address, ulong size);

    bool SplitHole(ulong address, ulong size);

    bool JoinHoles(ulong address, ulong size);

    bool FreeHole(ulong address, ulong size);

    bool TryMapView(HostBackingObject backing, ulong address, ulong offset, ulong size, HostPageProtection protection, out HostViewFailure failure);

    bool UnmapView(ulong address, ulong size);

    bool CommitPrivate(ulong address, ulong size, HostPageProtection protection);

    bool ReleasePrivate(ulong address, ulong size);

    bool ChangeAccess(ulong address, ulong size, HostPageProtection protection);

    // Releases every private commit and placeholder that remains inside an owned range.
    bool FreeOwnedRange(ulong address, ulong size);
}
