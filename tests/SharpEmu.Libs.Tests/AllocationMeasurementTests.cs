// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(AllocationMeasurementCollection.Name)]
public sealed class AllocationMeasurementTests
{
    [Fact]
    public void MeasurementDetectsAnAllocation()
    {
        var allocated = AllocationMeasurementCollection.Measure(() => GC.KeepAlive(new byte[32]));
        Assert.True(allocated >= 32);
    }

    [Fact]
    public void MeasurementSupportsAllocationsAboveOneMegabyte()
    {
        var allocated = AllocationMeasurementCollection.Measure(() => GC.KeepAlive(new byte[2 * 1024 * 1024]));
        Assert.True(allocated >= 2 * 1024 * 1024);
    }

    [Fact]
    public void CollectionInvalidatesMeasurement()
    {
        var previousMode = GCSettings.LatencyMode;
        var failure = Record.Exception(() => AllocationMeasurementCollection.Measure(() => GC.Collect()));
        Assert.NotNull(failure);
        Assert.Contains("Collection invalidated", failure.Message);
        Assert.Equal(previousMode, GCSettings.LatencyMode);
    }

    [Fact]
    public void FailedOperationAfterCollectionKeepsOriginalException()
    {
        var failure = new InvalidOperationException("Allocation measurement test.");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            AllocationMeasurementCollection.Measure(() =>
            {
                GC.Collect();
                throw failure;
            })));
    }

    [Fact]
    public void FailedOperationRestoresCollectionMode()
    {
        var previousMode = GCSettings.LatencyMode;
        var failure = new InvalidOperationException("Allocation measurement test.");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            AllocationMeasurementCollection.Measure(() => throw failure)));
        Assert.Equal(previousMode, GCSettings.LatencyMode);
    }
}
