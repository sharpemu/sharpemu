// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

// Collapses runs of identical guest log lines. A chatty guest can print the same
// line thousands of times (e.g. an engine logging one shader-cache miss per
// shader every frame); relaying each one drowns the log. Consecutive identical
// lines are counted rather than reprinted, and a "(previous message repeated N
// more times)" summary is emitted when a different line arrives, when the guest
// exits, or once per flush interval while a run of duplicates is outstanding.
// Unique output is never dropped.
internal sealed class KernelGuestLogCollapser : IDisposable
{
    // A background timer drains an outstanding run of duplicates once per this
    // interval. Without it a guest that keeps printing the one line (or that fell
    // idle mid-run) would show that line once and then the log would appear to
    // freeze, because the summary otherwise waits for a *different* line, or a
    // guest exit, that may never come. It also throttles the summary of an
    // actively repeating guest to at most one line per interval.
    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly string _prefix;
    private readonly TextWriter? _output;
    private readonly TimeProvider _time;
    private readonly TimeSpan _flushInterval;
    private readonly ITimer _flushTimer;

    private string? _lastMessage;
    private long _repeatCount;
    private long _lastEmitTimestamp;

    public KernelGuestLogCollapser(
        string prefix,
        TextWriter? output = null,
        TimeProvider? timeProvider = null,
        TimeSpan? flushInterval = null)
    {
        _prefix = prefix;
        _output = output;
        _time = timeProvider ?? TimeProvider.System;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _flushTimer = _time.CreateTimer(
            _ => FlushIfStale(), state: null, _flushInterval, _flushInterval);
    }

    private TextWriter Output => _output ?? Console.Out;

    public void Write(string message)
    {
        lock (_gate)
        {
            if (message == _lastMessage)
            {
                _repeatCount++;
                return;
            }

            FlushLocked();
            _lastMessage = message;
            _lastEmitTimestamp = _time.GetTimestamp();

            if (message.EndsWith('\n') || message.EndsWith('\r'))
            {
                Output.Write($"{_prefix}{message}");
            }
            else
            {
                Output.WriteLine($"{_prefix}{message}");
            }
        }
    }

    // Drain any pending repeat summary. Called on guest exit so a trailing run of
    // duplicates is never lost.
    public void Flush()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    public void Dispose() => _flushTimer.Dispose();

    // Background-timer path: surface an outstanding run of duplicates once the
    // interval has elapsed, so the count still reaches the log when no further
    // writes (nor a guest exit) arrive to trigger a flush. Runs on a threadpool
    // thread; the lock serialises it against Write/Flush.
    private void FlushIfStale()
    {
        lock (_gate)
        {
            if (_time.GetElapsedTime(_lastEmitTimestamp) >= _flushInterval)
            {
                FlushLocked();
            }
        }
    }

    private void FlushLocked()
    {
        if (_repeatCount == 0)
        {
            return;
        }

        Output.WriteLine(
            $"{_prefix}(previous message repeated {_repeatCount} more times)");
        _repeatCount = 0;
        _lastEmitTimestamp = _time.GetTimestamp();
    }
}
