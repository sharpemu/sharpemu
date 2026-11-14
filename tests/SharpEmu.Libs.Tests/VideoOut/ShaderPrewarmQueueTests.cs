// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// The boot prewarm's shared records: one caller's linked permutations compile one at a time, newest first, and a worker
// never waits on a busy caller while another record is left.
public sealed class ShaderPrewarmQueueTests
{
    // A record: its name and its linked caller (null for a plain record).
    private sealed record Item(string Name, ulong? Caller);

    private static ShaderPrewarmQueue<Item> Queue(params Item[] items) => new(items, static item => item.Caller);

    private static bool Never() => false;

    private const int Wait = ShaderPrewarmQueue<Item>.ReleaseWaitMilliseconds;

    [Fact]
    public void EachCallersLinkedRecordsTakeItsPlacesNewestFirst()
    {
        Item[] items =
        [
            new("a-old", 0xA), new("plain1", null), new("b-old", 0xB), new("a-mid", 0xA), new("plain2", null),
            new("a-new", 0xA), new("b-new", 0xB),
        ];

        var ordered = ShaderPrewarmQueue<Item>.NewestLinkedFirst(items, static item => item.Caller);

        Assert.Equal(new[] { "a-new", "plain1", "b-new", "a-mid", "plain2", "a-old", "b-old" },
            ordered.Select(static item => item.Name));
    }

    // The background lane takes every caller's first record before any second one: the one its session asks for first
    // (a-mid, c-new), else the oldest (b-old); then each caller's other records newest first. Slow callers go first within
    // a round, then callers in the order first recorded.
    [Fact]
    public void TheLinkedLaneTakesEachCallersFirstAskedRecordFirstAndSlowCallersFirst()
    {
        Item[] items =
        [
            new("a-old", 0xA), new("b-old", 0xB), new("a-mid", 0xA), new("c-old", 0xC), new("a-new", 0xA),
            new("c-new", 0xC), new("b-new", 0xB),
        ];

        var ordered = ShaderPrewarmQueue<Item>.LinkedLaneOrder(items, static item => item.Caller!.Value,
            static caller => caller == 0xC, static item => item.Name is "a-mid" or "c-new");

        Assert.Equal(new[] { "c-new", "a-mid", "b-old", "c-old", "a-new", "b-new", "a-old" },
            ordered.Select(static item => item.Name));
    }

    // The lane's queue keeps the lane order. While one worker compiles a caller that takes minutes, a second worker takes the
    // next caller's record, not the busy caller's second permutation, which waits until the first is released.
    [Fact]
    public void ALaneQueueKeepsTheLaneOrderAndASecondWorkerTakesTheNextCaller()
    {
        Item[] lane = [new("slow-first", 0xA), new("quick", 0xB), new("slow-second", 0xA), new("plain", null)];
        var queue = new ShaderPrewarmQueue<Item>(lane, static item => item.Caller, keepOrder: true);

        Assert.True(queue.TryTake(Never, out var first, out var firstGate));
        Assert.Equal("slow-first", first.Name);
        Assert.True(queue.TryTake(Never, out var second, out var secondGate, mayWait: false));
        Assert.Equal("quick", second.Name);
        queue.Release(secondGate);
        Assert.True(queue.TryTake(Never, out var third, out var thirdGate, mayWait: false));
        Assert.Equal("plain", third.Name);
        queue.Release(thirdGate);
        Assert.False(queue.TryTake(Never, out _, out _, mayWait: false));
        queue.Release(firstGate);
        Assert.True(queue.TryTake(Never, out var last, out var lastGate, mayWait: false));
        Assert.Equal("slow-second", last.Name);
        queue.Release(lastGate);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData(32, 2)]
    [InlineData(16, 2)]
    [InlineData(12, 1)]
    [InlineData(8, 1)]
    [InlineData(1, 1)]
    public void TheLinkedLaneRunsASecondWorkerOnlyWithSixteenHardwareThreads(int processorCount, int workers) =>
        Assert.Equal(workers, ShaderPrewarmQueue<Item>.LinkedLaneWorkers(processorCount));

    // A peek leaves the record in place, and a take without waiting returns at once when every record left waits on a
    // busy caller.
    [Fact]
    public void APeekLeavesTheRecordAndATakeWithoutWaitingReturnsAtOnce()
    {
        var queue = Queue(new("a-old", 0xA), new("a-new", 0xA));
        Assert.True(queue.TryPeek(out var peeked));
        Assert.Equal("a-new", peeked.Name);
        Assert.True(queue.TryTake(Never, out var first, out var gate));
        Assert.Equal("a-new", first.Name);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(queue.TryTake(Never, out _, out _, mayWait: false));
        Assert.True(watch.ElapsedMilliseconds < Wait);
        Assert.Equal(1, queue.Count);
        queue.Release(gate);
        Assert.True(queue.TryTake(Never, out var second, out var secondGate, mayWait: false));
        Assert.Equal("a-old", second.Name);
        queue.Release(secondGate);
        Assert.False(queue.TryPeek(out _));
    }

    [Fact]
    public void ABusyCallersRecordGoesBackAndTheNextRecordIsTaken()
    {
        var queue = Queue(new("a-old", 0xA), new("a-new", 0xA), new("plain", null));

        Assert.True(queue.TryTake(Never, out var first, out var firstGate));
        Assert.Equal("a-new", first.Name);
        Assert.NotNull(firstGate);

        // The caller is busy: a-old goes back and the plain record is taken.
        Assert.True(queue.TryTake(Never, out var second, out var secondGate));
        Assert.Equal("plain", second.Name);
        Assert.Null(secondGate);
        Assert.Equal(1, queue.Count);
        Assert.Equal(1, queue.Deferrals);

        queue.Release(firstGate);
        Assert.True(queue.TryTake(Never, out var third, out var thirdGate));
        Assert.Equal("a-old", third.Name);
        queue.Release(thirdGate);
        Assert.False(queue.TryTake(Never, out _, out _));
    }

    [Fact]
    public void WhenOnlyBusyCallersAreLeftAWorkerWaitsForTheirReleaseWithoutSpinning()
    {
        var queue = Queue(new("a-old", 0xA), new("a-new", 0xA), new("b", 0xB));
        Assert.True(queue.TryTake(Never, out var a, out var aGate));
        Assert.True(queue.TryTake(Never, out var b, out var bGate));
        Assert.Equal(("a-new", "b"), (a.Name, b.Name));

        // Only a-old is left and its caller is busy.
        Item? waited = null;
        SemaphoreSlim? waitedGate = null;
        var worker = new Thread(() =>
        {
            if (queue.TryTake(Never, out var record, out var gate)) (waited, waitedGate) = (record, gate);
        });
        var deferrals = queue.Deferrals;
        worker.Start();
        Assert.False(worker.Join(3 * Wait));
        Assert.Null(waited);
        Assert.Equal(1, queue.Count);
        // One pass (two tries of the one record) and no more while it waits.
        Assert.InRange(queue.Deferrals - deferrals, 1, 2);

        // Releasing another caller wakes it for one more pass; a-old stays queued.
        queue.Release(bGate);
        Assert.False(worker.Join(2 * Wait));
        Assert.InRange(queue.Deferrals - deferrals, 2, 4);

        queue.Release(aGate);
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal("a-old", waited!.Name);
        queue.Release(waitedGate);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void StoppingEndsAWaitAndTakesNothing()
    {
        var queue = Queue(new("a-old", 0xA), new("a-new", 0xA));
        Assert.True(queue.TryTake(Never, out _, out var gate));
        var stopping = false;
        var taken = true;
        var worker = new Thread(() => taken = queue.TryTake(() => Volatile.Read(ref stopping), out _, out _));
        worker.Start();
        Assert.False(worker.Join(2 * Wait));
        Volatile.Write(ref stopping, true);
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        Assert.False(taken);
        queue.Release(gate);
    }

    [Fact]
    public void ManyWorkersTakeEveryRecordOnceAndNeverTwoOfOneCallerAtOnce()
    {
        var items = new List<Item>();
        for (var index = 0; index < 60; index++)
        {
            items.Add(new($"r{index}", (index % 3) switch { 0 => null, 1 => 0xA, _ => (ulong)(0xB + index % 2) }));
        }

        var queue = new ShaderPrewarmQueue<Item>(items, static item => item.Caller);
        var taken = new ConcurrentBag<string>();
        var running = new ConcurrentDictionary<ulong, int>();
        var overlap = 0;
        var workers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            while (queue.TryTake(Never, out var record, out var gate))
            {
                try
                {
                    if (record.Caller is { } caller && running.AddOrUpdate(caller, 1, static (_, count) => count + 1) > 1)
                    {
                        Interlocked.Increment(ref overlap);
                    }

                    Thread.Sleep(1);
                    taken.Add(record.Name);
                    if (record.Caller is { } done)
                    {
                        running.AddOrUpdate(done, 0, static (_, count) => count - 1);
                    }
                }
                finally
                {
                    queue.Release(gate);
                }
            }
        })).ToArray();
        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) Assert.True(worker.Join(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, overlap);
        Assert.Equal(items.Select(static item => item.Name).Order(), taken.Order());
    }
}
