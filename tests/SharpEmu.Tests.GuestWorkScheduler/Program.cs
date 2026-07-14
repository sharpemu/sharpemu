// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;

AssertThrows<ArgumentOutOfRangeException>(() => new GuestWorkScheduler(0));

var scheduler = new GuestWorkScheduler(2);
Assert(scheduler.TryEnqueue("first"), "first enqueue failed");
Assert(scheduler.TryEnqueue("second"), "second enqueue failed");
Assert(!scheduler.TryEnqueue("overflow"), "scheduler exceeded its capacity");

var pending = scheduler.Snapshot();
Assert(pending.Queued == 2, "queued work count is incorrect");
Assert(pending.EnqueuedSequence == 2, "enqueue sequence is incorrect");
Assert(pending.CompletedSequence == 0, "completion sequence advanced early");
Assert(pending.Backlog == 2, "backlog is incorrect");

Assert(scheduler.TryTake(out var first) && Equals(first, "first"),
    "scheduler did not preserve FIFO order");
scheduler.Complete();
Assert(scheduler.TryEnqueue("third"), "capacity was not released");
Assert(scheduler.TryTake(out var second) && Equals(second, "second"),
    "second work item was reordered");
scheduler.Complete();
Assert(scheduler.TryTake(out var third) && Equals(third, "third"),
    "third work item was not available");
scheduler.Complete();

var completed = scheduler.Snapshot();
Assert(completed.Queued == 0, "completed queue is not empty");
Assert(completed.EnqueuedSequence == 3 && completed.CompletedSequence == 3,
    "completed sequence does not match enqueued work");
AssertThrows<InvalidOperationException>(scheduler.Complete);

scheduler.Close();
Assert(!scheduler.TryEnqueue("closed"), "closed scheduler accepted work");

Console.WriteLine("GuestWorkScheduler capacity, FIFO, sequence, and close tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}");
}
