// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Memory.GpuMemory;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class SubmissionSchedulerTests
{
    private readonly FakeTickDevice _device = new();
    private readonly RecordingRenderingState _rendering = new();

    [Fact]
    public void FirstSubmitSignalsTickOneAndFlushBeginsTheNextBuffer()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        Assert.True(scheduler.Active);
        Assert.False(scheduler.Current.IsInvalid);
        Assert.Equal(new[] { "read", "allocate 4", "begin b0" }, _device.Log);

        Assert.Equal(1UL, scheduler.Submit());

        Assert.Equal(2UL, scheduler.CurrentTick);
        Assert.True(scheduler.Current.IsInvalid);
        Assert.Equal(new[] { "end b0", "submit b0 tick=1" }, _device.Log.Skip(3));
        Assert.Equal((1UL, 0, 1), (_device.Submits[0].Tick, _device.Submits[0].Waits, _device.Submits[0].Signals));

        var bundle = new SubmitBundle();
        bundle.AddWait(0x77, 3);
        scheduler.BeginCommand();
        scheduler.Flush(bundle);
        Assert.Equal((2UL, 1, 1), (_device.Submits[1].Tick, _device.Submits[1].Waits, _device.Submits[1].Signals));
        Assert.Equal((1, 0), (bundle.WaitCount, bundle.SignalCount));

        scheduler.Flush(bundle);
        Assert.Equal((3UL, 1, 1), (_device.Submits[2].Tick, _device.Submits[2].Waits, _device.Submits[2].Signals));
        Assert.Equal((1, 0), (bundle.WaitCount, bundle.SignalCount));
        Assert.Equal("begin b3", _device.Log[^1]);
        _device.CompleteOnSubmit = true;
    }

    [Fact]
    public void SubmitRejectsAFullSignalListAndAMissingBuffer()
    {
        using var fatal = new FatalScope();
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var bundle = new SubmitBundle();
        bundle.AddSignal(1);
        bundle.AddSignal(2);
        bundle.AddSignal(3);
        Assert.Throws<SchedulerFatalException>(() => bundle.AddSignal(4));
        Assert.Throws<SchedulerFatalException>(() => scheduler.Submit(bundle));

        _device.CompleteOnSubmit = true;
        scheduler.Submit();
        Assert.Throws<SchedulerFatalException>(() => scheduler.Submit());
        Assert.Equal(3, fatal.Messages.Count);
    }

    [Fact]
    public void WaitOnTheCurrentTickSubmitsFirst()
    {
        using var fatal = new FatalScope();
        _device.CompleteOnSubmit = true;
        using var scheduler = NewActiveScheduler(_device, _rendering);

        scheduler.Wait(1);

        Assert.Equal(new[] { "end b0", "submit b0 tick=1", "read", "begin b1" }, _device.Log.Skip(3));
        Assert.Equal(2UL, scheduler.CurrentTick);

        scheduler.Wait(1);
        Assert.Equal(7, _device.Log.Length);
        Assert.Throws<SchedulerFatalException>(() => scheduler.Wait(5));
    }

    [Fact]
    public async Task UrgentWorkRunsAtRetirementAndDeferredWorkAfterSameTickUrgentWork()
    {
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var order = new List<string>();
        void Add(string entry)
        {
            lock (order)
            {
                order.Add(entry);
            }
        }

        scheduler.QueuePriorityCompletionAction(() => Add("urgent1"));
        scheduler.QueueCompletionAction(() => Add("deferred1"));
        scheduler.Submit();
        Assert.Empty(order);

        _device.Complete(1);
        Assert.True(WaitUntil(() => order.Count == 1));
        Assert.Equal(new[] { "urgent1" }, order);
        scheduler.RunCompletedOperations();
        Assert.Equal(new[] { "urgent1", "deferred1" }, order);

        using var gate = new ManualResetEventSlim();
        scheduler.BeginCommand();
        scheduler.QueuePriorityCompletionAction(() =>
        {
            Add("urgent2");
            gate.Wait();
            Add("urgent2_end");
        });
        scheduler.QueueCompletionAction(() => Add("deferred2"));
        scheduler.Submit();
        _device.Complete(2);
        Assert.True(WaitUntil(() => order.Contains("urgent2")));

        var pop = Task.Run(scheduler.RunCompletedOperations);
        Assert.False(await CompletesWithin(pop, 100));
        gate.Set();
        Assert.True(await CompletesWithin(pop, 5000));
        Assert.Equal(new[] { "urgent1", "deferred1", "urgent2", "urgent2_end", "deferred2" }, order);
    }

    [Fact]
    public void FinishSubmitsWaitsBeginsAgainAndPopsDeferredWork()
    {
        _device.CompleteOnSubmit = true;
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var ran = false;
        scheduler.QueueCompletionAction(() => ran = true);

        scheduler.Finish();

        Assert.True(ran);
        Assert.False(scheduler.Current.IsInvalid);
        Assert.Equal(new[] { "end b0", "submit b0 tick=1", "read", "begin b1", "read" }, _device.Log.Skip(3));
        Assert.True(scheduler.IsTickComplete(1));
    }

    [Fact]
    public async Task DeferredWorkWhileDrainingRunsInlineFromTheOwnerAndBlocksOtherThreadsUntilClosed()
    {
        _device.CompleteOnSubmit = true;
        var scheduler = NewActiveScheduler(_device, _rendering);
        var order = new List<string>();
        using var gate = new ManualResetEventSlim();
        scheduler.QueueCompletionAction(() =>
        {
            order.Add("op1");
            scheduler.QueueCompletionAction(() => order.Add("op2"));
            scheduler.QueuePriorityCompletionAction(() => order.Add("op2_urgent"));
            gate.Wait();
            order.Add("op1_end");
        });

        var shutdown = Task.Run(scheduler.Shutdown);
        Assert.True(WaitUntil(() => order.Count == 3));

        var other = Task.Run(() => scheduler.QueueCompletionAction(() => order.Add("op3")));
        Assert.False(await CompletesWithin(other, 100));
        gate.Set();
        Assert.True(await CompletesWithin(shutdown, 5000));
        Assert.True(await CompletesWithin(other, 5000));
        Assert.Equal(new[] { "op1", "op2", "op2_urgent", "op1_end", "op3" }, order);
        scheduler.Dispose();
    }

    [Fact]
    public void PriorityWaitsFromTheOwnCallbackAreFatalButAnotherSchedulerMayWait()
    {
        using var fatal = new FatalScope();
        _device.CompleteOnSubmit = true;
        using var scheduler = NewActiveScheduler(_device, _rendering);
        var inside = false;
        scheduler.QueueCompletionAction(() =>
        {
            inside = SubmissionScheduler.InDeferredOperation;
            scheduler.WaitForAllPriorityOperations();
        });
        scheduler.Submit();
        Assert.False(SubmissionScheduler.InDeferredOperation);

        Assert.Throws<SchedulerFatalException>(scheduler.RunCompletedOperations);
        Assert.True(inside);
        Assert.False(SubmissionScheduler.InDeferredOperation);
        Assert.Contains("own deferred callback", fatal.Messages.Single());

        var otherDevice = new FakeTickDevice { CompleteOnSubmit = true };
        using var other = NewActiveScheduler(otherDevice);
        var waited = false;
        other.QueueCompletionAction(() =>
        {
            scheduler.WaitForAllPriorityOperations();
            scheduler.WaitForPriorityOperations(1);
            waited = true;
        });
        other.Submit();
        other.RunCompletedOperations();
        Assert.True(waited);

        scheduler.BeginCommand();
        scheduler.QueueCompletionAction(() => scheduler.Shutdown());
        scheduler.Submit();
        Assert.Throws<SchedulerFatalException>(scheduler.RunCompletedOperations);
        Assert.Equal("Cannot stop the scheduler from its own deferred callback.", fatal.Messages[1]);
    }

    [Fact]
    public async Task ShutdownSubmitsTheOpenBufferJoinsTheWorkerAndIsIdempotent()
    {
        using var fatal = new FatalScope();
        _device.CompleteOnSubmit = true;
        var scheduler = NewActiveScheduler(_device, _rendering);
        var runs = 0;
        scheduler.QueueCompletionAction(() => Interlocked.Increment(ref runs));
        scheduler.QueuePriorityCompletionAction(() => Interlocked.Increment(ref runs));

        var concurrent = Task.WhenAll(Task.Run(scheduler.Shutdown), Task.Run(scheduler.Shutdown));
        scheduler.Shutdown();
        Assert.True(await CompletesWithin(concurrent, 5000));

        Assert.Equal(2, runs);
        Assert.Contains("submit b0 tick=1", _device.Log);
        Assert.False(scheduler.PriorityWorkerAlive);
        Assert.True(scheduler.Current.IsInvalid);
        scheduler.Shutdown();

        var late = false;
        scheduler.QueueCompletionAction(() => late = true);
        Assert.True(late);
        Assert.Throws<SchedulerFatalException>(() => scheduler.Begin(new SubmissionContext()));

        scheduler.Dispose();
        Assert.Equal("dispose", _device.Log[^1]);
    }

    [Fact]
    public void DisposeFromTheOwnCallbackDefersToTheOwnerAndReleasesTheDeviceOnce()
    {
        _device.CompleteOnSubmit = true;
        var scheduler = NewActiveScheduler(_device, _rendering);
        var disposedInsideCallback = false;
        scheduler.QueueCompletionAction(() =>
        {
            scheduler.Dispose();
            disposedInsideCallback = _device.Log.Contains("dispose");
        });

        scheduler.Shutdown();

        Assert.False(disposedInsideCallback);
        Assert.Equal(1, _device.Log.Count(entry => entry == "dispose"));
        Assert.Equal("dispose", _device.Log[^1]);
        scheduler.Dispose();
        Assert.Equal(1, _device.Log.Count(entry => entry == "dispose"));
    }

    [Fact]
    public async Task RepeatedAndConcurrentDisposeReleaseTheDeviceOnceAfterClosure()
    {
        _device.CompleteOnSubmit = true;
        var scheduler = NewActiveScheduler(_device, _rendering);
        using var gate = new ManualResetEventSlim();
        scheduler.QueueCompletionAction(() => gate.Wait());

        var disposals = Task.WhenAll(Task.Run(scheduler.Dispose), Task.Run(scheduler.Dispose), Task.Run(scheduler.Dispose));
        Assert.False(await CompletesWithin(disposals, 100));
        Assert.DoesNotContain("dispose", _device.Log);

        gate.Set();
        Assert.True(await CompletesWithin(disposals, 5000));
        scheduler.Dispose();
        Assert.Equal(1, _device.Log.Count(entry => entry == "dispose"));
        Assert.Equal("dispose", _device.Log[^1]);
    }

    [Fact]
    public void ShutdownBeforeActivationIsClean()
    {
        var scheduler = new SubmissionScheduler(_device, _rendering);
        Assert.False(scheduler.Active);

        scheduler.Dispose();

        Assert.Equal(new[] { "read", "dispose" }, _device.Log);
        Assert.Empty(_device.Submits);
        Assert.False(scheduler.PriorityWorkerAlive);
    }

    [Fact]
    public void SubmitFailureReportsTheDebugFields()
    {
        using var fatal = new FatalScope();
        using var scheduler = NewActiveScheduler(_device, _rendering);
        _device.FailSubmit = true;
        scheduler.Current.SetDebugInfo((uint)RecordedOperation.EopInterrupt, 99, 1, 2, 3, 4, 0xABCD);

        Assert.Throws<SchedulerFatalException>(() => scheduler.Submit());

        Assert.Equal(
            "vkQueueSubmit failed: VK_ERROR_DEVICE_LOST, tick=1 debug_op=4 debug_submit=99 args=1,2,3,4,0x000000000000ABCD",
            fatal.Messages.Single());
        _device.FailSubmit = false;
        _device.CompleteOnSubmit = true;
    }

    [Fact]
    public void TwoSchedulersKeepIndependentTicksAndOneMayRunWithoutActivation()
    {
        var otherDevice = new FakeTickDevice();
        using var renderer = NewActiveScheduler(_device, _rendering);
        using var present = new SubmissionScheduler(otherDevice, new RecordingRenderingState());

        renderer.Flush();
        Assert.Equal(2UL, renderer.Submit());
        present.BeginCommand();
        Assert.Equal(1UL, present.Submit());
        Assert.False(present.Active);

        _device.Complete(2);
        Assert.True(renderer.IsTickComplete(2));
        Assert.False(present.IsTickComplete(1));
        otherDevice.Complete(1);
        present.Wait(1);
        Assert.True(present.IsTickComplete(1));
        Assert.NotEqual(_device.TimelineHandle, otherDevice.TimelineHandle);
    }

    [Fact]
    public void EndClosesRenderingBeforeTheBufferEndsAndBeginRefusesAnOpenPass()
    {
        using var fatal = new FatalScope();
        _device.CompleteOnSubmit = true;
        using var scheduler = NewActiveScheduler(_device, _rendering);
        _rendering.IsRendering = true;

        scheduler.EndRendering();
        Assert.Equal(new[] { "end_rendering" }, _rendering.Log);

        _rendering.IsRendering = true;
        scheduler.Submit();
        Assert.Equal(new[] { "end_rendering", "end_rendering" }, _rendering.Log);
        Assert.False(_rendering.IsRendering);

        _rendering.IsRendering = true;
        Assert.Throws<SchedulerFatalException>(() => scheduler.BeginCommand());
        _rendering.IsRendering = false;
    }

    private sealed class OrderedStores : IGuestBufferStore, IGuestImageStore
    {
        public List<string> Order { get; } = new();

        public bool MarkCpuWrite(ulong address, ulong size)
        {
            lock (Order)
            {
                Order.Add("stores");
            }

            return false;
        }

        public bool DownloadToCpu(ulong address, ulong size) => false;

        public void Unregister(ulong address, ulong size)
        {
        }
    }

    private sealed class InlineRelay : IGpuQueueRelay
    {
        public bool IsGpuQueueThread => true;

        public void Post(Action work) => work();

        public void RunOnGpuQueue(Action work) => work();

        public bool TryRunOnGpuQueue(Action work)
        {
            work();
            return true;
        }
    }

    [Fact]
    public void UnregisterDrainsTheRealSchedulerBeforeTouchingTheStores()
    {
        var previous = PageGuard.OnFatal;
        var fatals = new List<string>();
        PageGuard.OnFatal = fatals.Add;
        try
        {
            _device.CompleteOnSubmit = true;
            using var scheduler = NewActiveScheduler(_device, _rendering);
            var stores = new OrderedStores();
            using var memory = new GuestGpuMemory(new RecordingAddressSpace(), stores, stores);
            memory.Register(0x10000, 0x1000);
            memory.AttachGpuQueue(new InlineRelay(), scheduler);
            scheduler.QueuePriorityCompletionAction(() =>
            {
                lock (stores.Order)
                {
                    stores.Order.Add("urgent");
                }
            });

            memory.Unregister(0x10000, 0x1000);

            Assert.Equal(new[] { "urgent", "stores" }, stores.Order);
            Assert.Contains("submit b0 tick=1", _device.Log);
            Assert.False(scheduler.Current.IsInvalid);
            Assert.False(memory.Covers(0x10000, 0x1000));

            memory.Register(0x20000, 0x1000);
            scheduler.QueueCompletionAction(() => memory.Unregister(0x20000, 0x1000));
            scheduler.Submit();
            scheduler.RunCompletedOperations();
            Assert.Single(fatals);
            Assert.True(memory.Covers(0x20000, 0x1000));
            memory.AttachGpuQueue(null, null);
            memory.Unregister(0x20000, 0x1000);
        }
        finally
        {
            PageGuard.OnFatal = previous;
        }
    }
}
