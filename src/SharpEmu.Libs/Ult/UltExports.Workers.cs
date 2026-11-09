// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Ult;

// Ulthread worker pool. Games create and join ulthreads by the hundreds per second (one per job),
// so starting a guest thread for each one floods the host with short-lived threads.
// Instead every runtime keeps recyclable guest threads: when an ulthread's entry returns,
// its worker parks and takes the next ulthread from a fresh frame.
// A worker that blocks inside an ulthread stays with it, so the pool grows on demand
// (up to the runtime's ulthread limit) rather than letting queued ulthreads deadlock behind blocked ones.
public static partial class UltExports
{
    private sealed class WorkerState(RuntimeState runtime)
    {
        public RuntimeState Runtime { get; } = runtime;

        public UlthreadState? Current;
    }

    private sealed class WorkerRecycler : IGuestThreadRecycler
    {
        public void OnEntryReturned(ulong threadHandle, ulong returnValue)
        {
            OnWorkerEntryReturned(threadHandle, returnValue);
        }
    }

    private static readonly WorkerRecycler Recycler = new();
    
    private static readonly Dictionary<ulong, WorkerState> Workers = new();

    private static void ResetWorkerState()
    {
        lock (SyncGate)
        {
            Workers.Clear();
        }
    }

    private static UlthreadState? CurrentUlthread()
    {
        lock (SyncGate)
        {
            return Workers.TryGetValue(GuestThreadExecution.CurrentGuestThreadHandle, out var worker)
                ? worker.Current
                : null;
        }
    }

    private static bool DispatchUlthread(
        CpuContext ctx,
        IGuestThreadScheduler scheduler,
        RuntimeState runtime,
        UlthreadState ulthread,
        out string? error)
    {
        while (true)
        {
            ulong idleWorker;
            lock (SyncGate)
            {
                if (!runtime.IdleWorkers.TryPop(out idleWorker))
                {
                    if (runtime.Workers.Count >= runtime.MaxUlthreads)
                    {
                        runtime.PendingUlthreads.Enqueue(ulthread);
                        error = null;
                        return true;
                    }

                    break;
                }

                AssignLocked(idleWorker, ulthread);
            }

            if (scheduler.TryRestartParkedThread(idleWorker, ulthread.Entry, ulthread.Argument, out error))
            {
                return true;
            }

            // A worker that cannot restart is dropped; try the next one.
            Console.Error.WriteLine(
                $"[LOADER][WARN] ult: dropping worker 0x{idleWorker:X16} of '{runtime.Name}': {error}");
            lock (SyncGate)
            {
                Workers.Remove(idleWorker);
                runtime.Workers.Remove(idleWorker);
                ulthread.WorkerHandle = 0;
            }
        }

        return TrySpawnWorker(ctx, scheduler, runtime, ulthread, out error);
    }

    private static bool TrySpawnWorker(
        CpuContext ctx,
        IGuestThreadScheduler scheduler,
        RuntimeState runtime,
        UlthreadState ulthread,
        out string? error)
    {
        string name;
        lock (SyncGate)
        {
            name = $"{runtime.Name}Worker{runtime.Workers.Count}";
        }

        var threadHandle = KernelPthreadState.CreateThreadHandle(name);
        KernelPthreadExtendedCompatExports.RegisterThreadStart(
            threadHandle,
            name,
            UlthreadPriority,
            UlthreadAffinityMask);

        lock (SyncGate)
        {
            Workers[threadHandle] = new WorkerState(runtime);
            runtime.Workers.Add(threadHandle);
            AssignLocked(threadHandle, ulthread);
        }

        TraceUlt($"worker_spawn: runtime=0x{runtime.Address:X16} worker=0x{threadHandle:X16} name='{name}'");
        var request = new GuestThreadStartRequest(
            threadHandle,
            ulthread.Entry,
            ulthread.Argument,
            AttributeAddress: 0,
            name,
            UlthreadPriority,
            UlthreadAffinityMask,
            Recycler: Recycler);
        if (scheduler.TryStartThread(ctx, request, out error))
        {
            return true;
        }

        lock (SyncGate)
        {
            Workers.Remove(threadHandle);
            runtime.Workers.Remove(threadHandle);
            ulthread.WorkerHandle = 0;
        }

        return false;
    }
    
    private static void AssignLocked(ulong workerHandle, UlthreadState ulthread)
    {
        Workers[workerHandle].Current = ulthread;
        ulthread.WorkerHandle = workerHandle;
    }

    private static void OnWorkerEntryReturned(ulong workerHandle, ulong returnValue)
    {
        UlthreadState? next = null;
        lock (SyncGate)
        {
            if (!Workers.TryGetValue(workerHandle, out var worker))
            {
                return;
            }

            if (worker.Current is { } finished)
            {
                finished.ExitValue = returnValue;
                finished.Done = true;
                worker.Current = null;
            }

            if (worker.Runtime.PendingUlthreads.TryDequeue(out next))
            {
                AssignLocked(workerHandle, next);
            }
            else
            {
                worker.Runtime.IdleWorkers.Push(workerHandle);
            }
        }

        WakeSyncWaiters();
        if (next is null)
        {
            return;
        }

        if (GuestThreadExecution.Scheduler is { } scheduler &&
            scheduler.TryRestartParkedThread(workerHandle, next.Entry, next.Argument, out var error))
        {
            return;
        }

        // Nothing can run the queued ulthread; complete it so joiners do not hang.
        Console.Error.WriteLine(
            $"[LOADER][ERROR] ult: failed to restart worker 0x{workerHandle:X16} for '{next.Name}'");
        lock (SyncGate)
        {
            next.Done = true;
            if (Workers.Remove(workerHandle, out var dropped))
            {
                dropped.Runtime.Workers.Remove(workerHandle);
            }
        }

        WakeSyncWaiters();
    }
}
