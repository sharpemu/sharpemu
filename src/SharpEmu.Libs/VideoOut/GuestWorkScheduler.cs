// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct GuestWorkSchedulerSnapshot(
    int Queued,
    long EnqueuedSequence,
    long CompletedSequence)
{
    public long Backlog => Math.Max(0, EnqueuedSequence - CompletedSequence);
}

internal sealed class GuestWorkScheduler
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly Queue<object> _queue = new();
    private bool _closed;
    private long _enqueuedSequence;
    private long _completedSequence;

    public GuestWorkScheduler(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public bool TryEnqueue(object work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_closed || _queue.Count >= _capacity)
            {
                return false;
            }

            _queue.Enqueue(work);
            _enqueuedSequence++;
            return true;
        }
    }

    public bool TryTake(out object work)
    {
        lock (_gate)
        {
            return _queue.TryDequeue(out work!);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completedSequence >= _enqueuedSequence)
            {
                throw new InvalidOperationException("guest work completion without an enqueue");
            }
            _completedSequence++;
        }
    }

    public GuestWorkSchedulerSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new GuestWorkSchedulerSnapshot(
                _queue.Count,
                _enqueuedSequence,
                _completedSequence);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }
}
