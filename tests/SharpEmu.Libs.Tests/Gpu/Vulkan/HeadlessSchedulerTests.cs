// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Memory.GpuMemory;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

[Collection(SchedulingStateCollection.Name)]
public sealed class HeadlessSchedulerTests : IClassFixture<HeadlessVulkanFixture>
{
    // Models the presenter loop: relay commands first, then one unit of guest work.
    private sealed class GpuWorker : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _guestWork = new();
        private readonly Thread _thread;
        private bool _stop;

        public GpuWorker(HeadlessVulkan vulkan)
        {
            Relay = new GpuWorkerRelay(Wake);
            Device = new LoggingTickDevice(vulkan.NewTickDevice());
            Scheduler = new SubmissionScheduler(Device, new RecordingRenderingState());
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public GpuWorkerRelay Relay { get; }

        public LoggingTickDevice Device { get; }

        public SubmissionScheduler Scheduler { get; }

        public int ThreadId => _thread.ManagedThreadId;

        public void Post(Action work)
        {
            lock (_gate)
            {
                _guestWork.Enqueue(work);
                Monitor.PulseAll(_gate);
            }
        }

        public void RunOnWorker(Action work)
        {
            using var done = new ManualResetEventSlim();
            Post(() =>
            {
                work();
                done.Set();
            });
            done.Wait();
        }

        // Stop the scheduler on the worker before detaching it.
        public void Close(GuestGpuMemory? memory)
        {
            Relay.StopAcceptingWork();
            Relay.RunPendingCommands();
            if (Scheduler.Active)
            {
                Scheduler.Finish();
                Scheduler.WaitForAllPriorityOperations();
            }

            Scheduler.Shutdown();
            memory?.AttachGpuQueue(null, null);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stop = true;
                Monitor.PulseAll(_gate);
            }

            _thread.Join();
            Scheduler.Dispose();
        }

        private void Wake()
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }

        private void Run()
        {
            Relay.BindCurrentThread();
            for (;;)
            {
                Action? guest;
                lock (_gate)
                {
                    while (!_stop && !Relay.HasPendingCommands && _guestWork.Count == 0)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_stop)
                    {
                        return;
                    }

                    Relay.RunPendingCommands();
                    _guestWork.TryDequeue(out guest);
                }

                guest?.Invoke();
            }
        }
    }

    // Records, per store call, the thread and how far the GPU was known to be.
    private sealed class ObservingStores : IGuestBufferStore, IGuestImageStore
    {
        public SubmissionScheduler? Scheduler { get; set; }

        public List<(int Thread, ulong CompletedTick)> Calls { get; } = new();

        public bool MarkCpuWrite(ulong address, ulong size)
        {
            lock (Calls)
            {
                Calls.Add((Environment.CurrentManagedThreadId, Scheduler?.Timeline.CompletedTick ?? 0));
            }

            return false;
        }

        public bool DownloadToCpu(ulong address, ulong size) => false;

        public void Unregister(ulong address, ulong size)
        {
        }
    }

    private readonly HeadlessVulkan? _vulkan;

    public HeadlessSchedulerTests(HeadlessVulkanFixture fixture, Xunit.Abstractions.ITestOutputHelper output)
    {
        _vulkan = fixture.Vulkan;
        output.WriteLine(_vulkan is null ? "headless vulkan: unavailable, tests skipped" : $"headless vulkan: {_vulkan.DeviceName}");
    }

    [Fact]
    public void SubmitAdvancesTheTimelineAndWaitReturnsAfterRetirement()
    {
        if (_vulkan is null) return;
        var device = new LoggingTickDevice(_vulkan.NewTickDevice());
        using var scheduler = new SubmissionScheduler(device, new RecordingRenderingState());
        scheduler.Begin(new SubmissionContext());

        Assert.Equal(1UL, scheduler.Submit());
        scheduler.Wait(1);

        Assert.True(scheduler.IsTickComplete(1));
        Assert.Equal(1UL, _vulkan.ReadSemaphore(device.TimelineHandle));
        scheduler.BeginCommand();
        Assert.Equal(2UL, scheduler.Flush());
        scheduler.FlushAndWait();
        Assert.Equal(3UL, scheduler.Timeline.CompletedTick);
        Assert.Equal(4UL, scheduler.CurrentTick);
    }

    [Fact]
    public void UrgentWorkRunsOnThePriorityThreadAfterRetirementAndDeferredWorkFollows()
    {
        if (_vulkan is null) return;
        using var scheduler = new SubmissionScheduler(_vulkan.NewTickDevice(), new RecordingRenderingState());
        scheduler.Begin(new SubmissionContext());
        var order = new List<string>();
        var urgentThread = 0;
        scheduler.QueuePriorityCompletionAction(() =>
        {
            urgentThread = Environment.CurrentManagedThreadId;
            lock (order)
            {
                order.Add("urgent");
            }
        });
        scheduler.QueueCompletionAction(() => order.Add("deferred"));

        scheduler.Submit();

        Assert.True(WaitUntil(() => order.Count == 1));
        Assert.NotEqual(Environment.CurrentManagedThreadId, urgentThread);
        scheduler.Wait(1);
        scheduler.RunCompletedOperations();
        Assert.Equal(new[] { "urgent", "deferred" }, order);
    }

    [Fact]
    public void ShutdownWithInFlightWorkSubmitsTheOpenBufferAndReleasesTheDevice()
    {
        if (_vulkan is null) return;
        var device = new LoggingTickDevice(_vulkan.NewTickDevice());
        var scheduler = new SubmissionScheduler(device, new RecordingRenderingState());
        scheduler.Begin(new SubmissionContext());
        scheduler.Flush();
        scheduler.Flush();
        scheduler.Flush();
        var ran = false;
        scheduler.QueueCompletionAction(() => ran = true);

        scheduler.Dispose();

        Assert.True(ran);
        Assert.False(scheduler.PriorityWorkerAlive);
        Assert.Equal(new[] { "submit 1", "submit 2", "submit 3", "submit 4" }, device.Log.Where(e => e.StartsWith("submit", StringComparison.Ordinal)));
    }

    [Fact]
    public void TwoSchedulersShareTheQueueWithIndependentTimelines()
    {
        if (_vulkan is null) return;
        var rendererDevice = new LoggingTickDevice(_vulkan.NewTickDevice());
        var presentDevice = new LoggingTickDevice(_vulkan.NewTickDevice());
        using var renderer = new SubmissionScheduler(rendererDevice, new RecordingRenderingState());
        using var present = new SubmissionScheduler(presentDevice, new RecordingRenderingState());
        renderer.Begin(new SubmissionContext());

        renderer.Flush();
        present.BeginCommand();
        Assert.Equal(1UL, present.Submit());
        Assert.Equal(2UL, renderer.Submit());
        renderer.Wait(2);
        present.Wait(1);

        Assert.Equal(2UL, _vulkan.ReadSemaphore(rendererDevice.TimelineHandle));
        Assert.Equal(1UL, _vulkan.ReadSemaphore(presentDevice.TimelineHandle));
        Assert.False(present.Active);
        Assert.NotEqual(rendererDevice.TimelineHandle, presentDevice.TimelineHandle);
    }

    [Fact]
    public void BundleWaitsOnAnEarlierSignalOfAnotherTimeline()
    {
        if (_vulkan is null) return;
        using var scheduler = new SubmissionScheduler(_vulkan.NewTickDevice(), new RecordingRenderingState());
        scheduler.Begin(new SubmissionContext());
        var label = _vulkan.CreateTimelineSemaphore();
        try
        {
            var signal = new SubmitBundle();
            signal.AddSignal(label, 5);
            scheduler.Flush(signal);
            var wait = new SubmitBundle();
            wait.AddWait(label, 5);
            var tick = scheduler.Flush(wait);
            scheduler.Wait(tick);

            Assert.Equal(5UL, _vulkan.ReadSemaphore(label));
            Assert.Equal((1, 0), (wait.WaitCount, wait.SignalCount));
        }
        finally
        {
            _vulkan.DestroySemaphore(label);
        }
    }

    [Fact]
    public void RelayRunsSynchronousCallsOnTheWorkerAndInlineFromIt()
    {
        if (_vulkan is null) return;
        using var worker = new GpuWorker(_vulkan);

        var callThread = 0;
        worker.Relay.RunOnGpuQueue(() => callThread = Environment.CurrentManagedThreadId);
        Assert.Equal(worker.ThreadId, callThread);

        var inlineThread = 0;
        worker.RunOnWorker(() => worker.Relay.RunOnGpuQueue(() => inlineThread = Environment.CurrentManagedThreadId));
        Assert.Equal(worker.ThreadId, inlineThread);
        Assert.False(worker.Relay.HasPendingCommands);
    }

    [Fact]
    public void PlainRelayCallbackDoesNotWaitForGpuRetirement()
    {
        if (_vulkan is null) return;
        using var worker = new GpuWorker(_vulkan);
        worker.RunOnWorker(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            worker.Scheduler.Submit();
        });

        var ran = false;
        worker.Relay.RunOnGpuQueue(() => ran = true);

        Assert.True(ran);
        Assert.Equal(new[] { "submit 1" }, worker.Device.Log);
    }

    [Fact]
    public void UnmapThroughTheRelaySubmitsTheOpenBufferWaitsItsTickAndCompletesUrgentWorkFirst()
    {
        if (_vulkan is null) return;
        using var worker = new GpuWorker(_vulkan);
        var stores = new ObservingStores { Scheduler = worker.Scheduler };
        using var memory = new GuestGpuMemory(new RecordingAddressSpace(), stores, stores);
        memory.Register(0x10000, 0x1000);
        memory.AttachGpuQueue(worker.Relay, worker.Scheduler);
        var order = new List<string>();
        worker.RunOnWorker(() =>
        {
            worker.Scheduler.Begin(new SubmissionContext());
            worker.Scheduler.QueuePriorityCompletionAction(() =>
            {
                lock (order)
                {
                    order.Add("urgent");
                }
            });
        });
        Assert.DoesNotContain(worker.Device.Log, entry => entry.StartsWith("submit", StringComparison.Ordinal));

        memory.Unregister(0x10000, 0x1000);

        Assert.Contains("submit 1", worker.Device.Log);
        Assert.Equal(new[] { "urgent" }, order);
        var call = Assert.Single(stores.Calls);
        Assert.Equal(worker.ThreadId, call.Thread);
        Assert.True(call.CompletedTick >= 1);
        Assert.Equal(2UL, worker.Scheduler.CurrentTick);
        Assert.False(memory.Covers(0x10000, 0x1000));
        memory.AttachGpuQueue(null, null);
    }

    [Fact]
    public void UnmapRacingClosureNeverReleasesBeforeRetirement()
    {
        if (_vulkan is null) return;
        using var fatal = new FatalScope();
        var onWorker = 0;
        var afterDetach = 0;
        for (var iteration = 0; iteration < 12; iteration++)
        {
            using var worker = new GpuWorker(_vulkan);
            var stores = new ObservingStores { Scheduler = worker.Scheduler };
            using var memory = new GuestGpuMemory(new RecordingAddressSpace(), stores, stores);
            memory.Register(0x10000, 0x1000);
            memory.AttachGpuQueue(worker.Relay, worker.Scheduler);
            worker.RunOnWorker(() =>
            {
                worker.Scheduler.Begin(new SubmissionContext());
                worker.Scheduler.Submit();
            });

            using var closed = new ManualResetEventSlim();
            worker.Post(() =>
            {
                worker.Close(memory);
                closed.Set();
            });
            memory.Unregister(0x10000, 0x1000);
            closed.Wait();

            var call = Assert.Single(stores.Calls);
            Assert.True(call.CompletedTick >= 1, $"iteration {iteration}: release before retirement");
            if (call.Thread == worker.ThreadId)
            {
                onWorker++;
            }
            else
            {
                Assert.Equal(Environment.CurrentManagedThreadId, call.Thread);
                afterDetach++;
            }

            Assert.False(memory.Covers(0x10000, 0x1000));
        }

        Assert.Equal(12, onWorker + afterDetach);
        Assert.Empty(fatal.Messages);
    }
}
