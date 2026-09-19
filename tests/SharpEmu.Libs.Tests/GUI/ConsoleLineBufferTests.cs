// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class ConsoleLineBufferTests
{
    [Fact]
    public void EnqueueAndDequeuePreserveOrderAndTheErrorFlag()
    {
        var buffer = new ConsoleLineBuffer(capacity: 4);

        buffer.Enqueue("first", isError: false);
        buffer.Enqueue("second", isError: true);

        Assert.Equal(2, buffer.Count);
        Assert.True(buffer.TryDequeue(out var line, out var isError));
        Assert.Equal("first", line);
        Assert.False(isError);
        Assert.True(buffer.TryDequeue(out line, out isError));
        Assert.Equal("second", line);
        Assert.True(isError);
        Assert.False(buffer.TryDequeue(out _, out _));
        Assert.True(buffer.IsEmpty);
    }

    /// <summary>
    /// The point of the whole class: a producer that outruns the drain must cost a bounded amount
    /// of memory rather than retaining everything it ever wrote.
    /// </summary>
    [Fact]
    public void ProducerThatOutrunsTheDrainNeverExceedsCapacity()
    {
        var buffer = new ConsoleLineBuffer(capacity: 100);

        for (var i = 0; i < 100_000; i++)
        {
            buffer.Enqueue($"line {i}", isError: false);
            Assert.True(buffer.Count <= buffer.Capacity);
        }

        Assert.Equal(100, buffer.Count);
    }

    /// <summary>
    /// Oldest-first, because the newest output is what someone watching a stuck title needs.
    /// </summary>
    [Fact]
    public void OverflowDropsTheOldestLinesAndKeepsTheNewest()
    {
        var buffer = new ConsoleLineBuffer(capacity: 3);

        buffer.Enqueue("a", isError: false);
        buffer.Enqueue("b", isError: false);
        buffer.Enqueue("c", isError: false);
        buffer.Enqueue("d", isError: false);
        buffer.Enqueue("e", isError: false);

        Assert.Equal(3, buffer.Count);
        Assert.True(buffer.TryDequeue(out var line, out _));
        Assert.Equal("c", line);
        Assert.True(buffer.TryDequeue(out line, out _));
        Assert.Equal("d", line);
        Assert.True(buffer.TryDequeue(out line, out _));
        Assert.Equal("e", line);
    }

    [Fact]
    public void DroppedCountIsReportedOnceAndThenReset()
    {
        var buffer = new ConsoleLineBuffer(capacity: 2);

        buffer.Enqueue("a", isError: false);
        buffer.Enqueue("b", isError: false);
        Assert.Equal(0, buffer.ExchangeDroppedCount());

        buffer.Enqueue("c", isError: false);
        buffer.Enqueue("d", isError: false);

        Assert.Equal(2, buffer.ExchangeDroppedCount());
        Assert.Equal(0, buffer.ExchangeDroppedCount());
    }

    [Fact]
    public void NothingIsDroppedWhileTheDrainKeepsUp()
    {
        var buffer = new ConsoleLineBuffer(capacity: 2);

        for (var i = 0; i < 1_000; i++)
        {
            buffer.Enqueue($"line {i}", isError: false);
            Assert.True(buffer.TryDequeue(out var line, out _));
            Assert.Equal($"line {i}", line);
        }

        Assert.Equal(0, buffer.ExchangeDroppedCount());
        Assert.True(buffer.IsEmpty);
    }

    /// <summary>
    /// Production has several writers (the emulator's stdout and stderr readers, plus the mirrored
    /// in-process console) against one drain, so the bookkeeping has to survive that. A negative or
    /// over-capacity Count here would mean the counter had drifted from the queue.
    /// </summary>
    [Fact]
    public async Task CountStaysConsistentUnderConcurrentProducersAndOneConsumer()
    {
        var buffer = new ConsoleLineBuffer(capacity: 64);
        using var stop = new CancellationTokenSource();
        var consumed = 0;

        var producers = Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                buffer.Enqueue($"p{producer}:{i}", isError: false);
            }
        })).ToArray();

        var consumer = Task.Run(() =>
        {
            while (!stop.Token.IsCancellationRequested)
            {
                while (buffer.TryDequeue(out _, out _))
                {
                    consumed++;
                }
            }
        });

        await Task.WhenAll(producers);
        stop.Cancel();
        await consumer;

        while (buffer.TryDequeue(out _, out _))
        {
            consumed++;
        }

        Assert.True(buffer.IsEmpty);
        Assert.Equal(0, buffer.Count);
        // Every line either reached the consumer or was counted as dropped; none vanished.
        Assert.Equal(4 * 20_000, consumed + buffer.ExchangeDroppedCount());
    }

    [Fact]
    public void CapacityMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleLineBuffer(capacity: 0));
    }
}
