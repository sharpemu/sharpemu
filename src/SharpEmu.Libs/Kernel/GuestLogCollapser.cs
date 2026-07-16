// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel;

// Collapses runs of identical guest log lines. A chatty guest can print the same
// line thousands of times (e.g. an engine logging one shader-cache miss per
// shader every frame); relaying each one drowns the log. Consecutive identical
// lines are counted rather than reprinted, and a single "(previous message
// repeated N more times)" summary is emitted when a different line arrives or the
// guest exits. Unique output is never dropped.
internal sealed class GuestLogCollapser(string prefix, TextWriter? output = null)
{
    private readonly object _gate = new();
    private readonly string _prefix = prefix;
    private readonly TextWriter? _output = output;

    private string? _lastMessage;
    private long _repeatCount;

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

    private void FlushLocked()
    {
        if (_repeatCount == 0)
        {
            return;
        }

        Output.WriteLine(
            $"{_prefix}(previous message repeated {_repeatCount} more times)");
        _repeatCount = 0;
    }
}
