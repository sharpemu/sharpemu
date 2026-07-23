// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

// Regression coverage for #235: on Windows, the .NET GC's own large virtual-address
// reservation can land on the fixed PS5 guest base (0x800000000) before SelfLoader ever
// runs, so the loader's fixed-address allocation fails outright even though none of
// those GC pages were ever committed. GuestAddressSpaceReservation wins that race with a
// ModuleInitializer that runs when this test assembly loads (before any test method
// executes), and these tests exercise the release half of that contract against the
// real, host-backed PhysicalVirtualMemory - not the fake IHostMemory used elsewhere in
// this suite - since the whole point is proving the real OS address is usable
// afterward.
public sealed class GuestAddressSpaceReservationTests
{
    [Fact]
    public void Release_FreesReservedWindow_AllowingExactAllocationAtGuestBase()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GuestAddressSpaceReservation.Release();

        using var memory = new PhysicalVirtualMemory();
        var allocated = memory.TryAllocateAtExact(
            GuestAddressSpaceReservation.GuestBase,
            0x1000,
            executable: true,
            out var actualAddress);

        Assert.True(allocated);
        Assert.Equal(GuestAddressSpaceReservation.GuestBase, actualAddress);
    }

    [Fact]
    public void Release_IsIdempotent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        GuestAddressSpaceReservation.Release();

        // Must not throw or double-free when called again with nothing left to release.
        GuestAddressSpaceReservation.Release();
    }
}
