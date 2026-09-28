// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.GuestMemory;

namespace SharpEmu.Libs.Gpu.Scheduling;

// Run queued commands on the GPU worker before submissions. The relay does not wait for GPU completion.
public sealed class GpuWorkerRelay : IGpuQueueRelay
{
    [ThreadStatic]
    private static GpuWorkerRelay? _boundWorker;

    private readonly object _gate = new();
    private readonly Queue<Action> _commands = new();
    private readonly Action _wake;
    private readonly Func<Action, Action, bool>? _tryEnqueueAfterPendingWork;
    private int _pendingCount;
    private bool _accepting = true;

    private sealed class HandoffTiming
    {
        public long LockStarted;
        public long LockAcquired;
        public long Published;
        public long WakeStarted;
        public long WakeFinished;
        public long CallbackStarted;
        public long CompletionStarted;
    }

    public GpuWorkerRelay(Action wake, Func<Action, Action, bool>? tryEnqueueAfterPendingWork = null)
    {
        _wake = wake;
        _tryEnqueueAfterPendingWork = tryEnqueueAfterPendingWork;
    }

    public bool TryRunAfterPendingWork(Action work) => TryRunOnGpuQueue(work);

    public bool TryRunAfterAcceptedCommandStreams(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (IsGpuQueueThread || _tryEnqueueAfterPendingWork is null)
        {
            return TryRunOnGpuQueue(work);
        }

        using var done = new SemaphoreSlim(0);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        if (!_tryEnqueueAfterPendingWork(
                () =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception exception)
                    {
                        failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                    }
                    finally
                    {
                        done.Release();
                    }
                },
                () =>
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                        new OperationCanceledException(
                            "The command stream stopped before the ordered GPU worker action could run."));
                    done.Release();
                }))
        {
            return false;
        }

        done.Wait();
        failure?.Throw();
        return true;
    }

    public bool IsGpuQueueThread => _boundWorker == this;

    public bool HasPendingCommands => Volatile.Read(ref _pendingCount) != 0;

    public void BindCurrentThread() => _boundWorker = this;

    public void Post(Action work)
    {
        if (!TryPost(work))
        {
            throw SubmissionScheduler.Fatal("The GPU worker relay is closed.");
        }
    }

    public bool TryPost(Action work) => TryPost(work, null);

    private bool TryPost(Action work, HandoffTiming? timing)
    {
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        if (timing is not null)
            timing.LockStarted = GuestMemoryProfile.GetTimestamp();
        lock (_gate)
        {
            if (timing is not null)
                timing.LockAcquired = GuestMemoryProfile.GetTimestamp();
            if (!_accepting)
            {
                return false;
            }

            _commands.Enqueue(work);
            Interlocked.Increment(ref _pendingCount);
            if (timing is not null)
                timing.Published = GuestMemoryProfile.GetTimestamp();
        }

        if (timing is not null)
            timing.WakeStarted = GuestMemoryProfile.GetTimestamp();
        _wake();
        if (timing is not null)
            timing.WakeFinished = GuestMemoryProfile.GetTimestamp();
        return true;
    }

    public void RunPendingCommands()
    {
        if (!IsGpuQueueThread)
        {
            throw SubmissionScheduler.Fatal("Only the GPU worker can run relay commands.");
        }

        while (HasPendingCommands)
        {
            Action command;
            lock (_gate)
            {
                command = _commands.Dequeue();
                Interlocked.Decrement(ref _pendingCount);
            }

            command();
        }
    }

    public void RunOnGpuQueue(Action work)
    {
        if (!TryRunOnGpuQueue(work))
        {
            throw SubmissionScheduler.Fatal("The GPU worker relay is closed.");
        }
    }

    public bool TryRunOnGpuQueue(Action work)
    {
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        using var done = new SemaphoreSlim(0);
        var timing = GuestMemoryProfile.GetTimestamp() != 0 ? new HandoffTiming() : null;
        if (!TryPost(() =>
            {
                if (timing is not null)
                    timing.CallbackStarted = GuestMemoryProfile.GetTimestamp();
                work();
                if (timing is not null)
                    timing.CompletionStarted = GuestMemoryProfile.GetTimestamp();
                done.Release();
            }, timing))
        {
            return false;
        }

        done.Wait();
        if (timing is not null)
        {
            var callerResumed = GuestMemoryProfile.GetTimestamp();
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayQueueLockAcquisition,
                timing.LockStarted, timing.LockAcquired);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayQueuePublication,
                timing.LockAcquired, timing.Published);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayWakeCall,
                timing.WakeStarted, timing.WakeFinished);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayQueueResidence,
                timing.Published, timing.CallbackStarted);
            GuestMemoryProfile.RecordInterval(GuestMemoryProfile.Operation.RelayCallerCompletion,
                timing.CompletionStarted, callerResumed);
        }
        return true;
    }

    // Reject new work from other threads. The worker can still run accepted commands.
    public void StopAcceptingWork()
    {
        lock (_gate)
        {
            _accepting = false;
        }
    }
}
