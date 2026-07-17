// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed unsafe class AgcGuestImageDirtyRefreshTests
{
    private const nuint PageSize = 4096;

    [Fact]
    public void CleanAvailableGuestImageKeepsEmptyPixelShortcut()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = AllocateTrackedPage(out var address);
        try
        {
            Assert.True(AgcExports.TryUseAvailableGuestImageWithoutSnapshot(
                address,
                guestImageAvailable: true,
                out var dirtySnapshotClaimed));
            Assert.False(dirtySnapshotClaimed);
        }
        finally
        {
            ReleaseTrackedPage(page, address);
        }
    }

    [Fact]
    public void DirtyAvailableGuestImageClaimsOneSnapshotAndRearmsAfterSuccess()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = AllocateTrackedPage(out var address);
        try
        {
            GuestImageWriteTracker.NotifyManagedWrite(address + 64, 1);

            Assert.False(AgcExports.TryUseAvailableGuestImageWithoutSnapshot(
                address,
                guestImageAvailable: true,
                out var dirtySnapshotClaimed));
            Assert.True(dirtySnapshotClaimed);
            Assert.False(GuestImageWriteTracker.PeekDirty(address));

            AgcExports.CompleteGuestImageSnapshot(address, succeeded: true);
            Assert.True(AgcExports.TryUseAvailableGuestImageWithoutSnapshot(
                address,
                guestImageAvailable: true,
                out var duplicateClaim));
            Assert.False(duplicateClaim);

            GuestImageWriteTracker.NotifyManagedWrite(address + 128, 1);

            Assert.True(GuestImageWriteTracker.PeekDirty(address));
        }
        finally
        {
            ReleaseTrackedPage(page, address);
        }
    }

    [Fact]
    public void FailedDirtySnapshotRemainsDirtyForRetry()
    {
        if (!GuestImageWriteTracker.Enabled)
        {
            return;
        }

        var page = AllocateTrackedPage(out var address);
        try
        {
            GuestImageWriteTracker.NotifyManagedWrite(address + 64, 1);
            Assert.False(AgcExports.TryUseAvailableGuestImageWithoutSnapshot(
                address,
                guestImageAvailable: true,
                out var firstClaim));
            Assert.True(firstClaim);

            AgcExports.CompleteGuestImageSnapshot(address, succeeded: false);

            Assert.True(GuestImageWriteTracker.PeekDirty(address));
            Assert.False(AgcExports.TryUseAvailableGuestImageWithoutSnapshot(
                address,
                guestImageAvailable: true,
                out var retryClaim));
            Assert.True(retryClaim);
            AgcExports.CompleteGuestImageSnapshot(address, succeeded: true);
        }
        finally
        {
            ReleaseTrackedPage(page, address);
        }
    }

    private static byte* AllocateTrackedPage(out ulong address)
    {
        var page = (byte*)NativeMemory.AlignedAlloc(PageSize, PageSize);
        Assert.NotEqual(nint.Zero, (nint)page);
        new Span<byte>(page, checked((int)PageSize)).Clear();
        address = (ulong)page;
        GuestImageWriteTracker.Track(address, checked((ulong)PageSize));
        return page;
    }

    private static void ReleaseTrackedPage(byte* page, ulong address)
    {
        GuestImageWriteTracker.Untrack(address);
        NativeMemory.AlignedFree(page);
    }
}
