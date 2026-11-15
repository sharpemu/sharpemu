// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// The prewarm's threads serve both lanes, the item that must start first first, and each lane finishes once.
public sealed class PrewarmPoolTests
{
    // A lane over named items with their starts; it logs each item it runs and counts its finishes.
    private sealed class TestLane(string name, ConcurrentQueue<string> log, params float[] starts)
    {
        private readonly ConcurrentQueue<float> _items = new(starts);
        private readonly int _total = starts.Length;

        public int Finishes;
        public bool EmptyWhenFinished = true;

        public PrewarmPool.Lane Lane => new(_total,
            (out float start) => _items.TryPeek(out start),
            _ =>
            {
                if (!_items.TryDequeue(out var start))
                {
                    return false;
                }

                log.Enqueue($"{name}{start}");
                return true;
            },
            () =>
            {
                EmptyWhenFinished &= _items.IsEmpty;
                Interlocked.Increment(ref Finishes);
            });
    }

    [Fact]
    public void AWorkerRunsTheItemThatMustStartFirst()
    {
        var log = new ConcurrentQueue<string>();
        var compute = new TestLane("c", log, 0, 5, 10);
        var graphics = new TestLane("g", log, 3, 4, 20);
        var pool = new PrewarmPool(static () => false);
        // One thread, which begins at Start and so sees both lanes from its first turn.
        pool.Add(graphics.Lane, 1, "test graphics");
        pool.Add(compute.Lane, 0, "test compute");
        pool.Start();
        pool.Stop();
        Assert.Equal(new[] { "c0", "g3", "g4", "c5", "c10", "g20" }, log);
        Assert.Equal(1, compute.Finishes);
        Assert.Equal(1, graphics.Finishes);
        Assert.True(compute.EmptyWhenFinished && graphics.EmptyWhenFinished);
    }

    // The threads added with one lane serve the other once theirs is empty.
    [Fact]
    public void ALanesThreadsServeTheOtherLane()
    {
        var log = new ConcurrentQueue<string>();
        var compute = new TestLane("c", log, Enumerable.Range(0, 50).Select(static index => (float)index).ToArray());
        var graphics = new TestLane("g", log, 100);
        var pool = new PrewarmPool(static () => false);
        pool.Add(compute.Lane, 0, "test compute");
        pool.Add(graphics.Lane, 3, "test graphics");
        pool.Start();
        pool.Stop();
        Assert.Equal(51, log.Count);
        Assert.Contains("g100", log);
        Assert.Equal(1, compute.Finishes);
        Assert.Equal(1, graphics.Finishes);
        Assert.True(compute.EmptyWhenFinished && graphics.EmptyWhenFinished);
    }

    // A lane whose items wait on a busy caller lets the other lane run; it waits for the caller only when the other is empty.
    [Fact]
    public void ALaneThatMustWaitLetsTheOtherRun()
    {
        var log = new ConcurrentQueue<string>();
        var released = false;
        var waits = 0;
        var blocked = new PrewarmPool.Lane(1,
            (out float start) =>
            {
                start = 0;
                return !Volatile.Read(ref released) || log.All(static entry => entry != "blocked");
            },
            mayWait =>
            {
                if (!mayWait)
                {
                    return false;
                }

                Interlocked.Increment(ref waits);
                Volatile.Write(ref released, true);
                log.Enqueue("blocked");
                return true;
            },
            static () => { });
        var other = new TestLane("g", log, 1, 2);
        var pool = new PrewarmPool(static () => false);
        pool.Add(blocked, 0, "test blocked");
        pool.Add(other.Lane, 1, "test other");
        pool.Start();
        pool.Stop();
        Assert.Equal(new[] { "g1", "g2", "blocked" }, log);
        Assert.Equal(1, waits);
    }

    // Stopping ends the threads before the lanes' last items; Stop then finishes those lanes once, also for a pool that never
    // started.
    [Fact]
    public void StopFinishesTheLanesItStoppedBefore()
    {
        var never = 0;
        var unstarted = new PrewarmPool(static () => false);
        unstarted.Add(new PrewarmPool.Lane(1, (out float start) => { start = 0; return true; }, static _ => true,
            () => Interlocked.Increment(ref never)), 2, "test unstarted");
        unstarted.Stop();
        Assert.Equal(1, never);

        var log = new ConcurrentQueue<string>();
        var compute = new TestLane("c", log);
        var finished = 0;
        var pool = new PrewarmPool(static () => true);
        var unfinished = new PrewarmPool.Lane(1, (out float start) => { start = 0; return true; }, static _ => true,
            () => Interlocked.Increment(ref finished));
        pool.Add(unfinished, 2, "test unfinished");
        pool.Add(compute.Lane, 0, "test compute");
        pool.Start();
        pool.Stop();
        pool.Stop();
        Assert.Equal(1, finished);
        Assert.Equal(1, compute.Finishes);
        Assert.Empty(log);
    }

    // A queue looks empty while a worker puts a busy caller's record back, so a lane finishes on the run of its last
    // item, not when its queue looks empty with no run in progress.
    [Fact]
    public void ALaneFinishesOnItsLastItemNotWhenItsQueueLooksEmpty()
    {
        var finishes = 0;
        var queued = 1;
        var lane = new PrewarmPool.Lane(2,
            (out float start) =>
            {
                start = 0;
                return queued > 0;
            },
            _ =>
            {
                if (queued == 0)
                {
                    return false;
                }

                queued--;
                return true;
            },
            () => finishes++);
        Assert.True(lane.Run(mayWait: false));
        // The second item is out of the queue while a worker puts it back: the lane looks empty, with no run in progress.
        Assert.False(lane.TryPeek(out _));
        Assert.False(lane.Run(mayWait: false));
        Assert.Equal(0, finishes);
        queued = 1;
        Assert.True(lane.Run(mayWait: false));
        Assert.Equal(1, finishes);
        Assert.True(lane.Done);
    }
}
