// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanFeedbackSnapshotTelemetryTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void EnablesPoolUnlessExplicitlyDisabled(string? value, bool expected)
    {
        Assert.Equal(expected, VulkanFeedbackSnapshotPoolPolicy.IsEnabled(value));
    }

    [Fact]
    public void TracksAllocationCopyAndRetirementTotals()
    {
        var telemetry = new VulkanFeedbackSnapshotTelemetry();

        telemetry.RecordAcquired(VulkanFeedbackSnapshotKind.Color, 1024, allocated: true);
        telemetry.RecordAcquired(VulkanFeedbackSnapshotKind.Depth, 2048, allocated: true);
        telemetry.RecordCopy(800);
        telemetry.RecordCopy(400);
        telemetry.RecordRetired(1024, returnedToPool: true);

        var statistics = telemetry.GetStatistics();
        Assert.Equal(2, statistics.Created);
        Assert.Equal(1, statistics.ColorCreated);
        Assert.Equal(1, statistics.DepthCreated);
        Assert.Equal(2, statistics.Allocations);
        Assert.Equal(3072UL, statistics.AllocatedBytes);
        Assert.Equal(0, statistics.PoolHits);
        Assert.Equal(2, statistics.Copies);
        Assert.Equal(1200UL, statistics.CopiedBytes);
        Assert.Equal(1, statistics.Retired);
        Assert.Equal(1, statistics.PoolReturns);
        Assert.Equal(0, statistics.Destroyed);
        Assert.Equal(1, statistics.Live);
        Assert.Equal(2048UL, statistics.LiveBytes);
        Assert.Equal(2, statistics.PeakLive);
        Assert.Equal(3072UL, statistics.PeakLiveBytes);
    }

    [Fact]
    public void TracksPoolReuseWithoutAnotherAllocation()
    {
        var telemetry = new VulkanFeedbackSnapshotTelemetry();

        telemetry.RecordAcquired(VulkanFeedbackSnapshotKind.Depth, 2048, allocated: true);
        telemetry.RecordRetired(2048, returnedToPool: true);
        telemetry.RecordAcquired(VulkanFeedbackSnapshotKind.Depth, 2048, allocated: false);

        var statistics = telemetry.GetStatistics();
        Assert.Equal(2, statistics.Created);
        Assert.Equal(1, statistics.Allocations);
        Assert.Equal(1, statistics.PoolHits);
        Assert.Equal(1, statistics.PoolReturns);
        Assert.Equal(1, statistics.Live);
        Assert.Equal(2048UL, statistics.LiveBytes);
    }

    [Theory]
    [InlineData(0, 0, 32, 2, 256, true)]
    [InlineData(1, 32, 32, 2, 256, true)]
    [InlineData(2, 64, 32, 2, 256, false)]
    [InlineData(0, 240, 32, 2, 256, false)]
    [InlineData(0, 0, 300, 2, 256, false)]
    public void BoundsPoolByKeyAndTotalBytes(
        int entriesForKey,
        ulong pooledBytes,
        ulong allocationBytes,
        int maxEntriesPerKey,
        ulong maxBytes,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanFeedbackSnapshotPoolPolicy.CanStore(
                entriesForKey,
                pooledBytes,
                allocationBytes,
                maxEntriesPerKey,
                maxBytes));
    }

    [Theory]
    [InlineData(Format.D16Unorm, 2560, 1440, 7372800)]
    [InlineData(Format.D32Sfloat, 2560, 1440, 14745600)]
    [InlineData(Format.R8G8B8A8Unorm, 1920, 1080, 1234)]
    public void ResolvesDepthCopyByteCounts(
        Format format,
        uint width,
        uint height,
        ulong expected)
    {
        var estimated = format == Format.R8G8B8A8Unorm ? 1234UL : 0UL;
        Assert.Equal(
            expected,
            VulkanFeedbackSnapshotPoolPolicy.ResolveCopyByteCount(
                format,
                width,
                height,
                estimated));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(8, true)]
    [InlineData(15, false)]
    [InlineData(16, true)]
    public void ReportsOnlyBoundedCreationCounts(long created, bool expected)
    {
        Assert.Equal(expected, VulkanFeedbackSnapshotTelemetry.ShouldReport(created));
    }
}
