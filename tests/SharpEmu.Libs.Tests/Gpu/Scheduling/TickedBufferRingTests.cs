// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class TickedBufferRingTests
{
    [Fact]
    public void GrowsByFourAndReusesOnlySubmittedBuffersWhoseTickCompleted()
    {
        var device = new FakeTickDevice();
        var timeline = new TickTimeline(device);
        var ring = new TickedBufferRing(device, timeline);

        var first = Enumerable.Range(0, 4).Select(_ => ring.AcquireBuffer()).ToArray();
        Assert.Equal(new[] { "b0", "b1", "b2", "b3" }, first.Select(device.Name));
        Assert.Equal(new[] { "read", "allocate 4" }, device.Log);

        Assert.Equal("b4", device.Name(ring.AcquireBuffer()));
        Assert.Equal(new[] { "read", "allocate 4", "read", "allocate 4" }, device.Log);
        Assert.Equal(8, ring.Count);

        Assert.Equal(1UL, timeline.ReserveTick());
        ring.Submitted(first[0], 1);
        ring.Submitted(first[1], 2);
        device.Complete(1);
        Assert.Equal(new[] { "b5", "b6", "b7" }, Enumerable.Range(0, 3).Select(_ => device.Name(ring.AcquireBuffer())));
        Assert.Equal("b0", device.Name(ring.AcquireBuffer()));
        Assert.Equal(8, ring.Count);
        Assert.Equal("read", device.Log[^1]);

        Assert.Equal("b8", device.Name(ring.AcquireBuffer()));
        Assert.Equal(12, ring.Count);
        Assert.Equal(new[] { "b9", "b10", "b11" }, Enumerable.Range(0, 3).Select(_ => device.Name(ring.AcquireBuffer())));

        device.Complete(2);
        Assert.Equal("b1", device.Name(ring.AcquireBuffer()));
    }

    [Fact]
    public void SearchesFromTheHintBeforeWrapping()
    {
        var device = new FakeTickDevice();
        var timeline = new TickTimeline(device);
        var ring = new TickedBufferRing(device, timeline);

        var b0 = ring.AcquireBuffer();
        Assert.Equal("b0", device.Name(b0));
        Assert.Equal(1UL, timeline.ReserveTick());
        ring.Submitted(b0, 1);
        Assert.Equal("b1", device.Name(ring.AcquireBuffer()));
        device.Complete(1);
        timeline.RefreshCompletedTick();

        Assert.Equal("b2", device.Name(ring.AcquireBuffer()));
        Assert.Equal("b3", device.Name(ring.AcquireBuffer()));
        Assert.Equal("b0", device.Name(ring.AcquireBuffer()));
    }

    [Fact]
    public void SubmittingABufferThatIsNotInUseIsFatal()
    {
        using var fatal = new FatalScope();
        var device = new FakeTickDevice();
        var ring = new TickedBufferRing(device, new TickTimeline(device));
        var buffer = ring.AcquireBuffer();
        ring.Submitted(buffer, 1);
        Assert.Throws<SchedulerFatalException>(() => ring.Submitted(buffer, 2));
    }
}
