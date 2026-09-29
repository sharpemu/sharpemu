// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class MeshDrawTraceTests
{
    [Fact]
    public void RepeatedStateUsesPowerOfTwoCounts()
    {
        var window = new MeshDrawTrace.EventWindow(8, 8, 32);
        var emitted = Enumerable.Range(1, 8).Where(_ => window.Record("same", "state").Emit).ToArray();
        Assert.Equal(new[] { 1, 2, 4, 8 }, emitted);
    }

    [Fact]
    public void LimitsRetainTheNewestHistoryAfterOutputStops()
    {
        var window = new MeshDrawTrace.EventWindow(2, 2, 3);
        Assert.True(window.Record("a", "first").Emit);
        Assert.True(window.Record("b", "second").Emit);
        Assert.True(window.Record("c", "third").LimitReached);
        Assert.False(window.Record("d", "fourth").Emit);
        Assert.Equal(2, window.KeyCount);
        Assert.Equal(new[] { "third occurrence=0", "fourth occurrence=0" }, window.Recent);
    }

    [Fact]
    public void RangeComparisonHandlesBoundariesWithoutAddressOverflow()
    {
        Assert.False(MeshDrawTrace.Overlaps(10, 5, 15, 3));
        Assert.True(MeshDrawTrace.Overlaps(10, 5, 14, 3));
        Assert.False(MeshDrawTrace.Overlaps(10, 0, 10, 3));
        Assert.True(MeshDrawTrace.Overlaps(ulong.MaxValue - 2, 3, ulong.MaxValue, 1));
        Assert.False(MeshDrawTrace.Overlaps(ulong.MaxValue - 2, 3, 0, 1));
    }
}
