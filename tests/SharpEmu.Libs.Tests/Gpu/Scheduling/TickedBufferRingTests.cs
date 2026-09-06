// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class TickedBufferRingTests
{
    [Fact]
    public void GrowsByFourReusesRetiredBuffersAndWrapsTheHint()
    {
        var device = new FakeTickDevice();
        var timeline = new TickTimeline(device);
        var ring = new TickedBufferRing(device, timeline);

        var first = Enumerable.Range(0, 4).Select(_ => device.Name(ring.AcquireBuffer())).ToArray();
        Assert.Equal(new[] { "b0", "b1", "b2", "b3" }, first);
        Assert.Equal(new[] { "read", "allocate 4" }, device.Log);

        Assert.Equal("b4", device.Name(ring.AcquireBuffer()));
        Assert.Equal(new[] { "read", "allocate 4", "read", "allocate 4" }, device.Log);
        Assert.Equal(8, ring.Count);

        Assert.Equal(new[] { "b5", "b6", "b7" }, Enumerable.Range(0, 3).Select(_ => device.Name(ring.AcquireBuffer())));
        Assert.Equal(8, ring.Count);

        Assert.Equal(1UL, timeline.ReserveTick());
        device.Complete(1);
        Assert.Equal("b0", device.Name(ring.AcquireBuffer()));
        Assert.Equal(8, ring.Count);
        Assert.Equal("read", device.Log[^1]);

        Assert.Equal(new[] { "b1", "b2", "b3", "b4", "b5", "b6", "b7" }, Enumerable.Range(0, 7).Select(_ => device.Name(ring.AcquireBuffer())));
        Assert.Equal("b8", device.Name(ring.AcquireBuffer()));
        Assert.Equal(12, ring.Count);
    }

    [Fact]
    public void SearchesFromTheHintBeforeWrapping()
    {
        var device = new FakeTickDevice();
        var timeline = new TickTimeline(device);
        var ring = new TickedBufferRing(device, timeline);

        Assert.Equal("b0", device.Name(ring.AcquireBuffer()));
        Assert.Equal(1UL, timeline.ReserveTick());
        Assert.Equal("b1", device.Name(ring.AcquireBuffer()));
        device.Complete(1);
        timeline.RefreshCompletedTick();

        Assert.Equal("b2", device.Name(ring.AcquireBuffer()));
        Assert.Equal("b3", device.Name(ring.AcquireBuffer()));
        Assert.Equal("b0", device.Name(ring.AcquireBuffer()));
    }
}
