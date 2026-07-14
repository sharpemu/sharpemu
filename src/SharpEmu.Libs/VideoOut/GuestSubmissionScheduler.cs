// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal sealed class GuestSubmissionScheduler<TSubmission>
{
    private readonly Queue<TSubmission> _pending = new();
    private readonly int _capacity;
    private readonly Action<TSubmission> _wait;
    private readonly Func<TSubmission, bool> _isComplete;
    private readonly Action<TSubmission> _release;

    public GuestSubmissionScheduler(
        int capacity,
        Action<TSubmission> wait,
        Func<TSubmission, bool> isComplete,
        Action<TSubmission> release)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _wait = wait;
        _isComplete = isComplete;
        _release = release;
    }

    public int Count => _pending.Count;

    public void Track(TSubmission submission) => _pending.Enqueue(submission);

    public void EnsureCapacity()
    {
        Collect(waitForOldest: false);
        if (_pending.Count >= _capacity)
        {
            Collect(waitForOldest: true);
        }
    }

    public void Collect(bool waitForOldest)
    {
        if (waitForOldest && _pending.TryPeek(out var oldest))
        {
            _wait(oldest);
        }

        while (_pending.TryPeek(out var submission) && _isComplete(submission))
        {
            _pending.Dequeue();
            _release(submission);
        }
    }
}
