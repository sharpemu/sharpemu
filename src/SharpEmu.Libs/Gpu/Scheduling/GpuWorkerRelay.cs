// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;

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

    public GpuWorkerRelay(Action wake, Func<Action, Action, bool>? tryEnqueueAfterPendingWork = null)
    {
        _wake = wake;
        _tryEnqueueAfterPendingWork = tryEnqueueAfterPendingWork;
    }

    public bool TryRunAfterPendingWork(Action work)
    {
        // Mapping calls may come from the guest render thread while an older GPU
        // queue is blocked on a label that only later work from that same thread
        // can produce. Waiting for that blocked submission here creates a cycle:
        // the render thread cannot submit the producer because it is waiting for
        // the barrier, and the barrier cannot run until the label is produced.
        // Run on the GPU worker instead. ApplyChange drains recorded host GPU work
        // before mutating the mapping, preserving the relay's original ordering.
        return TryRunOnGpuQueue(work);
    }

    // Video-out lifetime changes must remain behind command streams that were
    // already accepted. They cannot use TryRunAfterPendingWork: relay commands
    // run before the render loop parses its next command-stream slice, so an
    // unregister posted there can invalidate a display slot before an older
    // flip packet reserves it. Mapping updates deliberately keep the non-barrier
    // path above because waiting behind a label producer can deadlock them.
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

    public bool TryPost(Action work)
    {
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            _commands.Enqueue(work);
            Interlocked.Increment(ref _pendingCount);
        }

        _wake();
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
        ArgumentNullException.ThrowIfNull(work);
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        using var done = new SemaphoreSlim(0);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        if (!TryPost(() =>
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
            }))
        {
            return false;
        }

        done.Wait();
        failure?.Throw();
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
