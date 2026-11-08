// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ult;

// ULT synchronization objects (mutexes, condition variables, queues) and the resource pools that back them.
// On hardware, they park cooperative ulthreads on waiting queues carved out of the pool work areas;
// here ulthreads are guest threads, so every object lives on the host side keyed by its guest address
// and waits go through the scheduler's cooperative block.
public static partial class UltExports
{
    private const string SyncWakeKey = "sceUlt:sync";

    // The work areas only have to look plausible; see WorkAreaHeaderSize.
    private const ulong WaitingQueuePerThread = 0x40;
    private const ulong WaitingQueuePerSyncObject = 0x40;
    private const ulong QueueDataPerQueueObject = 0x80;

    private sealed class MutexState
    {
        public ulong Owner;
    }

    private sealed class ConditionWaiter
    {
        public bool Signaled;
    }

    private sealed class ConditionVariableState
    {
        public LinkedList<ConditionWaiter> Waiters { get; } = new();
    }

    private sealed class QueueState(ulong dataSize)
    {
        public ulong DataSize { get; } = dataSize;

        public Queue<byte[]> Items { get; } = new();
    }

    // One gate guards every ULT sync object:
    // contention is low and it keeps condition-variable waits (which touch a mutex too) free of lock ordering.
    private static readonly object SyncGate = new();
    private static readonly HashSet<ulong> ResourcePools = new();
    private static readonly Dictionary<ulong, MutexState> Mutexes = new();
    private static readonly Dictionary<ulong, ConditionVariableState> ConditionVariables = new();
    private static readonly Dictionary<ulong, QueueState> Queues = new();

    private static void ResetSyncState()
    {
        lock (SyncGate)
        {
            ResourcePools.Clear();
            Mutexes.Clear();
            ConditionVariables.Clear();
            Queues.Clear();
        }

        ResetWorkerState();
    }
    
    [SysAbiExport(
        Nid = "WIWV1Qd7PFU",
        ExportName = "sceUltWaitingQueueResourcePoolGetWorkAreaSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int WaitingQueueResourcePoolGetWorkAreaSize(CpuContext ctx)
    {
        var threads = (uint)ctx[CpuRegister.Rdi];
        var syncObjects = (uint)ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = WorkAreaHeaderSize +
            (threads * WaitingQueuePerThread) +
            (syncObjects * WaitingQueuePerSyncObject);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
    
    [SysAbiExport(
        Nid = "evj9YPkS8s4",
        ExportName = "sceUltQueueDataResourcePoolGetWorkAreaSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int QueueDataResourcePoolGetWorkAreaSize(CpuContext ctx)
    {
        var dataCount = (uint)ctx[CpuRegister.Rdi];
        var dataSize = ctx[CpuRegister.Rsi];
        var queueObjects = (uint)ctx[CpuRegister.Rdx];
        ctx[CpuRegister.Rax] = WorkAreaHeaderSize +
            (dataCount * ((dataSize + 15) & ~15UL)) +
            (queueObjects * QueueDataPerQueueObject);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LuLTRt0rfTw",
        ExportName = "_sceUltWaitingQueueResourcePoolOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "6gYjd50q0CE",
        ExportName = "_sceUltQueueDataResourcePoolOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "1+8t9aHLiz8",
        ExportName = "_sceUltMutexOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "RVmEia0vXMI",
        ExportName = "_sceUltConditionVariableOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "TkASc9I-xX0",
        ExportName = "_sceUltQueueOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int SyncOptParamInitialize(CpuContext ctx)
    {
        // These structs are smaller than the ulthread ones and the HLE objects never read them back,
        // so leave the guest's bytes untouched rather than risk zeroing past a stack-allocated block.
        return ctx.SetReturn(ctx[CpuRegister.Rdi] == 0 ? ErrorNull : 0, typeof(long));
    }
    
    [SysAbiExport(
        Nid = "YiHujOG9vXY",
        ExportName = "_sceUltWaitingQueueResourcePoolCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int WaitingQueueResourcePoolCreate(CpuContext ctx)
    {
        return CreateResourcePool(ctx, "waiting_queue_pool");
    }
    
    [SysAbiExport(
        Nid = "TFHm6-N6vks",
        ExportName = "_sceUltQueueDataResourcePoolCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int QueueDataResourcePoolCreate(CpuContext ctx)
    {
        return CreateResourcePool(ctx, "queue_data_pool");
    }

    private static int CreateResourcePool(CpuContext ctx, string kind)
    {
        var pool = ctx[CpuRegister.Rdi];
        if (pool == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var name = ReadName(ctx, ctx[CpuRegister.Rsi]);
        lock (SyncGate)
        {
            ResourcePools.Add(pool);
        }

        TraceUlt(
            $"{kind}_create: pool=0x{pool:X16} name='{name}' " +
            $"a2=0x{ctx[CpuRegister.Rdx]:X} a3=0x{ctx[CpuRegister.Rcx]:X} a4=0x{ctx[CpuRegister.R8]:X}");
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "or55417wcDk",
        ExportName = "sceUltWaitingQueueResourcePoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "dh11uAUWNyM",
        ExportName = "sceUltQueueDataResourcePoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int ResourcePoolDestroy(CpuContext ctx)
    {
        var pool = ctx[CpuRegister.Rdi];
        if (pool == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        lock (SyncGate)
        {
            return ctx.SetReturn(ResourcePools.Remove(pool) ? 0 : ErrorInvalid, typeof(long));
        }
    }
    
    [SysAbiExport(
        Nid = "mmt8Sa6tL6c",
        ExportName = "_sceUltMutexCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int MutexCreate(CpuContext ctx)
    {
        var mutex = ctx[CpuRegister.Rdi];
        if (mutex == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var name = ReadName(ctx, ctx[CpuRegister.Rsi]);
        lock (SyncGate)
        {
            Mutexes[mutex] = new MutexState();
        }

        TraceUlt($"mutex_create: mutex=0x{mutex:X16} name='{name}'");
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "jW+HnafeS3Y",
        ExportName = "sceUltMutexDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int MutexDestroy(CpuContext ctx)
    {
        var mutex = ctx[CpuRegister.Rdi];
        if (mutex == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        lock (SyncGate)
        {
            if (!Mutexes.TryGetValue(mutex, out var state))
            {
                return ctx.SetReturn(ErrorInvalid, typeof(long));
            }

            if (state.Owner != 0)
            {
                return ctx.SetReturn(ErrorBusy, typeof(long));
            }

            Mutexes.Remove(mutex);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "8hEGkR1pfr8",
        ExportName = "sceUltMutexLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int MutexLock(CpuContext ctx)
    {
        var mutex = ctx[CpuRegister.Rdi];
        if (mutex == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var self = CurrentSyncOwner();
        MutexState? state;
        lock (SyncGate)
        {
            state = GetMutex(mutex);
            if (state.Owner == self)
            {
                return ctx.SetReturn(ErrorPermission, typeof(long));
            }
        }

        return BlockUntil(ctx, "sceUltMutexLock", () => TryAcquireMutex(state, self));
    }

    [SysAbiExport(
        Nid = "jOsUG0BJI-Y",
        ExportName = "sceUltMutexTryLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int MutexTryLock(CpuContext ctx)
    {
        var mutex = ctx[CpuRegister.Rdi];
        if (mutex == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var self = CurrentSyncOwner();
        lock (SyncGate)
        {
            var state = GetMutex(mutex);
            if (state.Owner == self)
            {
                return ctx.SetReturn(ErrorPermission, typeof(long));
            }

            return ctx.SetReturn(TryAcquireMutex(state, self) ? 0 : ErrorBusy, typeof(long));
        }
    }

    [SysAbiExport(
        Nid = "h0XebKiMBtk",
        ExportName = "sceUltMutexUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int MutexUnlock(CpuContext ctx)
    {
        var mutex = ctx[CpuRegister.Rdi];
        if (mutex == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var self = CurrentSyncOwner();
        lock (SyncGate)
        {
            if (!Mutexes.TryGetValue(mutex, out var state) || state.Owner != self)
            {
                return ctx.SetReturn(ErrorPermission, typeof(long));
            }

            state.Owner = 0;
        }

        WakeSyncWaiters();
        return ctx.SetReturn(0, typeof(long));
    }
    
    [SysAbiExport(
        Nid = "jnKaHGkrxZ4",
        ExportName = "_sceUltConditionVariableCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int ConditionVariableCreate(CpuContext ctx)
    {
        var conditionVariable = ctx[CpuRegister.Rdi];
        if (conditionVariable == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var name = ReadName(ctx, ctx[CpuRegister.Rsi]);
        lock (SyncGate)
        {
            ConditionVariables[conditionVariable] = new ConditionVariableState();
        }

        TraceUlt($"cond_create: cv=0x{conditionVariable:X16} name='{name}'");
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "xrmmI832R4U",
        ExportName = "sceUltConditionVariableDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int ConditionVariableDestroy(CpuContext ctx)
    {
        var conditionVariable = ctx[CpuRegister.Rdi];
        if (conditionVariable == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        lock (SyncGate)
        {
            if (!ConditionVariables.TryGetValue(conditionVariable, out var state))
            {
                return ctx.SetReturn(ErrorInvalid, typeof(long));
            }

            if (state.Waiters.Count != 0)
            {
                return ctx.SetReturn(ErrorBusy, typeof(long));
            }

            ConditionVariables.Remove(conditionVariable);
        }

        return ctx.SetReturn(0, typeof(long));
    }
    
    [SysAbiExport(
        Nid = "5xGAHCxA8M0",
        ExportName = "sceUltConditionVariableWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int ConditionVariableWait(CpuContext ctx)
    {
        var conditionVariable = ctx[CpuRegister.Rdi];
        var mutex = ctx[CpuRegister.Rsi];
        if (conditionVariable == 0 || mutex == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var self = CurrentSyncOwner();
        var waiter = new ConditionWaiter();
        MutexState mutexState;
        lock (SyncGate)
        {
            if (!Mutexes.TryGetValue(mutex, out mutexState!) || mutexState.Owner != self)
            {
                return ctx.SetReturn(ErrorPermission, typeof(long));
            }

            GetConditionVariable(conditionVariable).Waiters.AddLast(waiter);
            mutexState.Owner = 0;
        }

        WakeSyncWaiters();

        // The wait completes once a signal has picked this waiter and the mutex could be re-acquired,
        // mirroring pthread_cond_wait.
        return BlockUntil(
            ctx,
            "sceUltConditionVariableWait",
            () => waiter.Signaled && TryAcquireMutex(mutexState, self));
    }

    [SysAbiExport(
        Nid = "JTw1cAVkuc0",
        ExportName = "sceUltConditionVariableSignal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int ConditionVariableSignal(CpuContext ctx)
    {
        return SignalCore(ctx, all: false);
    }

    [SysAbiExport(
        Nid = "byiceqcMvV0",
        ExportName = "sceUltConditionVariableSignalAll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int ConditionVariableSignalAll(CpuContext ctx)
    {
        return SignalCore(ctx, all: true);
    }

    private static int SignalCore(CpuContext ctx, bool all)
    {
        var conditionVariable = ctx[CpuRegister.Rdi];
        if (conditionVariable == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        var woke = false;
        lock (SyncGate)
        {
            var waiters = GetConditionVariable(conditionVariable).Waiters;
            while (waiters.First is { } first)
            {
                first.Value.Signaled = true;
                waiters.RemoveFirst();
                woke = true;
                if (!all)
                {
                    break;
                }
            }
        }

        if (woke)
        {
            WakeSyncWaiters();
        }

        return ctx.SetReturn(0, typeof(long));
    }
    
    [SysAbiExport(
        Nid = "9Y5keOvb6ok",
        ExportName = "_sceUltQueueCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int QueueCreate(CpuContext ctx)
    {
        var queue = ctx[CpuRegister.Rdi];
        var dataSize = ctx[CpuRegister.Rdx];
        if (queue == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        if (dataSize == 0 || dataSize > 0x10000)
        {
            return ctx.SetReturn(ErrorInvalid, typeof(long));
        }

        var name = ReadName(ctx, ctx[CpuRegister.Rsi]);
        lock (SyncGate)
        {
            Queues[queue] = new QueueState(dataSize);
        }

        TraceUlt($"queue_create: queue=0x{queue:X16} name='{name}' data_size=0x{dataSize:X}");
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "PP9nZxpSKLY",
        ExportName = "sceUltQueueDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int QueueDestroy(CpuContext ctx)
    {
        var queue = ctx[CpuRegister.Rdi];
        if (queue == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        lock (SyncGate)
        {
            return ctx.SetReturn(Queues.Remove(queue) ? 0 : ErrorInvalid, typeof(long));
        }
    }

    // The queue is unbounded: hardware blocks a push once the data pool is exhausted,
    // but never blocking cannot deadlock a guest that relies on that back-pressure,
    // so both push flavors complete immediately.
    [SysAbiExport(
        Nid = "dUwpX3e5NDE",
        ExportName = "sceUltQueuePush",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    [SysAbiExport(
        Nid = "6Mc2Xs7pI1I",
        ExportName = "sceUltQueueTryPush",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int QueuePush(CpuContext ctx)
    {
        var queue = ctx[CpuRegister.Rdi];
        var data = ctx[CpuRegister.Rsi];
        if (queue == 0 || data == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        QueueState? state;
        lock (SyncGate)
        {
            if (!Queues.TryGetValue(queue, out state))
            {
                return ctx.SetReturn(ErrorInvalid, typeof(long));
            }
        }

        var item = new byte[state.DataSize];
        if (!ctx.Memory.TryRead(data, item))
        {
            return ctx.SetReturn(ErrorInvalid, typeof(long));
        }

        lock (SyncGate)
        {
            state.Items.Enqueue(item);
        }

        WakeSyncWaiters();
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "RVSq2tsm2yw",
        ExportName = "sceUltQueuePop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int QueuePop(CpuContext ctx)
    {
        return PopCore(ctx, tryOnly: false);
    }

    [SysAbiExport(
        Nid = "uZz3ci7XYqc",
        ExportName = "sceUltQueueTryPop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUlt")]
    public static int QueueTryPop(CpuContext ctx)
    {
        return PopCore(ctx, tryOnly: true);
    }

    private static int PopCore(CpuContext ctx, bool tryOnly)
    {
        var queue = ctx[CpuRegister.Rdi];
        var data = ctx[CpuRegister.Rsi];
        if (queue == 0 || data == 0)
        {
            return ctx.SetReturn(ErrorNull, typeof(long));
        }

        QueueState? state;
        lock (SyncGate)
        {
            if (!Queues.TryGetValue(queue, out state))
            {
                return ctx.SetReturn(ErrorInvalid, typeof(long));
            }

            if (tryOnly)
            {
                return ctx.SetReturn(TryDequeueInto(ctx.Memory, state, data) ? 0 : ErrorBusy, typeof(long));
            }
        }

        var memory = ctx.Memory;
        return BlockUntil(ctx, "sceUltQueuePop", () => TryDequeueInto(memory, state, data));
    }

    private static bool TryDequeueInto(ICpuMemory memory, QueueState state, ulong destination)
    {
        lock (SyncGate)
        {
            if (!state.Items.TryDequeue(out var item))
            {
                return false;
            }

            memory.TryWrite(destination, item);
            return true;
        }
    }

    private static bool TryAcquireMutex(MutexState state, ulong owner)
    {
        lock (SyncGate)
        {
            if (state.Owner != 0)
            {
                return false;
            }

            state.Owner = owner;
            return true;
        }
    }
    
    private static MutexState GetMutex(ulong mutex)
    {
        if (!Mutexes.TryGetValue(mutex, out var state))
        {
            state = new MutexState();
            Mutexes[mutex] = state;
        }

        return state;
    }

    private static ConditionVariableState GetConditionVariable(ulong conditionVariable)
    {
        if (!ConditionVariables.TryGetValue(conditionVariable, out var state))
        {
            state = new ConditionVariableState();
            ConditionVariables[conditionVariable] = state;
        }

        return state;
    }

    private static ulong CurrentSyncOwner()
    {
        var handle = GuestThreadExecution.CurrentGuestThreadHandle;
        return handle != 0 ? handle : (ulong)(uint)Environment.CurrentManagedThreadId | (1UL << 63);
    }

    private static void WakeSyncWaiters()
    {
        lock (SyncGate)
        {
            Monitor.PulseAll(SyncGate);
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(SyncWakeKey);
    }
    
    private static int BlockUntil(CpuContext ctx, string reason, Func<bool> tryComplete)
    {
        if (tryComplete())
        {
            return ctx.SetReturn(0, typeof(long));
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                reason,
                SyncWakeKey,
                static () => 0,
                tryComplete))
        {
            // A wake may have landed before the scheduler registered the block.
            if (tryComplete())
            {
                GuestThreadExecution.TryConsumeCurrentThreadBlock(out _);
            }

            return ctx.SetReturn(0, typeof(long));
        }

        lock (SyncGate)
        {
            while (!tryComplete())
            {
                Monitor.Wait(SyncGate, 10);
            }
        }

        return ctx.SetReturn(0, typeof(long));
    }
}
