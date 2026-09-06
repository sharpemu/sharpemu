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
    private int _pendingCount;
    private bool _accepting = true;

    public GpuWorkerRelay(Action wake) => _wake = wake;

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
        if (IsGpuQueueThread)
        {
            work();
            return true;
        }

        using var done = new SemaphoreSlim(0);
        if (!TryPost(() =>
            {
                work();
                done.Release();
            }))
        {
            return false;
        }

        done.Wait();
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
