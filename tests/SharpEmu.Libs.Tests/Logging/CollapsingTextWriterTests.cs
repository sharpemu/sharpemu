// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Logging;
using Xunit;

namespace SharpEmu.Libs.Tests.Logging;

// CollapsingTextWriter decorates Console.Out/Console.Error and folds runs of
// repeated log lines. Two lines are "the same" after hex addresses (0x...) and
// #counters are masked, so structurally identical spam that differs only by a
// pointer or an incrementing id still collapses. Unique output is never dropped.
public sealed class CollapsingTextWriterTests : IDisposable
{
    private readonly List<CollapsingTextWriter> _created = [];

    private (CollapsingTextWriter Writer, StringWriter Sink, ManualTimeProvider Clock) New()
    {
        var clock = new ManualTimeProvider();
        var sink = new StringWriter { NewLine = "\n" };
        var writer = new CollapsingTextWriter(
            sink, clock, flushInterval: TimeSpan.FromSeconds(2));
        _created.Add(writer);
        return (writer, sink, clock);
    }

    // Stop the background timers; Dispose also drains, but the asserts run first.
    public void Dispose()
    {
        foreach (var writer in _created)
        {
            writer.Dispose();
        }
    }

    [Fact]
    public void DistinctLines_PassThroughVerbatim()
    {
        var (writer, sink, _) = New();

        writer.WriteLine("a");
        writer.WriteLine("b");
        writer.WriteLine("c");

        Assert.Equal("a\nb\nc\n", sink.ToString());
    }

    [Fact]
    public void IdenticalRun_PrintsOnce_ThenSummarisesWhenADifferentLineArrives()
    {
        var (writer, sink, _) = New();

        for (var i = 0; i < 5; i++)
        {
            writer.WriteLine("same");
        }
        writer.WriteLine("different");

        Assert.Equal(
            "same\n" +
            "(previous message repeated 4 more times)\n" +
            "different\n",
            sink.ToString());
    }

    [Fact]
    public void Normalisation_FoldsLinesDifferingOnlyByAddressesAndCounters()
    {
        var (writer, sink, _) = New();

        // The shutdown import-result spam: every line differs by the Import#
        // counter and an rsi address that alternates between two values, yet they
        // are the same event and must collapse to one line plus a summary.
        writer.WriteLine("Import#100 result: INVALID (QOQtbeDqsT4) rsi=0x00007FFFDF1FF790 ret=0x800");
        writer.WriteLine("Import#131 result: INVALID (QOQtbeDqsT4) rsi=0x00007FFFDF1FEF90 ret=0x800");
        writer.WriteLine("Import#162 result: INVALID (QOQtbeDqsT4) rsi=0x00007FFFDF1FF790 ret=0x800");
        writer.WriteLine("[DEBUG] done");

        Assert.Equal(
            "Import#100 result: INVALID (QOQtbeDqsT4) rsi=0x00007FFFDF1FF790 ret=0x800\n" +
            "(previous message repeated 2 more times)\n" +
            "[DEBUG] done\n",
            sink.ToString());
    }

    [Fact]
    public void Normalisation_KeepsGenuinelyDifferentLinesApart()
    {
        var (writer, sink, _) = New();

        // Same shape, different NID in the un-masked part: must NOT fold.
        writer.WriteLine("Import#1 result: INVALID (AAAAAAAAAAA) rsi=0x10");
        writer.WriteLine("Import#2 result: INVALID (BBBBBBBBBBB) rsi=0x20");

        Assert.Equal(
            "Import#1 result: INVALID (AAAAAAAAAAA) rsi=0x10\n" +
            "Import#2 result: INVALID (BBBBBBBBBBB) rsi=0x20\n",
            sink.ToString());
    }

    [Fact]
    public void IdleRun_IsSummarisedByTheBackgroundTimer()
    {
        var (writer, sink, clock) = New();

        writer.WriteLine("spam");
        writer.WriteLine("spam");            // one outstanding repeat, then quiet

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(
            "spam\n" +
            "(previous message repeated 1 more times)\n",
            sink.ToString());
    }

    [Fact]
    public void Flush_DoesNotEmitTheSummary_SoAutoFlushDoesNotSpamIt()
    {
        var (writer, sink, _) = New();

        writer.WriteLine("spam");
        writer.WriteLine("spam");
        writer.Flush();                       // Console.Error auto-flushes like this
        writer.Flush();

        // The repeat is still pending; Flush must stay silent about it.
        Assert.Equal("spam\n", sink.ToString());
    }

    [Fact]
    public void Drain_EmitsTrailingSummary_AtShutdown()
    {
        var (writer, sink, _) = New();

        writer.WriteLine("spam");
        writer.WriteLine("spam");
        writer.WriteLine("spam");
        writer.Drain();

        Assert.Equal(
            "spam\n" +
            "(previous message repeated 2 more times)\n",
            sink.ToString());
    }

    [Fact]
    public void Drain_FlushesAnUnterminatedPartialLine()
    {
        var (writer, sink, _) = New();

        writer.Write("partial with no newline");
        writer.Drain();

        Assert.Equal("partial with no newline", sink.ToString());
    }

    // A TimeProvider whose clock only moves when the test advances it; advancing
    // also fires any due timers, so the background summary can be driven
    // deterministically.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, dueTime, period);
            lock (_timers)
            {
                _timers.Add(timer);
            }
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            _timestamp += delta.Ticks;

            FakeTimer[] snapshot;
            lock (_timers)
            {
                snapshot = [.. _timers];
            }
            foreach (var timer in snapshot)
            {
                timer.Advance(delta);
            }
        }

        private void Remove(FakeTimer timer)
        {
            lock (_timers)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class FakeTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private TimeSpan _period = period;
            private bool _active = dueTime != Timeout.InfiniteTimeSpan;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _remaining = dueTime;
                _active = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }

            public void Advance(TimeSpan delta)
            {
                if (!_active)
                {
                    return;
                }

                _remaining -= delta;
                while (_active && _remaining <= TimeSpan.Zero)
                {
                    callback(state);
                    if (_period <= TimeSpan.Zero)
                    {
                        _active = false;
                    }
                    else
                    {
                        _remaining += _period;
                    }
                }
            }

            public void Dispose()
            {
                _active = false;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
