// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;

AssertThrows<ArgumentOutOfRangeException>(() =>
    new GuestSubmissionScheduler<Submission>(0, _ => { }, _ => true, _ => { }));

var waited = new List<int>();
var released = new List<int>();
var scheduler = new GuestSubmissionScheduler<Submission>(
    2,
    submission =>
    {
        waited.Add(submission.Id);
        submission.Complete = true;
    },
    submission => submission.Complete,
    submission => released.Add(submission.Id));

var first = new Submission(1);
var second = new Submission(2);
scheduler.Track(first);
scheduler.Track(second);
scheduler.Collect(waitForOldest: false);
Assert(released.Count == 0, "in-flight submissions were released early");

second.Complete = true;
scheduler.Collect(waitForOldest: false);
Assert(released.Count == 0, "FIFO lifetime ordering was not preserved");

scheduler.EnsureCapacity();
Assert(waited.SequenceEqual([1]), "capacity did not wait for the oldest submission");
Assert(released.SequenceEqual([1, 2]), "completed resources were not released in FIFO order");
Assert(scheduler.Count == 0, "completed submissions remain tracked");

Console.WriteLine("GuestSubmissionScheduler capacity and deferred release tests passed.");

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

internal sealed class Submission(int id)
{
    public int Id { get; } = id;
    public bool Complete { get; set; }
}
