// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpEmu.CLI;

/// <summary>
/// Reserves the guest address space early in process startup, before the .NET runtime
/// has a chance to reserve its GC heap in the same region. On systems with limited RAM
/// (e.g. 16 GB), the .NET GC may reserve up to 256 GB of virtual address space, which
/// can overlap the fixed PS5 image base at 0x800000000.
/// </summary>
internal static partial class GuestAddressSpaceReservation
{
    // PS5 guest address space layout:
    // - Main image: 0x0000000800000000 (32 GB)
    // - Module search: 0x0000000804000000 - 0x0000000900000000
    // - Import stubs: 0x0000_7000_0000_0000
    // - Guest arena: 0x00006000_0000_0000
    //
    // We reserve the critical region 0x800000000 - 0x900000000 (4 GB) to ensure
    // the main image can be loaded at its required base address.
    private const ulong Ps5CriticalRegionBase = 0x0000000800000000UL;
    private const ulong Ps5CriticalRegionSize = 0x0000000100000000UL; // 4 GB

    private static IntPtr _reservedRegion;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Reserve the critical guest address space region as early as possible.
        // This must happen before the .NET GC reserves its heap.
        ReserveCriticalRegion();
    }

    private static void ReserveCriticalRegion()
    {
        if (!OperatingSystem.IsWindows())
        {
            // On POSIX systems, the GC heap reservation pattern is different and
            // typically doesn't conflict with the guest address space.
            return;
        }

        // MEM_RESERVE without MEM_COMMIT - just reserves the address range
        const uint MEM_RESERVE = 0x00002000;
        const uint PAGE_NOACCESS = 0x01;

        _reservedRegion = VirtualAlloc(
            unchecked((IntPtr)Ps5CriticalRegionBase),
            unchecked((UIntPtr)Ps5CriticalRegionSize),
            MEM_RESERVE,
            PAGE_NOACCESS);

        if (_reservedRegion == IntPtr.Zero)
        {
            // Reservation failed - the address space might already be taken.
            // PhysicalVirtualMemory will handle the failure gracefully.
            return;
        }

        // Verify we got the exact address we requested
        if ((ulong)_reservedRegion != Ps5CriticalRegionBase)
        {
            // Got a different address - free it and let the loader handle it
            VirtualFree(_reservedRegion, UIntPtr.Zero, 0x00008000 /* MEM_RELEASE */);
            _reservedRegion = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Releases the early reservation so PhysicalVirtualMemory can allocate normally.
    /// Called just before the loader needs the address space.
    /// </summary>
    internal static void ReleaseReservation()
    {
        if (_reservedRegion != IntPtr.Zero)
        {
            VirtualFree(_reservedRegion, UIntPtr.Zero, 0x00008000 /* MEM_RELEASE */);
            _reservedRegion = IntPtr.Zero;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAlloc(
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint flAllocationType,
        uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint dwFreeType);
}
