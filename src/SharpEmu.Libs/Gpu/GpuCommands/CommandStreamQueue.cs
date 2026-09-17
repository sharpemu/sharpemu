// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public enum IdleOutcome
{
    Completed,
    Cancelled,
    Failed,
}

public readonly record struct BlockedSnapshot(int Outstanding, double OldestAgeMilliseconds, ulong SampleWaitAddress, int SampleQueueId);

public enum SliceResult
{
    NoWork,
    AllBlocked,
    Progressed,
    BlockedWithoutProgress,
    Completed,
}

// Submissions of every queue; one thread runs the slices, other threads enqueue and wait.
public sealed class CommandStreamQueue
{
    public const int ComputeQueueCount = 56;
    public const int QueueCount = 1 + ComputeQueueCount;
    public const int AllBlockedRetryMilliseconds = 100;

    private readonly ICommandStreamHost _host;
    private readonly object _gate = new();
    private readonly LinkedList<CommandSubmission>[] _queues = new LinkedList<CommandSubmission>[QueueCount];
    private readonly GpuCommandInterpreter?[] _interpreters = new GpuCommandInterpreter?[QueueCount];
    private int _nextQueue;
    private int _submissionCount;
    private bool _accepting = true;
    private bool _stopping;
    private bool _processing;
    private bool _graphicsDone;
    private ulong _submitId;
    private ulong _lastCompletedGpuTick;
    private int _doneCount;
    private IdleOutcome _outcome = IdleOutcome.Completed;
    private Thread? _processingThread;

    public CommandStreamQueue(ICommandStreamHost host)
    {
        _host = host;
        for (var index = 0; index < QueueCount; index++)
        {
            _queues[index] = new LinkedList<CommandSubmission>();
        }
    }

    public ulong SubmissionsStarted { get; private set; }

    public ulong SlicesRun { get; private set; }

    public ulong BlockedRetries { get; private set; }

    public int FrameNumber => Volatile.Read(ref _doneCount);

    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _submissionCount != 0;
            }
        }
    }

    public int PendingSubmissionCount
    {
        get
        {
            lock (_gate)
            {
                return _submissionCount;
            }
        }
    }

    public bool HasUnblockedPending
    {
        get
        {
            lock (_gate)
            {
                return SelectQueueLocked() >= 0;
            }
        }
    }

    public GpuCommandInterpreter GetInterpreter(int queueId)
    {
        if (queueId < 0 || queueId >= QueueCount)
        {
            throw _host.Fatal($"The queue id is outside the queue table: queue={queueId}.");
        }

        lock (_gate)
        {
            return _interpreters[queueId] ??= new GpuCommandInterpreter(_host, queueId, queueId == 0 ? 0 : GpuCommandInterpreter.ComputeQueueBase + queueId - 1);
        }
    }

    public void EnqueueGraphics(ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        if (dwordCount == 0)
        {
            return;
        }

        lock (_gate)
        {
            var submission = new CommandSubmission(CommandSubmissionKind.Graphics, 0, address, dwordCount, submissionId, geometrySnapshots)
            {
                ResetInterpreter = _graphicsDone,
            };
            _graphicsDone = false;
            EnqueueLocked(submission);
        }
    }

    // Compute queues are addressed 0x20 to 0x57; queue id 1 is the first of them.
    public void EnqueueCompute(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        if (queue < GpuCommandInterpreter.ComputeQueueBase || queue >= GpuCommandInterpreter.ComputeQueueBase + ComputeQueueCount)
        {
            throw _host.Fatal($"The compute queue is outside the supported range: queue=0x{queue:X}.");
        }

        if (dwordCount == 0)
        {
            throw _host.Fatal($"A compute submission is empty: queue=0x{queue:X} address=0x{address:X16}.");
        }

        lock (_gate)
        {
            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.Compute, (int)(queue - GpuCommandInterpreter.ComputeQueueBase) + 1, address, dwordCount, submissionId, geometrySnapshots));
        }
    }

    // Queues a video-out export flip behind the graphics submissions accepted before it.
    public bool TryEnqueueFlipPreparation(int handle, int index, ulong requestId)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.FlipPreparation, 0, 0, 0, 0, null)
            {
                FlipHandle = handle,
                FlipIndex = index,
                FlipRequestId = requestId,
            });
            return true;
        }
    }

    private void EnqueueLocked(CommandSubmission submission)
    {
        if (!_accepting)
        {
            if (_outcome != IdleOutcome.Failed)
            {
                throw new OperationCanceledException("The command stream has stopped accepting submissions.");
            }

            throw _host.Fatal($"The command stream no longer accepts submissions: queue={submission.QueueId} address=0x{submission.Address:X16}.");
        }

        _queues[submission.QueueId].AddLast(submission);
        _submissionCount++;
        if (submission.Kind != CommandSubmissionKind.FlipPreparation)
            SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.Enqueued, submission.QueueId,
                submission.SubmissionId, submission.Address, submission.DwordCount, _submissionCount);
        Monitor.PulseAll(_gate);
    }

    // Marks the frame boundary; from another thread it first waits for the queue to drain.
    public IdleOutcome Done()
    {
        var outcome = IdleOutcome.Completed;
        if (_processingThread != Thread.CurrentThread)
        {
            outcome = WaitForIdle();
        }

        lock (_gate)
        {
            _graphicsDone = true;
            _doneCount++;
        }

        return outcome;
    }

    // Returns once nothing is pending; a cancelled or failed queue reports that instead.
    public IdleOutcome WaitForIdle()
    {
        lock (_gate)
        {
            while (_outcome == IdleOutcome.Completed && (_processing || _submissionCount != 0))
            {
                Monitor.Wait(_gate);
            }

            return _outcome;
        }
    }

    public void Wake()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    // Waits until a submission can run or the timeout passes; returns whether one can run.
    public bool WaitForWork(int timeoutMilliseconds)
    {
        lock (_gate)
        {
            if (SelectQueueLocked() >= 0 || _stopping)
            {
                return SelectQueueLocked() >= 0;
            }

            Monitor.Wait(_gate, timeoutMilliseconds);
            return SelectQueueLocked() >= 0;
        }
    }

    // Waits the whole interval unless a submission becomes runnable; stopping does not cut it short.
    public bool WaitForRetryInterval(int intervalMilliseconds)
    {
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + intervalMilliseconds;
            while (SelectQueueLocked() < 0)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)remaining);
            }

            return true;
        }
    }

    // Publishes a failure: nothing runs any more and every waiter is released with Failed.
    public void Fail()
    {
        lock (_gate)
        {
            FailLocked();
        }
    }

    private void FailLocked()
    {
        _outcome = IdleOutcome.Failed;
        _accepting = false;
        _stopping = true;
        DropAllLocked();
        _processing = false;
        Monitor.PulseAll(_gate);
    }

    // A completed GPU tick can make a blocked memory wait ready.
    public void NotifyCompletedGpuTick(ulong completedTick)
    {
        lock (_gate)
        {
            if (completedTick <= _lastCompletedGpuTick)
            {
                return;
            }

            _lastCompletedGpuTick = completedTick;
            RetryBlocked();
        }
    }

    public void RetryBlocked()
    {
        lock (_gate)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is { } head)
                {
                    head.Value.Blocked = false;
                }
            }

            BlockedRetries++;
            Monitor.PulseAll(_gate);
        }
    }

    // Runs one slice of the next unblocked submission on the calling thread.
    public SliceResult ProcessOne()
    {
        CommandSubmission submission;
        lock (_gate)
        {
            if (_processing)
            {
                throw _host.Fatal("The command stream queue is already processing a slice.");
            }

            var selected = SelectQueueLocked();
            if (selected < 0)
            {
                return _submissionCount != 0 ? SliceResult.AllBlocked : SliceResult.NoWork;
            }

            var queue = _queues[selected];
            submission = queue.First!.Value;
            queue.RemoveFirst();
            _submissionCount--;
            _nextQueue = (selected + 1) % QueueCount;
            _processing = true;
            _processingThread = Thread.CurrentThread;
            if (!submission.Started && submission.Kind != CommandSubmissionKind.FlipPreparation)
                SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.ProcessingStarted, submission.QueueId,
                    submission.SubmissionId, submission.Address, submission.DwordCount, _submissionCount);
        }

        bool complete;
        try
        {
            complete = RunSlice(submission);
        }
        catch (Exception exception)
        {
            try
            {
                // Report the cause before another submitting thread observes the failed queue.
                Console.Error.WriteLine($"[GPU][ERROR] Command stream slice failed: queue={submission.QueueId} address=0x{submission.Address:X16}. {exception}");
            }
            catch (Exception)
            {
                // An output failure must not prevent queue failure or replace the original exception.
            }

            Fail();
            throw;
        }

        lock (_gate)
        {
            SliceResult result;
            if (!complete)
            {
                submission.Blocked = true;
                submission.BlockedSinceTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                submission.BlockedPacketAddress = submission.Commands.CurrentPacketAddress;
                _queues[submission.QueueId].AddFirst(submission);
                _submissionCount++;
                result = submission.Commands.MadeProgress ? SliceResult.Progressed : SliceResult.BlockedWithoutProgress;
            }
            else
            {
                foreach (var queue in _queues)
                {
                    if (queue.First is { } head)
                    {
                        head.Value.Blocked = false;
                    }
                }

                result = SliceResult.Completed;
            }

            _processing = false;
            Monitor.PulseAll(_gate);
            return result;
        }
    }

    private bool RunSlice(CommandSubmission submission)
    {
        if (submission.Kind == CommandSubmissionKind.FlipPreparation)
        {
            SlicesRun++;
            _host.PrepareCpuFlip(submission.FlipHandle, submission.FlipIndex, submission.FlipRequestId);
            _host.Flush();
            return true;
        }

        var firstSlice = !submission.Started;
        var processor = GetInterpreter(submission.QueueId);
        if (firstSlice && submission.ResetInterpreter)
        {
            processor.Reset();
        }

        if (firstSlice)
        {
            submission.Started = true;
            processor.SubmitId = ++_submitId;
            processor.ResetCounters();
            processor.SetFlip(default);
            SubmissionsStarted++;
        }

        SlicesRun++;
        _host.BeginSubmission(submission.QueueId, submission.SubmissionId, submission.GeometrySnapshots);
        processor.ConstantEngineComplete = true;
        var complete = processor.Process(submission.Commands, submission.Address, submission.DwordCount) == SubmissionProgress.Complete;
        submission.CommandsComplete = complete;
        if (submission.Commands.MadeProgress)
        {
            if (complete)
            {
                _host.RunGarbageCollector();
            }

            _host.Flush();
        }
        else if (complete)
        {
            _host.RunGarbageCollector();
        }

        return complete;
    }

    public void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
    }

    // Processes to empty; with the flag, heads still blocked after a full retry cycle are cancelled.
    public IdleOutcome DrainForShutdown(bool cancelBlockedOnNoProgress)
    {
        for (;;)
        {
            var result = ProcessOne();
            switch (result)
            {
                case SliceResult.NoWork:
                    return Outcome;
                case SliceResult.Progressed:
                case SliceResult.Completed:
                    continue;
                case SliceResult.BlockedWithoutProgress:
                    if (HasUnblockedPending)
                    {
                        continue;
                    }

                    goto case SliceResult.AllBlocked;
                case SliceResult.AllBlocked:
                    _ = WaitForRetryInterval(AllBlockedRetryMilliseconds);
                    RetryBlocked();
                    if (!RunRetryCycle() && cancelBlockedOnNoProgress)
                    {
                        CancelBlocked();
                    }

                    continue;
            }
        }
    }

    // One pass over every head after a retry; true when any slice made progress.
    private bool RunRetryCycle()
    {
        var progressed = false;
        while (HasUnblockedPending)
        {
            var result = ProcessOne();
            if (result is SliceResult.Progressed or SliceResult.Completed)
            {
                progressed = true;
            }

            if (result is SliceResult.NoWork or SliceResult.AllBlocked)
            {
                break;
            }
        }

        return progressed;
    }

    private void CancelBlocked()
    {
        lock (_gate)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is { } head && head.Value.Blocked)
                {
                    _submissionCount -= queue.Count;
                    queue.Clear();
                    _outcome = IdleOutcome.Cancelled;
                }
            }

            Monitor.PulseAll(_gate);
        }
    }

    // Drops every submission without stopping admission; the presenter uses it after device loss.
    public void DiscardAll()
    {
        lock (_gate)
        {
            _outcome = IdleOutcome.Failed;
            DropAllLocked();
            Monitor.PulseAll(_gate);
        }
    }

    // A diagnostic view of the blocked heads: their count, the oldest age and one sample.
    public BlockedSnapshot SnapshotBlocked()
    {
        lock (_gate)
        {
            var count = 0;
            var oldestTicks = 0L;
            var sampleAddress = 0UL;
            var sampleQueue = -1;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var queueId = 0; queueId < QueueCount; queueId++)
            {
                if (_queues[queueId].First is not { } head || !head.Value.Blocked)
                {
                    continue;
                }

                count++;
                var age = now - head.Value.BlockedSinceTicks;
                if (sampleQueue < 0 || age > oldestTicks)
                {
                    oldestTicks = age;
                    sampleAddress = head.Value.BlockedPacketAddress;
                    sampleQueue = queueId;
                }
            }

            return new BlockedSnapshot(count, oldestTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, sampleAddress, sampleQueue);
        }
    }

    public IdleOutcome Outcome
    {
        get
        {
            lock (_gate)
            {
                return _outcome;
            }
        }
    }

    public int BlockedQueueCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var queue in _queues)
                {
                    if (queue.First is { } head && head.Value.Blocked)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    private int SelectQueueLocked()
    {
        for (var offset = 0; offset < QueueCount; offset++)
        {
            var queueId = (_nextQueue + offset) % QueueCount;
            if (_queues[queueId].First is { } head && !head.Value.Blocked)
            {
                return queueId;
            }
        }

        return -1;
    }

    private void DropAllLocked()
    {
        foreach (var queue in _queues)
        {
            queue.Clear();
        }

        _submissionCount = 0;
    }
}
