// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// KernelGuestLogCollapser relays guest printf output but collapses runs of identical
// lines so a chatty guest (e.g. an engine logging a shader-cache miss per shader
// every frame) cannot flood the log. Unique output must never be dropped.
public sealed class KernelGuestLogCollapserTests : IDisposable
{
    private readonly List<KernelGuestLogCollapser> _created = [];

    // A collapser writing into a StringWriter with a fixed "\n" newline, so the
    // captured text is identical on every platform (default is "\r\n" on Windows).
    // Uses the real (system) clock, so its background flush timer never fires
    // within the sub-second lifetime of a test.
    private (KernelGuestLogCollapser Log, StringWriter Sink) NewCollapser()
    {
        var sink = new StringWriter { NewLine = "\n" };
        return (Track(new KernelGuestLogCollapser("PFX ", sink)), sink);
    }

    // A collapser driven by a manual clock, so the background flush timer can be
    // fired deterministically by advancing that clock.
    private (KernelGuestLogCollapser Log, StringWriter Sink, ManualTimeProvider Clock) NewTimedCollapser()
    {
        var clock = new ManualTimeProvider();
        var sink = new StringWriter { NewLine = "\n" };
        var log = Track(new KernelGuestLogCollapser(
            "PFX ", sink, clock, flushInterval: TimeSpan.FromSeconds(5)));
        return (log, sink, clock);
    }

    private KernelGuestLogCollapser Track(KernelGuestLogCollapser log)
    {
        _created.Add(log);
        return log;
    }

    // Stop the background timers the collapsers started.
    public void Dispose()
    {
        foreach (var log in _created)
        {
            log.Dispose();
        }
    }

    [Fact]
    public void SingleLine_IsPrintedImmediately_WithNoSummary()
    {
        var (log, sink) = NewCollapser();

        log.Write("only once");

        Assert.Equal("PFX only once\n", sink.ToString());
    }

    [Fact]
    public void DistinctLines_AreAllRelayedVerbatim()
    {
        var (log, sink) = NewCollapser();

        log.Write("a");
        log.Write("b");
        log.Write("c");

        Assert.Equal("PFX a\nPFX b\nPFX c\n", sink.ToString());
    }

    [Fact]
    public void IdenticalRun_PrintsOnce_ThenSummarisesWhenADifferentLineArrives()
    {
        var (log, sink) = NewCollapser();

        // 5000 identical lines followed by one different line: the flood must
        // collapse to the line itself plus a single "repeated" summary.
        for (var i = 0; i < 5000; i++)
        {
            log.Write("same line");
        }
        log.Write("different line");

        Assert.Equal(
            "PFX same line\n" +
            "PFX (previous message repeated 4999 more times)\n" +
            "PFX different line\n",
            sink.ToString());
    }

    [Fact]
    public void Flush_DrainsTrailingRepeats_ThatWouldOtherwiseBeLostAtExit()
    {
        var (log, sink) = NewCollapser();

        log.Write("tail");
        log.Write("tail");
        log.Write("tail");

        // No different line follows, so without an explicit flush the two extra
        // repeats would never be reported. The guest exit paths call Flush() for
        // exactly this case.
        log.Flush();

        Assert.Equal(
            "PFX tail\n" +
            "PFX (previous message repeated 2 more times)\n",
            sink.ToString());
    }

    [Fact]
    public void Flush_WithNoPendingRepeats_WritesNothingExtra()
    {
        var (log, sink) = NewCollapser();

        log.Write("x");
        log.Flush();
        log.Flush();

        Assert.Equal("PFX x\n", sink.ToString());
    }

    [Fact]
    public void IdleGuest_MidRun_HasTrailingRepeatsFlushedByTheBackgroundTimer()
    {
        // A guest prints a line a few times and then goes completely silent: no
        // different line, no further writes, no exit. The background timer must
        // still surface the count on its own once the interval elapses, otherwise
        // the log freezes on the single printed line.
        var (log, sink, clock) = NewTimedCollapser();

        log.Write("boom");
        log.Write("boom");
        log.Write("boom");   // two repeats outstanding, then the guest falls silent

        // Only wall time passes; the timer fires and drains the trailing count.
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(
            "PFX boom\n" +
            "PFX (previous message repeated 2 more times)\n",
            sink.ToString());
    }

    [Fact]
    public void StuckGuest_StillRepeating_GetsAThrottledSummaryEachInterval()
    {
        // A guest wedged in an error loop keeps printing the same line and never a
        // different one. Each elapsed interval surfaces the repeats accumulated
        // since the last summary, so the log keeps advancing.
        var (log, sink, clock) = NewTimedCollapser();

        log.Write("wedged");                    // printed once
        log.Write("wedged");                    // 1 outstanding
        clock.Advance(TimeSpan.FromSeconds(5)); // timer -> summary of 1

        log.Write("wedged");
        log.Write("wedged");                    // 2 outstanding
        clock.Advance(TimeSpan.FromSeconds(5)); // timer -> summary of 2

        Assert.Equal(
            "PFX wedged\n" +
            "PFX (previous message repeated 1 more times)\n" +
            "PFX (previous message repeated 2 more times)\n",
            sink.ToString());
    }

    [Fact]
    public void BackgroundTimer_WithNoOutstandingRepeats_WritesNothing()
    {
        // The timer fires on its own schedule; when there is nothing pending it
        // must stay silent rather than emitting an empty summary.
        var (log, sink, clock) = NewTimedCollapser();

        log.Write("once");
        clock.Advance(TimeSpan.FromSeconds(15)); // several timer ticks, nothing pending

        Assert.Equal("PFX once\n", sink.ToString());
    }

    [Fact]
    public void MessageEndingInNewline_IsNotGivenASecondNewline()
    {
        var (log, sink) = NewCollapser();

        // The guest already terminated the line; the collapser must relay it as-is
        // rather than appending another newline.
        log.Write("has newline\n");

        Assert.Equal("PFX has newline\n", sink.ToString());
    }

    // A TimeProvider whose clock only moves when the test advances it. Advancing
    // the clock also fires any timers whose due time has elapsed, so the
    // collapser's background flush timer can be driven deterministically.
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
