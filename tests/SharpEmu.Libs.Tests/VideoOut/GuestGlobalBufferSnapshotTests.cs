// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GuestGlobalBufferSnapshotTests
{
    [Fact]
    public void ReadOnlyBufferUsesCapturedDrawEpoch()
    {
        Assert.True(VulkanVideoPresenter.ShouldRefreshGuestGlobalBuffer(
            writable: false,
            capturedMatchesShadow: false,
            liveMatchesShadow: true));

        Assert.False(VulkanVideoPresenter.ShouldRefreshGuestGlobalBuffer(
            writable: false,
            capturedMatchesShadow: true,
            liveMatchesShadow: false));
    }

    [Fact]
    public void WritableBufferUsesLiveGuestMemory()
    {
        Assert.True(VulkanVideoPresenter.ShouldRefreshGuestGlobalBuffer(
            writable: true,
            capturedMatchesShadow: true,
            liveMatchesShadow: false));

        Assert.False(VulkanVideoPresenter.ShouldRefreshGuestGlobalBuffer(
            writable: true,
            capturedMatchesShadow: false,
            liveMatchesShadow: true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReadOnlyBufferVersionsOutstandingAllocation(
        bool inFlight,
        bool openBatch)
    {
        Assert.True(VulkanVideoPresenter.ShouldVersionReadOnlyGuestGlobalBuffer(
            writable: false,
            needsRefresh: true,
            allocationInFlight: inFlight,
            allocationInOpenBatch: openBatch));

        Assert.False(VulkanVideoPresenter.ShouldVersionReadOnlyGuestGlobalBuffer(
            writable: true,
            needsRefresh: true,
            allocationInFlight: inFlight,
            allocationInOpenBatch: openBatch));
    }

    [Fact]
    public void UnchangedReadOnlyBufferReusesOutstandingAllocation()
    {
        Assert.False(VulkanVideoPresenter.ShouldVersionReadOnlyGuestGlobalBuffer(
            writable: false,
            needsRefresh: false,
            allocationInFlight: true,
            allocationInOpenBatch: true));
    }

    [Fact]
    public void ChangedReadOnlyBufferReusesRetiredAllocation()
    {
        Assert.False(VulkanVideoPresenter.ShouldVersionReadOnlyGuestGlobalBuffer(
            writable: false,
            needsRefresh: true,
            allocationInFlight: false,
            allocationInOpenBatch: false));
    }
}
