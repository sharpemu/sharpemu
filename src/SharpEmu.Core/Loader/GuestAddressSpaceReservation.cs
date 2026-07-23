// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Pre-claims the fixed PS5 guest image window on Windows before the .NET GC gets a
/// chance to (see #235). The GC's own initial virtual-address reservation can be very
/// large on a low-RAM machine and lands wherever the OS happens to place it; when that
/// reservation covers <see cref="GuestBase"/>, <c>SelfLoader</c>'s fixed-address mapping
/// for the main PS5 image fails outright with an unrecoverable exception, even though
/// none of those GC pages were ever committed there.
///
/// <see cref="Reserve"/> runs as a <see cref="ModuleInitializerAttribute"/>, which fires
/// as soon as this assembly's module is loaded - ahead of anything the loader itself
/// does, and (per manual testing against this repository's actual startup path) ahead
/// of the GC's own large reservation - so it wins the race for the address instead of
/// losing it. <see cref="Release"/> gives the window back immediately before
/// <c>SelfLoader</c> performs the real fixed-address allocation there.
/// </summary>
internal static class GuestAddressSpaceReservation
{
    // Covers Ps5MainImageBase (0x800000000) through Ps5ModuleSearchEnd (0x900000000)
    // in SelfLoader.cs - the whole fixed PS5 guest window.
    internal const ulong GuestBase = 0x0000000800000000UL;
    internal const ulong GuestSize = 0x100000000UL;

    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageNoAccess = 0x01;

    private static nint _reservedAddress;

    // CA2255 flags ModuleInitializer outside application code, but running as early as
    // possible - ahead of the GC's own reservation - is the entire point of this class.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Reserve()
    {
#pragma warning restore CA2255
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = VirtualAlloc(unchecked((nint)GuestBase), unchecked((nuint)GuestSize), MemReserve, PageNoAccess);
        if (result != unchecked((nint)GuestBase))
        {
            // Something else (most likely the GC) already won the race for this
            // address, or holds part of it under ASLR. SelfLoader's existing
            // TryBackFixedRange fallback still gets its normal shot at recovering;
            // this reservation is best-effort only.
            if (result != 0)
            {
                VirtualFree(result, 0, MemRelease);
            }

            return;
        }

        _reservedAddress = result;
    }

    /// <summary>
    /// Frees the pre-reservation so the real allocation in <c>SelfLoader</c> can claim
    /// the address. Safe to call more than once, and safe to call when nothing was ever
    /// reserved (non-Windows, or the reservation lost the startup race).
    /// </summary>
    internal static void Release()
    {
        var address = Interlocked.Exchange(ref _reservedAddress, 0);
        if (address != 0)
        {
            VirtualFree(address, 0, MemRelease);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAlloc(nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(nint lpAddress, nuint dwSize, uint dwFreeType);
}
