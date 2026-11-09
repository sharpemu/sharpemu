// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class TickTimelineTests
{
    private readonly FakeTickDevice _device = new();

    [Fact]
    public void StartsAtTickOneAndHandsTicksOutInOrder()
    {
        var timeline = new TickTimeline(_device);

        Assert.Equal(1UL, timeline.CurrentTick);
        Assert.Equal(0UL, timeline.CompletedTick);
        Assert.True(timeline.IsTickComplete(0));
        Assert.False(timeline.IsTickComplete(1));
        Assert.Equal(1UL, timeline.ReserveTick());
        Assert.Equal(2UL, timeline.ReserveTick());
        Assert.Equal(3UL, timeline.CurrentTick);
        Assert.Equal(_device.TimelineHandle, timeline.Handle);
    }

    [Fact]
    public void RefreshNeverMovesTheKnownTickBackward()
    {
        var timeline = new TickTimeline(_device);
        _device.Complete(5);
        timeline.RefreshCompletedTick();
        Assert.Equal(5UL, timeline.CompletedTick);

        _device.Rewind(3);
        timeline.RefreshCompletedTick();
        Assert.Equal(5UL, timeline.CompletedTick);

        _device.Complete(6);
        var workers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                timeline.RefreshCompletedTick();
            }
        })).ToArray();
        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        Assert.Equal(6UL, timeline.CompletedTick);
    }

    [Fact]
    public async Task WaitReturnsEarlyWhenKnownOrAfterRefreshAndOtherwiseWaitsOnTheDevice()
    {
        var timeline = new TickTimeline(_device);
        _device.Complete(2);
        timeline.RefreshCompletedTick();
        var reads = _device.Log.Length;

        timeline.Wait(1);
        Assert.Equal(reads, _device.Log.Length);

        _device.Complete(3);
        timeline.Wait(3);
        Assert.Equal(new[] { "read" }, _device.Log.Skip(reads));
        Assert.DoesNotContain("wait 3", _device.Log);

        var waiter = Task.Run(() => timeline.Wait(4));
        Assert.True(SchedulingTestSupport.WaitUntil(() => _device.Log.Contains("wait 4")));
        Assert.False(await SchedulingTestSupport.CompletesWithin(waiter, 50));
        _device.Complete(4);
        Assert.True(await SchedulingTestSupport.CompletesWithin(waiter, 5000));
        Assert.Equal(4UL, timeline.CompletedTick);
    }

    [Fact]
    public void WaitReportsADeviceFailureAsFatal()
    {
        using var fatal = new FatalScope();
        var timeline = new TickTimeline(_device);
        _device.FailWait = true;

        Assert.Throws<SchedulerFatalException>(() => timeline.Wait(1));
        Assert.Equal("vkWaitSemaphores failed: VK_ERROR_DEVICE_LOST, tick=1", fatal.Messages.Single());
    }
}
