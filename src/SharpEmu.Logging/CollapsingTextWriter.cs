// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using System.Text.RegularExpressions;

namespace SharpEmu.Logging;

// A TextWriter decorator that collapses runs of repeated log lines into a single
// line plus a "(previous message repeated N more times)" summary. It is installed
// over Console.Out / Console.Error so every write in the process funnels through
// one collapser, instead of each spammy call site needing its own.
//
// Two lines count as "the same" after volatile tokens are masked: hex addresses
// (0x...) and #counters. That lets structurally identical spam that differs only
// by a pointer or an incrementing id still fold (e.g. the import-result warnings
// logged per-call during shutdown). The trade-off is deliberate: two genuinely
// different lines that differ only by such a token collapse together. The first
// occurrence of a run is always printed verbatim; only the repeats are dropped.
//
// The summary is emitted when a different line arrives, once per flush interval
// while a run is outstanding (so an ongoing flood still shows progress and a run
// that falls quiet still gets summarised), or on Drain at shutdown. Crucially it
// is NOT emitted from Flush(): Console.Error auto-flushes after every write, so
// summarising there would print a summary line after every single repeat.
public sealed partial class CollapsingTextWriter : TextWriter
{
    [GeneratedRegex("0x[0-9A-Fa-f]+")]
    private static partial Regex HexRegex();

    [GeneratedRegex("#[0-9]+")]
    private static partial Regex CounterRegex();

    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(2);

    private readonly TextWriter _inner;
    private readonly Lock _gate = new();
    private readonly StringBuilder _lineBuffer = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _flushInterval;
    private readonly ITimer _flushTimer;

    private string? _lastKey;
    private long _repeatCount;
    private long _lastEmitTimestamp;

    public CollapsingTextWriter(
        TextWriter inner,
        TimeProvider? timeProvider = null,
        TimeSpan? flushInterval = null)
    {
        _inner = inner;
        _time = timeProvider ?? TimeProvider.System;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _flushTimer = _time.CreateTimer(
            _ => FlushStaleSummary(), state: null, _flushInterval, _flushInterval);
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) => Append([value]);

    public override void Write(string? value) => Append(value.AsSpan());

    public override void Write(char[] buffer, int index, int count) =>
        Append(buffer.AsSpan(index, count));

    public override void Write(ReadOnlySpan<char> buffer) => Append(buffer);

    public override void WriteLine() => Append(_inner.NewLine.AsSpan());

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            AppendLocked(value.AsSpan());
            AppendLocked(_inner.NewLine.AsSpan());
        }
    }

    // Do NOT summarise here. Console.Error auto-flushes after every write, so a
    // summary from Flush would print once per repeat. Just push the inner writer.
    public override void Flush()
    {
        lock (_gate)
        {
            _inner.Flush();
        }
    }

    // Emit any pending summary and buffered partial line. Call at shutdown so a
    // trailing run of repeats (or an unterminated line) is never lost.
    public void Drain()
    {
        lock (_gate)
        {
            FlushSummaryLocked();
            if (_lineBuffer.Length > 0)
            {
                _inner.Write(_lineBuffer.ToString());
                _lineBuffer.Clear();
                _lastKey = null;
            }

            _inner.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flushTimer.Dispose();
            Drain();
        }

        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            AppendLocked(text);
        }
    }

    private void AppendLocked(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            _lineBuffer.Append(c);
            if (c == '\n')
            {
                FinalizeLineLocked();
            }
        }
    }

    private void FinalizeLineLocked()
    {
        var line = _lineBuffer.ToString();
        _lineBuffer.Clear();

        var content = line.AsSpan().TrimEnd('\n').TrimEnd('\r');
        var key = Normalize(content);

        if (key == _lastKey)
        {
            _repeatCount++;
            return;
        }

        FlushSummaryLocked();
        _inner.Write(line);
        _lastKey = key;
        _lastEmitTimestamp = _time.GetTimestamp();
    }

    // Timer path: surface an outstanding run once the interval has elapsed, so an
    // ongoing flood shows progress and a run that fell quiet is still summarised.
    private void FlushStaleSummary()
    {
        lock (_gate)
        {
            if (_repeatCount > 0 &&
                _time.GetElapsedTime(_lastEmitTimestamp) >= _flushInterval)
            {
                FlushSummaryLocked();
            }
        }
    }

    private void FlushSummaryLocked()
    {
        if (_repeatCount == 0)
        {
            return;
        }

        _inner.WriteLine(
            $"(previous message repeated {_repeatCount} more times)");
        _repeatCount = 0;
        _lastEmitTimestamp = _time.GetTimestamp();
    }

    private static string Normalize(ReadOnlySpan<char> content)
    {
        // Fast path: nothing volatile to mask, the line is its own key.
        if (content.IndexOf("0x", StringComparison.Ordinal) < 0 &&
            content.IndexOf('#') < 0)
        {
            return content.ToString();
        }

        var masked = HexRegex().Replace(content.ToString(), "0x#");
        return CounterRegex().Replace(masked, "#N");
    }
}
