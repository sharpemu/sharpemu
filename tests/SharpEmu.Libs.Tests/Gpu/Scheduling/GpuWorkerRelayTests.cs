// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Scheduling.SchedulingTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class GpuWorkerRelayTests : IDisposable
{
    // Models the render loop: relay commands first, then one unit of guest work.
    private sealed class TestWorker : IDisposable
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _guestWork = new();
        private readonly Thread _thread;
        private bool _stop;

        public TestWorker(Func<Action, Action, bool>? tryEnqueueAfterPendingWork = null)
        {
            Relay = new GpuWorkerRelay(Wake, tryEnqueueAfterPendingWork);
            _thread = new Thread(Run) { IsBackground = true };
        }

        public GpuWorkerRelay Relay { get; }

        public int ThreadId => _thread.ManagedThreadId;

        public void Start() => _thread.Start();

        public void AddGuestWork(Action work)
        {
            lock (_gate)
            {
                _guestWork.Enqueue(work);
            }

            Wake();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stop = true;
                Monitor.PulseAll(_gate);
            }

            if (_thread.IsAlive)
            {
                _thread.Join();
            }
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

    private readonly TestWorker _worker = new();

    private readonly List<string> _order = new();

    public void Dispose() => _worker.Dispose();

    private void Note(string entry)
    {
        lock (_order)
        {
            _order.Add(entry);
        }
    }

    [Fact]
    public void PostRunsOnTheWorkerAndRunOnGpuQueueReturnsAfterTheWorkerRanIt()
    {
        _worker.Start();
        var relay = _worker.Relay;
        Assert.False(relay.IsGpuQueueThread);

        var postThread = 0;
        relay.Post(() => postThread = Environment.CurrentManagedThreadId);
        Assert.True(WaitUntil(() => postThread != 0));
        Assert.Equal(_worker.ThreadId, postThread);

        var syncThread = 0;
        var onWorker = false;
        relay.RunOnGpuQueue(() =>
        {
            syncThread = Environment.CurrentManagedThreadId;
            onWorker = relay.IsGpuQueueThread;
        });
        Assert.Equal(_worker.ThreadId, syncThread);
        Assert.True(onWorker);
        Assert.False(relay.HasPendingCommands);
    }

    [Fact]
    public void WorkerCallsRunInlineWithoutQueueing()
    {
        _worker.Start();
        var relay = _worker.Relay;

        relay.RunOnGpuQueue(() =>
        {
            Note("outer");
            relay.RunOnGpuQueue(() => Note("inner_sync"));
            relay.Post(() => Note("inner_post"));
            Note("outer_end");
        });

        Assert.Equal(new[] { "outer", "inner_sync", "inner_post", "outer_end" }, _order);
    }

    [Fact]
    public void PlainCallbackDoesNotWaitForGpuRetirement()
    {
        _worker.Start();
        var device = new FakeTickDevice();
        using var scheduler = NewActiveScheduler(device);
        scheduler.Submit();

        var retired = true;
        _worker.Relay.RunOnGpuQueue(() => retired = scheduler.IsTickComplete(1));

        Assert.False(retired);
        Assert.DoesNotContain(device.Log, entry => entry.StartsWith("wait", StringComparison.Ordinal));
        device.Complete(1);
    }

    [Fact]
    public void AfterPendingWork_BypassesTheCommandStreamBarrier()
    {
        var barrierCalled = false;
        using var worker = new TestWorker(
            (_, _) =>
            {
                barrierCalled = true;
                return true;
            });
        worker.Start();
        var workRan = false;
        var ranOnWorker = false;

        Assert.True(worker.Relay.TryRunAfterPendingWork(() =>
        {
            workRan = true;
            ranOnWorker = worker.Relay.IsGpuQueueThread;
        }));
        Assert.True(workRan);
        Assert.True(ranOnWorker);
        Assert.False(barrierCalled);
    }

    [Fact]
    public void AfterPendingWork_ReturnsFalseWhenTheWorkerIsClosed()
    {
        var barrierCalled = false;
        var relay = new GpuWorkerRelay(
            () => { },
            (_, _) =>
            {
                barrierCalled = true;
                return true;
            });
        relay.StopAcceptingWork();

        Assert.False(relay.TryRunAfterPendingWork(() => { }));
        Assert.False(barrierCalled);
    }

    [Fact]
    public void AfterPendingWork_RethrowsTheWorkExceptionAndKeepsTheWorkerAlive()
    {
        var barrierCalled = false;
        using var worker = new TestWorker(
            (_, _) =>
            {
                barrierCalled = true;
                return true;
            });
        worker.Start();
        var expected = new InvalidOperationException("mapping change failed");

        var failure = Record.Exception(() =>
            worker.Relay.TryRunAfterPendingWork(() => throw expected));

        Assert.Same(expected, failure);
        var ranAfterFailure = false;
        Assert.True(worker.Relay.TryRunAfterPendingWork(() => ranAfterFailure = true));
        Assert.True(ranAfterFailure);
        Assert.False(barrierCalled);
    }

    [Fact]
    public async Task AfterAcceptedCommandStreams_WaitsForTheControlBarrier()
    {
        Action? run = null;
        Action? cancel = null;
        var relay = new GpuWorkerRelay(
            () => { },
            (work, cancellation) =>
            {
                run = work;
                cancel = cancellation;
                return true;
            });
        var workRan = false;

        var completion = Task.Run(() =>
            relay.TryRunAfterAcceptedCommandStreams(() => workRan = true));
        Assert.True(WaitUntil(() => Volatile.Read(ref run) is not null));
        Assert.False(await CompletesWithin(completion, 50));

        run!();

        Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(workRan);
        Assert.NotNull(cancel);
    }

    [Fact]
    public async Task AfterAcceptedCommandStreams_CancellationReleasesTheCaller()
    {
        Action? cancel = null;
        var relay = new GpuWorkerRelay(
            () => { },
            (_, cancellation) =>
            {
                cancel = cancellation;
                return true;
            });

        var completion = Task.Run(() => Record.Exception(() =>
            relay.TryRunAfterAcceptedCommandStreams(() => { })));
        Assert.True(WaitUntil(() => Volatile.Read(ref cancel) is not null));
        cancel!();

        Assert.IsType<OperationCanceledException>(
            await completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AfterAcceptedCommandStreams_RethrowsTheWorkException()
    {
        Action? run = null;
        var relay = new GpuWorkerRelay(
            () => { },
            (work, _) =>
            {
                run = work;
                return true;
            });
        var expected = new InvalidOperationException("ordered state change failed");

        var completion = Task.Run(() => Record.Exception(() =>
            relay.TryRunAfterAcceptedCommandStreams(() => throw expected)));
        Assert.True(WaitUntil(() => Volatile.Read(ref run) is not null));
        run!();

        Assert.Same(expected, await completion.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RunOnGpuQueue_RethrowsTheWorkExceptionAndKeepsTheWorkerAlive()
    {
        _worker.Start();
        var expected = new InvalidOperationException("worker action failed");

        var completion = Task.Run(() => Record.Exception(() =>
            _worker.Relay.RunOnGpuQueue(() => throw expected)));

        Assert.Same(expected, await completion.WaitAsync(TimeSpan.FromSeconds(2)));
        var ranAfterFailure = false;
        _worker.Relay.RunOnGpuQueue(() => ranAfterFailure = true);
        Assert.True(ranAfterFailure);
    }

    [Fact]
    public void CommandsRunBeforeGuestWork()
    {
        _worker.AddGuestWork(() => Note("guest1"));
        _worker.AddGuestWork(() => Note("guest2"));
        _worker.Relay.Post(() => Note("command"));

        _worker.Start();

        Assert.True(WaitUntil(() => _order.Count == 3));
        Assert.Equal(new[] { "command", "guest1", "guest2" }, _order);
    }

    [Fact]
    public async Task ClosureRejectsCrossThreadCallsButAcceptedAndWorkerCallsStillRun()
    {
        using var fatal = new FatalScope();
        var relay = _worker.Relay;
        var accepted = Task.Run(() => relay.RunOnGpuQueue(() =>
        {
            Note("accepted");
            relay.RunOnGpuQueue(() => Note("reentered"));
            relay.Post(() => Note("reposted"));
        }));
        Assert.True(WaitUntil(() => relay.HasPendingCommands));

        relay.StopAcceptingWork();

        Assert.Throws<SchedulerFatalException>(() => relay.Post(() => Note("late_post")));
        Assert.Throws<SchedulerFatalException>(() => relay.RunOnGpuQueue(() => Note("late_sync")));
        Assert.False(relay.TryRunOnGpuQueue(() => Note("late_try")));
        Assert.Equal(2, fatal.Messages.Count);
        Assert.False(await CompletesWithin(accepted, 50));

        _worker.Start();

        Assert.True(await CompletesWithin(accepted, 5000));
        Assert.Equal(new[] { "accepted", "reentered", "reposted" }, _order);
        Assert.False(relay.HasPendingCommands);
    }
}
