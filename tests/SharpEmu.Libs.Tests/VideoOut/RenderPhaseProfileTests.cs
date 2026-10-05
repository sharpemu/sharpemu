// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class RenderPhaseProfileTests
{
    [Fact]
    public void DisabledDetailMeasurement_DoesNotAllocateOnANewThread()
    {
        if (RenderPhaseProfile.Enabled) return;

        long allocated = -1;
        var thread = new Thread(() =>
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTracking))
            {
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DetailMeasurement_PreservesTheOuterScope()
    {
        Assert.False(RenderPhaseProfile.DetailMeasurementsEnabled);
        using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.Draw))
        {
            Assert.Equal(RenderPhaseProfile.Enabled, RenderPhaseProfile.DetailMeasurementsEnabled);
            using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ImageTracking))
                Assert.Equal(RenderPhaseProfile.Enabled, RenderPhaseProfile.DetailMeasurementsEnabled);
            Assert.Equal(RenderPhaseProfile.Enabled, RenderPhaseProfile.DetailMeasurementsEnabled);
        }
        Assert.False(RenderPhaseProfile.DetailMeasurementsEnabled);
    }
}
