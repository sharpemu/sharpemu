// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Host page-allocation primitives used by the native execution engine.
/// Allocate/Reserve/Commit are deliberately separate members (rather than a
/// flags parameter) so every call site maps 1:1 onto the exact native call it
/// replaced, keeping the Windows behavior byte-for-byte identical.
/// </summary>
public interface IHostMemory
{
    /// <summary>
    /// Whether reserve-only mappings are replaceable placeholders and must be
    /// materialized explicitly before use.
    /// </summary>
    bool UsesPlaceholderReservations => false;

    /// <summary>
    /// Reserves and commits pages in one step. <paramref name="desiredAddress"/> of 0
    /// lets the OS choose the address. Returns the base address, or 0 on failure.
    /// The OS may satisfy the request at a different address than desired; callers
    /// that require an exact placement must check the result themselves.
    /// </summary>
    ulong Allocate(ulong desiredAddress, ulong size, HostPageProtection protection);

    /// <summary>Reserves address space without committing pages (lazy regions).</summary>
    ulong Reserve(ulong desiredAddress, ulong size, HostPageProtection protection);

    /// <summary>Creates a sparse shared backing object of the requested size.</summary>
    IHostSharedMemory? CreateSharedMemory(ulong size) => null;

    /// <summary>
    /// Maps a view of <paramref name="sharedMemory"/>. A nonzero desired address
    /// requires exact placement; the returned address denotes the requested
    /// byte offset, even when the host view has a larger alignment prefix.
    /// </summary>
    ulong MapSharedMemory(
        IHostSharedMemory sharedMemory,
        ulong desiredAddress,
        ulong size,
        ulong offset,
        HostPageProtection protection) => 0;

    /// <summary>Unmaps one exact shared-memory view previously returned by MapSharedMemory.</summary>
    bool UnmapSharedMemory(ulong address, ulong size) => false;

    /// <summary>Releases a range from a reserve-only mapping.</summary>
    bool UnmapReservedMemory(ulong address, ulong size) => false;

    /// <summary>Commits pages inside a previously reserved range (fault-path lazy commit).</summary>
    bool Commit(ulong address, ulong size, HostPageProtection protection);

    /// <summary>Releases an entire allocation or reservation by its base address.</summary>
    bool Free(ulong address);

    /// <summary>
    /// Changes protection on committed pages. <paramref name="rawOldProtection"/> is the
    /// untranslated previous OS protection value (see <see cref="HostRegionInfo.RawProtection"/>).
    /// </summary>
    bool Protect(ulong address, ulong size, HostPageProtection protection, out uint rawOldProtection);

    /// <summary>
    /// Restores a raw protection value previously returned by <see cref="Protect"/> or
    /// <see cref="Query"/> on this same platform. Raw values are opaque to callers and
    /// must never cross platforms; this exists so save/restore protection sequences
    /// round-trip OS-specific modifier bits the neutral enum cannot represent.
    /// </summary>
    bool ProtectRaw(ulong address, ulong size, uint rawProtection, out uint rawOldProtection);

    bool Query(ulong address, out HostRegionInfo info);

    void FlushInstructionCache(ulong address, ulong size);
}
