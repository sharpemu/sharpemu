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
    public void WorkerPoolsOnlyFreeCompletedHandles()
    {
        var device = new FakeTickDevice { CompleteOnSubmit = true };
        var rendering = new RecordingRenderingState();
        using var scheduler = new SubmissionScheduler(device, rendering);
        var workers = new WorkerCommandPools(scheduler, rendering);
        var spare = new System.Collections.Concurrent.ConcurrentBag<RecordingBuffer>();
        var a = workers.Take(spare, 1000);
        var b = workers.Take(spare, 1000);
        Assert.Same(a.Source, b.Source);
        Assert.DoesNotContain("release pool", device.Log);
        var first = a.Ring.AcquireWorkerHandle();
        a.Ring.Submitted(first, 3);
        var second = a.Ring.AcquireWorkerHandle();
        Assert.NotEqual(first, second);
        a.Ring.Submitted(second, 0);
        Assert.Equal(second, a.Ring.AcquireWorkerHandle());
        a.Ring.Submitted(second, 0);
        Assert.Equal(2, a.Ring.Count);
        Assert.Equal(1, a.Ring.TrimIdle(long.MaxValue));
        Assert.Equal(0, a.Ring.TrimIdle(long.MaxValue));
        Assert.DoesNotContain($"free {device.Name(first)}", device.Log);
        spare.Add(a);
        spare.Add(b);
        device.Complete(2);
        workers.Sweep(spare, 7000);
        Assert.DoesNotContain("release pool", device.Log);
        Assert.DoesNotContain($"free {device.Name(first)}", device.Log);
        device.Complete(3);
        workers.Sweep(spare, 8000);
        Assert.False(workers.NeedsSweep);
        Assert.Contains($"free {device.Name(first)}", device.Log);
        Assert.Single(device.Log, entry => entry == "release pool");
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
