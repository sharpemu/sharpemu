// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using static SharpEmu.Libs.Tests.Memory.HostViews.HostViewTestSupport;

namespace SharpEmu.Libs.Tests.Memory.GuestMemory;

public sealed unsafe partial class GuestSpaceOwnerTests
{
    private enum InitializationCall
    {
        ReserveHole,
        ReserveFreeRegions,
        CreateBacking,
        ReleaseBacking,
        FreeOwnedRange,
    }

    private sealed class InitializationHostViews(
        bool reservePrimaryUserRange,
        bool reserveExtendedRange = true) : IHostViewMemory
    {
        public List<InitializationCall> Calls { get; } = new();

        public ulong PageSize => 0x1000;

        public ulong Granularity => 0x10000;

        public bool TryCreateBacking(ulong size, out HostBackingObject? backing, out HostViewFailure failure)
        {
            Calls.Add(InitializationCall.CreateBacking);
            backing = new HostBackingObject(0x1_0000, size, _ => Calls.Add(InitializationCall.ReleaseBacking));
            failure = HostViewFailure.None;
            return true;
        }

        public bool TryCommitBacking(HostBackingObject backing, ulong offset, ulong size) => false;

        public bool TryReserveFreeRegions(
            ulong startAddress,
            ulong endAddress,
            ulong minimumRegionSize,
            out IReadOnlyList<HostAddressRange> reservations)
        {
            Calls.Add(InitializationCall.ReserveFreeRegions);
            reservations = Array.Empty<HostAddressRange>();
            return false;
        }

        public ulong ReserveHole(ulong address, ulong size)
        {
            Calls.Add(InitializationCall.ReserveHole);
            if (address == GuestMemoryLayout.GuestUserAddressStart)
            {
                Assert.Equal(GuestMemoryLayout.GuestPrimaryUserAddressSize, size);
                return reservePrimaryUserRange ? address : 0;
            }

            Assert.Equal(GuestMemoryLayout.GuestExtendedAddressStart, address);
            Assert.Equal(GuestMemoryLayout.GuestExtendedAddressSize, size);
            return reserveExtendedRange ? address : 0;
        }

        public bool SplitHole(ulong address, ulong size) => false;

        public bool JoinHoles(ulong address, ulong size) => false;

        public bool FreeHole(ulong address, ulong size) => false;

        public bool TryMapView(
            HostBackingObject backing,
            ulong address,
            ulong offset,
            ulong size,
            HostPageProtection protection,
            out HostViewFailure failure)
        {
            failure = HostViewFailure.FixedMapFailed;
            return false;
        }

        public bool UnmapView(ulong address, ulong size) => false;

        public bool CommitPrivate(ulong address, ulong size, HostPageProtection protection) => false;

        public bool ReleasePrivate(ulong address, ulong size) => false;

        public bool ChangeAccess(ulong address, ulong size, HostPageProtection protection) => false;

        public bool FreeOwnedRange(ulong address, ulong size)
        {
            Calls.Add(InitializationCall.FreeOwnedRange);
            return true;
        }
    }

    [Fact]
    public void LazyInitializationPublishesPrimaryUserReservationBeforeBackingOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = new InitializationHostViews(reservePrimaryUserRange: true);
        using var owner = new GuestSpaceOwner(host, BackingSize, GuestVirtualAddressPlacement.Lazy);
        var address = GuestMemoryLayout.GuestUserAddressStart;
        var size = GuestMemoryLayout.GuestPrimaryUserAddressLimit - address;

        Assert.Equal(
            new[]
            {
                InitializationCall.ReserveHole,
                InitializationCall.ReserveHole,
                InitializationCall.CreateBacking,
            },
            host.Calls);
        Assert.True(owner.OwnsReservedRange(address, size));
        Assert.True(owner.ContainsFreeRange(address, size));
        Assert.True(owner.OwnsReservedRange(
            GuestMemoryLayout.GuestExtendedAddressStart,
            GuestMemoryLayout.GuestExtendedAddressSize));
        Assert.True(owner.ContainsFreeRange(
            GuestMemoryLayout.GuestExtendedAddressStart,
            GuestMemoryLayout.GuestExtendedAddressSize));
    }

    [Fact]
    public void LazyInitializationContinuesWhenPrimaryUserReservationFailsOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var originalFatal = GuestSpaceOwner.OnFatal;
        var fatalMessages = new List<string>();
        GuestSpaceOwner.OnFatal = fatalMessages.Add;
        try
        {
            var host = new InitializationHostViews(reservePrimaryUserRange: false);
            using var owner = new GuestSpaceOwner(host, BackingSize, GuestVirtualAddressPlacement.Lazy);
            var address = GuestMemoryLayout.GuestUserAddressStart;
            var size = GuestMemoryLayout.GuestPrimaryUserAddressLimit - address;

            Assert.Equal(
                new[]
                {
                    InitializationCall.ReserveHole,
                    InitializationCall.ReserveHole,
                    InitializationCall.CreateBacking,
                },
                host.Calls);
            Assert.Empty(fatalMessages);
            Assert.False(owner.OwnsReservedRange(address, size));
            Assert.False(owner.ContainsFreeRange(address, size));
            Assert.True(owner.OwnsReservedRange(
                GuestMemoryLayout.GuestExtendedAddressStart,
                GuestMemoryLayout.GuestExtendedAddressSize));
        }
        finally
        {
            GuestSpaceOwner.OnFatal = originalFatal;
        }
    }

    [Fact]
    public void CanonicalInitializationRequiresPrimaryUserReservationOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var originalFatal = GuestSpaceOwner.OnFatal;
        var fatalMessages = new List<string>();
        GuestSpaceOwner.OnFatal = fatalMessages.Add;
        try
        {
            var host = new InitializationHostViews(reservePrimaryUserRange: false);
            using var owner = new GuestSpaceOwner(host, BackingSize, GuestVirtualAddressPlacement.Canonical);

            Assert.Equal(
                new[]
                {
                    InitializationCall.ReserveHole,
                    InitializationCall.ReserveHole,
                    InitializationCall.CreateBacking,
                },
                host.Calls);
            Assert.Single(fatalMessages);
            Assert.Contains("primary guest user address range", fatalMessages[0]);
            Assert.DoesNotContain(InitializationCall.ReserveFreeRegions, host.Calls);
        }
        finally
        {
            GuestSpaceOwner.OnFatal = originalFatal;
        }
    }

    [Fact]
    public void ExplicitNoReservationConstructorDoesNotPreclaimPrimaryUserRange()
    {
        var host = new InitializationHostViews(reservePrimaryUserRange: true);
        using var owner = new GuestSpaceOwner(host, BackingSize, 0, 0, 0);

        Assert.Equal(new[] { InitializationCall.CreateBacking }, host.Calls);
    }

}
