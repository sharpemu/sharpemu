// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using System.Diagnostics.CodeAnalysis;

namespace SharpEmu.Libs.Kernel;

public static class KernelPthreadCompatExports
{
    private const int MutexTypeErrorCheck = 1;
    private const int MutexTypeRecursive = 2;
    private const int MutexTypeNormal = 3;
    private const int MutexTypeAdaptiveNp = 4;
    private const ulong StaticAdaptiveMutexInitializer = 1;
    private const int MutexObjectSize = 0x100;
    private const int MutexAttrObjectSize = 0x40;
    private const int CondObjectSize = 0x100;
    private const int PthreadOnceUninitialized = 0;
    private const int PthreadOnceInProgress = 1;
    private const int PthreadOnceDone = 2;

    private static readonly object _stateGate = new();
    private static readonly ConcurrentDictionary<ulong, PthreadMutexState> _mutexStates = new();
    private static readonly Dictionary<ulong, PthreadMutexAttrState> _mutexAttrStates = new();
    private static readonly Dictionary<ulong, PthreadCondState> _condStates = new();
    private static readonly Dictionary<ulong, object> _onceGates = new();
    private static readonly HashSet<ulong> _condAttrStates = new();
    private static readonly bool _tracePthreads =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREADS"), "1", StringComparison.Ordinal);
    private static readonly bool _tracePthreadConds =
        _tracePthreads ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_CONDS"), "1", StringComparison.Ordinal);
    private static readonly HashSet<ulong>? _tracePthreadMutexFilter = ParseTraceAddressFilter(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_PTHREAD_MUTEX_FILTER"));
    private static long _nextSynchronizationWaiterId;

    private sealed class PthreadMutexState
    {
        public ulong OwnerThreadId { get; set; }
        public int RecursionCount { get; set; }
        public int Type { get; set; } = MutexTypeErrorCheck;
        public int Protocol { get; set; }
        public LinkedList<PthreadMutexWaiter> Waiters { get; } = new();
    }

    private sealed class PthreadMutexWaiter
    {
        public required ulong ThreadId { get; init; }
        public required string WakeKey { get; init; }
        public required bool Cooperative { get; init; }
        public LinkedListNode<PthreadMutexWaiter>? Node { get; set; }
        public int Granted;
    }

    private sealed class PthreadCondState
    {
        public object SyncRoot { get; } = new();
        public LinkedList<PthreadCondWaiter> WaiterQueue { get; } = new();
        public ulong SignalEpoch { get; set; }
        public int Waiters { get; set; }
    }

    private sealed class PthreadCondWaiter
    {
        public required ulong ThreadId { get; init; }
        public required PthreadMutexState MutexState { get; init; }
        public required string WakeKey { get; init; }
        public required bool Cooperative { get; init; }
        public bool PosixErrors { get; init; }
        public LinkedListNode<PthreadCondWaiter>? Node { get; set; }
        public PthreadMutexWaiter? MutexWaiter { get; set; }
        public Timer? TimeoutTimer { get; set; }
        // 0 = waiting, 1 = signaled, 2 = timed out.
        public int CompletionState { get; set; }
    }

    private readonly record struct PthreadMutexAttrState(int Type, int Protocol);

    static KernelPthreadCompatExports()
    {
        RunSynchronizationSelfChecks();
    }

    [SysAbiExport(
        Nid = "aI+OeCz8xrQ",
        ExportName = "scePthreadSelf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSelf(CpuContext ctx)
    {
        var currentThreadHandle = KernelPthreadState.GetCurrentThreadHandle();
        GuestThreadExecution.Scheduler?.RegisterGuestThreadContext(currentThreadHandle, ctx);
        ctx[CpuRegister.Rax] = currentThreadHandle;
        TracePthreadSelf(ctx, currentThreadHandle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EotR8a3ASf4",
        ExportName = "pthread_self",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadSelf(CpuContext ctx) => PthreadSelf(ctx);

    [SysAbiExport(
        Nid = "3PtV6p3QNX4",
        ExportName = "scePthreadEqual",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadEqual(CpuContext ctx)
    {
        var left = ctx[CpuRegister.Rdi];
        var right = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = left == right ? 1UL : 0UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "7Xl257M4VNI",
        ExportName = "pthread_equal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int PosixPthreadEqual(CpuContext ctx) => PthreadEqual(ctx);

    [SysAbiExport(
        Nid = "T72hz6ffq08",
        ExportName = "scePthreadYield",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadYield(CpuContext ctx)
    {
        _ = ctx;
        Thread.Yield();
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GBUY7ywdULE",
        ExportName = "scePthreadRename",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadRename(CpuContext ctx)
    {
        if (_tracePthreads)
        {
            var nameAddress = ctx[CpuRegister.Rsi];
            Span<byte> nameBytes = stackalloc byte[64];
            var name = "<unreadable>";
            if (nameAddress != 0 && ctx.Memory.TryRead(nameAddress, nameBytes))
            {
                var length = nameBytes.IndexOf((byte)0);
                name = System.Text.Encoding.UTF8.GetString(length >= 0 ? nameBytes[..length] : nameBytes);
            }
            Console.Error.WriteLine(
                $"[LOADER][TRACE] pthread.rename thread=0x{ctx[CpuRegister.Rdi]:X16} name=\"{name}\"");
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EI-5-jlq2dE",
        ExportName = "scePthreadGetthreadid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadGetthreadid(CpuContext ctx) => PthreadGetthreadidCore(ctx);

    [SysAbiExport(
        Nid = "3eqs37G74-s",
        ExportName = "pthread_getthreadid_np",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadGetthreadidNp(CpuContext ctx) => PthreadGetthreadidCore(ctx);

    [SysAbiExport(
        Nid = "cmo1RIYva9o",
        ExportName = "scePthreadMutexInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexInit(CpuContext ctx) => PthreadMutexInitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "2Of0f+3mhhE",
        ExportName = "scePthreadMutexDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexDestroy(CpuContext ctx) => PthreadMutexDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "9UK1vLZQft4",
        ExportName = "scePthreadMutexLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexLock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: false);

    [SysAbiExport(
        Nid = "upoVrzMHFeE",
        ExportName = "scePthreadMutexTrylock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexTrylock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: true);

    [SysAbiExport(
        Nid = "tn3VlD0hG60",
        ExportName = "scePthreadMutexUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexUnlock(CpuContext ctx) => PthreadMutexUnlockCore(ctx, ctx[CpuRegister.Rdi], requireOwner: true);

    [SysAbiExport(
        Nid = "ttHNfU+qDBU",
        ExportName = "pthread_mutex_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexInit(CpuContext ctx) => PthreadMutexInitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "ltCfaGr2JGE",
        ExportName = "pthread_mutex_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexDestroy(CpuContext ctx) => PthreadMutexDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "7H0iTOciTLo",
        ExportName = "pthread_mutex_lock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexLock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: false);

    [SysAbiExport(
        Nid = "K-jXhbt2gn4",
        ExportName = "pthread_mutex_trylock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexTrylock(CpuContext ctx) => PthreadMutexLockCore(ctx, ctx[CpuRegister.Rdi], tryOnly: true);

    [SysAbiExport(
        Nid = "2Z+PpY6CaJg",
        ExportName = "pthread_mutex_unlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexUnlock(CpuContext ctx) => PthreadMutexUnlockCore(ctx, ctx[CpuRegister.Rdi], requireOwner: true);

    private static int PthreadGetthreadidCore(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = KernelPthreadState.GetCurrentThreadUniqueId();
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "F8bUHwAG284",
        ExportName = "scePthreadMutexattrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrInit(CpuContext ctx) => PthreadMutexattrInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "smWEktiyyG0",
        ExportName = "scePthreadMutexattrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrDestroy(CpuContext ctx) => PthreadMutexattrDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "iMp8QpE+XO4",
        ExportName = "scePthreadMutexattrSettype",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrSettype(CpuContext ctx) => PthreadMutexattrSettypeCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "1FGvU0i9saQ",
        ExportName = "scePthreadMutexattrSetprotocol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadMutexattrSetprotocol(CpuContext ctx) => PthreadMutexattrSetprotocolCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "dQHWEsJtoE4",
        ExportName = "pthread_mutexattr_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrInit(CpuContext ctx) => PthreadMutexattrInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "HF7lK46xzjY",
        ExportName = "pthread_mutexattr_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrDestroy(CpuContext ctx) => PthreadMutexattrDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "mDmgMOGVUqg",
        ExportName = "pthread_mutexattr_settype",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrSettype(CpuContext ctx) => PthreadMutexattrSettypeCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "5txKfcMUAok",
        ExportName = "pthread_mutexattr_setprotocol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadMutexattrSetprotocol(CpuContext ctx) => PthreadMutexattrSetprotocolCore(ctx, ctx[CpuRegister.Rdi], unchecked((int)ctx[CpuRegister.Rsi]));

    [SysAbiExport(
        Nid = "2Tb92quprl0",
        ExportName = "scePthreadCondInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondInit(CpuContext ctx) => PthreadCondInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "0TyVk4MSLt0",
        ExportName = "pthread_cond_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondInit(CpuContext ctx) => PthreadCondInitCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "g+PZd2hiacg",
        ExportName = "scePthreadCondDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondDestroy(CpuContext ctx) => PthreadCondDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "RXXqi4CtF8w",
        ExportName = "pthread_cond_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondDestroy(CpuContext ctx) => PthreadCondDestroyCore(ctx, ctx[CpuRegister.Rdi]);

    [SysAbiExport(
        Nid = "WKAXJ4XBPQ4",
        ExportName = "scePthreadCondWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondWait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: false);

    [SysAbiExport(
        Nid = "BmMjYxmew1w",
        ExportName = "scePthreadCondTimedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondTimedwait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: true, timeoutUsec: unchecked((uint)ctx[CpuRegister.Rdx]));

    [SysAbiExport(
        Nid = "kDh-NfxgMtE",
        ExportName = "scePthreadCondSignal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondSignal(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: false);

    [SysAbiExport(
        Nid = "JGgj7Uvrl+A",
        ExportName = "scePthreadCondBroadcast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondBroadcast(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: true);

    [SysAbiExport(
        Nid = "Op8TBGY5KHg",
        ExportName = "pthread_cond_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondWait(CpuContext ctx) => PthreadCondWaitCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], timed: false);

    [SysAbiExport(
        Nid = "27bAgiJmOh0",
        ExportName = "pthread_cond_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondTimedwait(CpuContext ctx)
    {
        var deadlineAddress = ctx[CpuRegister.Rdx];
        if (deadlineAddress == 0 ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, deadlineAddress, out var rawSeconds) ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(
                ctx,
                deadlineAddress + sizeof(long),
                out var rawNanoseconds))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var seconds = unchecked((long)rawSeconds);
        var nanoseconds = unchecked((long)rawNanoseconds);
        if (seconds < 0 || nanoseconds is < 0 or >= 1_000_000_000)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var now = DateTimeOffset.UtcNow;
        var deltaSeconds = seconds - now.ToUnixTimeSeconds();
        var nowNanoseconds = (now.Ticks % TimeSpan.TicksPerSecond) * 100L;
        uint timeoutUsec;
        if (deltaSeconds < 0)
        {
            timeoutUsec = 0;
        }
        else if (deltaSeconds > uint.MaxValue / 1_000_000L + 1)
        {
            timeoutUsec = uint.MaxValue;
        }
        else
        {
            var remainingNanoseconds =
                deltaSeconds * 1_000_000_000L + nanoseconds - nowNanoseconds;
            var remainingUsec = remainingNanoseconds <= 0
                ? 0
                : (remainingNanoseconds + 999L) / 1_000L;
            timeoutUsec = (uint)Math.Min(remainingUsec, uint.MaxValue);
        }

        return PthreadCondWaitCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            timed: true,
            timeoutUsec,
            posixErrors: true);
    }

    [SysAbiExport(
        Nid = "mkx2fVhNMsg",
        ExportName = "pthread_cond_broadcast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondBroadcast(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: true);

    [SysAbiExport(
        Nid = "2MOy+rUfuhQ",
        ExportName = "pthread_cond_signal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadCondSignal(CpuContext ctx) => PthreadCondSignalCore(ctx, ctx[CpuRegister.Rdi], broadcast: false);

    [SysAbiExport(
        Nid = "m5-2bsNfv7s",
        ExportName = "scePthreadCondattrInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondattrInit(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            _condAttrStates.Add(attrAddress);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "waPcxYiR3WA",
        ExportName = "scePthreadCondattrDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadCondattrDestroy(CpuContext ctx)
    {
        var attrAddress = ctx[CpuRegister.Rdi];
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (_stateGate)
        {
            _condAttrStates.Remove(attrAddress);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "14bOACANTBo",
        ExportName = "scePthreadOnce",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadOnce(CpuContext ctx)
    {
        var onceAddress = ctx[CpuRegister.Rdi];
        var initRoutine = ctx[CpuRegister.Rsi];
        if (onceAddress == 0 || initRoutine == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadInt32(ctx, onceAddress, out var onceValue))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (onceValue == PthreadOnceDone)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var gate = GetPthreadOnceGate(onceAddress);
        var shouldCall = false;
        lock (gate)
        {
            if (!TryReadInt32(ctx, onceAddress, out onceValue))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            while (onceValue == PthreadOnceInProgress)
            {
                Monitor.Wait(gate, TimeSpan.FromMilliseconds(1));
                if (!TryReadInt32(ctx, onceAddress, out onceValue))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }

            if (onceValue != PthreadOnceDone)
            {
                if (!TryWriteInt32(ctx, onceAddress, PthreadOnceInProgress))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                shouldCall = true;
            }
        }

        if (shouldCall)
        {
            var scheduler = GuestThreadExecution.Scheduler;
            string? error = null;
            if (scheduler is null ||
                !scheduler.TryCallGuestFunction(ctx, initRoutine, 0, 0, 0, 0, "pthread_once", out error))
            {
                lock (gate)
                {
                    _ = TryWriteInt32(ctx, onceAddress, PthreadOnceUninitialized);
                    Monitor.PulseAll(gate);
                }

                TracePthreadOnce(onceAddress, initRoutine, "failed", error);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
            }

            lock (gate)
            {
                if (!TryWriteInt32(ctx, onceAddress, PthreadOnceDone))
                {
                    _ = TryWriteInt32(ctx, onceAddress, PthreadOnceUninitialized);
                    Monitor.PulseAll(gate);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                Monitor.PulseAll(gate);
            }
        }

        TracePthreadOnce(onceAddress, initRoutine, shouldCall ? "call" : "done", null);
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static int PthreadMutexInitCore(CpuContext ctx, ulong mutexAddress, ulong attrAddress)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var attr = ResolveMutexAttrState(ctx, attrAddress);
        var state = new PthreadMutexState
        {
            Type = attr.Type,
            Protocol = attr.Protocol,
        };

        if (!TryAllocateOpaqueObject(ctx, MutexObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        if (!InitializeMutexObject(ctx, handle, state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _mutexStates[mutexAddress] = state;
        _mutexStates[handle] = state;

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, handle))
        {
            _mutexStates.TryRemove(mutexAddress, out _);
            _mutexStates.TryRemove(handle, out _);

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexDestroyCore(CpuContext ctx, ulong mutexAddress)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexHandle(ctx, mutexAddress);
        if (!_mutexStates.TryGetValue(resolvedAddress, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (state)
        {
            if (state.OwnerThreadId != 0 || state.RecursionCount != 0 || state.Waiters.Count != 0)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            }

            _mutexStates.TryRemove(resolvedAddress, out _);
            if (resolvedAddress != mutexAddress)
            {
                _mutexStates.TryRemove(mutexAddress, out _);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, 0);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexLockCore(CpuContext ctx, ulong mutexAddress, bool tryOnly)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out var resolvedAddress, out var state))
        {
            TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, null, KernelPthreadState.GetCurrentThreadHandle(), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        var canCooperativelyBlock = !tryOnly &&
            GuestThreadExecution.IsGuestThread &&
            GuestThreadExecution.TryGetCurrentImportCallFrame(out _);
        PthreadMutexWaiter? waiter = null;
        lock (state)
        {
            if (state.OwnerThreadId == currentThreadId)
            {
                if (state.Type == MutexTypeRecursive)
                {
                    state.RecursionCount++;
                    TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }

                if (state.Type is MutexTypeNormal or MutexTypeAdaptiveNp)
                {
                    if (tryOnly)
                    {
                        TracePthreadMutex(ctx, "trylock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
                    }

                    // FreeBSD maps NORMAL to checked non-recursive behavior:
                    // self-lock is an error, never implicit recursion.
                    TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK;
                }
                else
                {
                    var ownedResult = tryOnly
                        ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY
                        : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK;
                    TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, ownedResult);
                    return ownedResult;
                }
            }

            if (state.OwnerThreadId == 0 && state.Waiters.Count == 0)
            {
                state.OwnerThreadId = currentThreadId;
                state.RecursionCount = 1;
                TracePthreadMutex(ctx, tryOnly ? "trylock" : "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (tryOnly)
            {
                TracePthreadMutex(ctx, "trylock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
            }

            waiter = EnqueueMutexWaiterLocked(state, currentThreadId, canCooperativelyBlock);
        }

        if (canCooperativelyBlock && waiter is not null &&
            GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "pthread_mutex_lock",
                waiter.WakeKey,
                () => CompleteBlockedMutexLock(ctx, mutexAddress, resolvedAddress, state, waiter),
                () => TryGrantBlockedMutexLock(ctx, mutexAddress, resolvedAddress, state, waiter)))
        {
            TracePthreadMutex(ctx, "lock-block", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var hostResult = WaitForHostMutexLock(state, waiter!);
        TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, hostResult);
        return hostResult;
    }

    private static int PthreadMutexUnlockCore(CpuContext ctx, ulong mutexAddress, bool requireOwner)
    {
        if (mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out var resolvedAddress, out var state))
        {
            TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, null, KernelPthreadState.GetCurrentThreadHandle(), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        string? nextWakeKey = null;
        lock (state)
        {
            if (state.RecursionCount <= 0)
            {
                TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }

            if (requireOwner && state.OwnerThreadId != currentThreadId)
            {
                TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }

            state.RecursionCount--;
            if (state.RecursionCount == 0)
            {
                state.OwnerThreadId = 0;
                nextWakeKey = state.Waiters.First?.Value.Cooperative == true
                    ? state.Waiters.First.Value.WakeKey
                    : null;
                Monitor.PulseAll(state);
            }
        }

        if (nextWakeKey is not null)
        {
            _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(nextWakeKey, 1);
        }

        TracePthreadMutex(ctx, "unlock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrInitCore(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryAllocateOpaqueObject(ctx, MutexAttrObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var initialState = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        if (!WriteMutexAttrObject(ctx, handle, initialState))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            _mutexAttrStates[attrAddress] = initialState;
            _mutexAttrStates[handle] = initialState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, attrAddress, handle))
        {
            lock (_stateGate)
            {
                _mutexAttrStates.Remove(attrAddress);
                _mutexAttrStates.Remove(handle);
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrDestroyCore(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            _mutexAttrStates.Remove(resolvedAddress);
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates.Remove(attrAddress);
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadMutexattrSettypeCore(CpuContext ctx, ulong attrAddress, int type)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        PthreadMutexAttrState updatedState;
        lock (_stateGate)
        {
            if (!_mutexAttrStates.TryGetValue(resolvedAddress, out var state))
            {
                state = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
            }

            updatedState = state with { Type = NormalizeMutexType(type) };
            _mutexAttrStates[resolvedAddress] = updatedState;
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates[attrAddress] = updatedState;
            }
        }

        return WriteMutexAttrObject(ctx, resolvedAddress, updatedState)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static int PthreadMutexattrSetprotocolCore(CpuContext ctx, ulong attrAddress, int protocol)
    {
        if (attrAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        PthreadMutexAttrState updatedState;
        lock (_stateGate)
        {
            if (!_mutexAttrStates.TryGetValue(resolvedAddress, out var state))
            {
                state = new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
            }

            updatedState = state with { Protocol = protocol };
            _mutexAttrStates[resolvedAddress] = updatedState;
            if (resolvedAddress != attrAddress)
            {
                _mutexAttrStates[attrAddress] = updatedState;
            }
        }

        return WriteMutexAttrObject(ctx, resolvedAddress, updatedState)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static ulong ResolveMutexHandle(CpuContext ctx, ulong mutexAddress)
    {
        if (mutexAddress == 0)
        {
            return 0;
        }

        if (_mutexStates.ContainsKey(mutexAddress))
        {
            return mutexAddress;
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var pointedHandle) && pointedHandle != 0)
        {
            if (_mutexStates.ContainsKey(pointedHandle))
            {
                return pointedHandle;
            }
        }

        return mutexAddress;
    }

    private static bool TryResolveMutexState(CpuContext ctx, ulong mutexAddress, bool createIfZero, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadMutexState? state)
    {
        resolvedAddress = 0;
        state = null;
        if (mutexAddress == 0)
        {
            return false;
        }

        if (_mutexStates.TryGetValue(mutexAddress, out state))
        {
            resolvedAddress = mutexAddress;
            return true;
        }

        if (!KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var pointedHandle))
        {
            return false;
        }

        if (pointedHandle == StaticAdaptiveMutexInitializer)
        {
            return CreateImplicitMutexState(ctx, mutexAddress, MutexTypeAdaptiveNp, out resolvedAddress, out state);
        }

        if (pointedHandle != 0)
        {
            if (_mutexStates.TryGetValue(pointedHandle, out state))
            {
                _mutexStates.TryAdd(mutexAddress, state);
                resolvedAddress = pointedHandle;
                return true;
            }

            resolvedAddress = pointedHandle;
            return false;
        }

        if (!createIfZero)
        {
            resolvedAddress = mutexAddress;
            return false;
        }

        return CreateImplicitMutexState(ctx, mutexAddress, MutexTypeErrorCheck, out resolvedAddress, out state);
    }

    private static ulong ResolveMutexAttrHandle(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return 0;
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, attrAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_mutexAttrStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        lock (_stateGate)
        {
            if (_mutexAttrStates.ContainsKey(attrAddress))
            {
                return attrAddress;
            }
        }

        return attrAddress;
    }

    private static PthreadMutexAttrState ResolveMutexAttrState(CpuContext ctx, ulong attrAddress)
    {
        if (attrAddress == 0)
        {
            return new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        }

        var resolvedAddress = ResolveMutexAttrHandle(ctx, attrAddress);
        lock (_stateGate)
        {
            return _mutexAttrStates.TryGetValue(resolvedAddress, out var state)
                ? state
                : new PthreadMutexAttrState(MutexTypeErrorCheck, 0);
        }
    }

    private static ulong ResolveCondHandle(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return 0;
        }

        lock (_stateGate)
        {
            if (_condStates.ContainsKey(condAddress))
            {
                return condAddress;
            }
        }

        if (KernelMemoryCompatExports.TryReadUInt64Compat(ctx, condAddress, out var pointedHandle) && pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_condStates.ContainsKey(pointedHandle))
                {
                    return pointedHandle;
                }
            }
        }

        return condAddress;
    }

    private static bool TryResolveCondState(CpuContext? ctx, ulong condAddress, bool createIfZero, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadCondState? state)
    {
        resolvedAddress = 0;
        state = null;
        if (condAddress == 0)
        {
            return false;
        }

        lock (_stateGate)
        {
            if (_condStates.TryGetValue(condAddress, out state))
            {
                resolvedAddress = condAddress;
                return true;
            }
        }

        if (ctx is null || !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, condAddress, out var pointedHandle))
        {
            return false;
        }

        if (pointedHandle != 0)
        {
            lock (_stateGate)
            {
                if (_condStates.TryGetValue(pointedHandle, out state))
                {
                    _condStates[condAddress] = state;
                    resolvedAddress = pointedHandle;
                    return true;
                }
            }

            resolvedAddress = pointedHandle;
            return false;
        }

        if (!createIfZero)
        {
            resolvedAddress = condAddress;
            return false;
        }

        var createdState = new PthreadCondState();
        if (!TryAllocateOpaqueObject(ctx, CondObjectSize, out var handle))
        {
            return false;
        }

        lock (_stateGate)
        {
            _condStates[condAddress] = createdState;
            _condStates[handle] = createdState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, handle))
        {
            lock (_stateGate)
            {
                _condStates.Remove(condAddress);
                _condStates.Remove(handle);
            }

            return false;
        }

        resolvedAddress = handle;
        state = createdState;
        return true;
    }

    private static bool TryAllocateOpaqueObject(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)size, alignment: 0x10, out address))
        {
            return false;
        }

        Span<byte> initialData = stackalloc byte[size];
        initialData.Clear();
        return ctx.Memory.TryWrite(address, initialData);
    }

    private static bool InitializeMutexObject(CpuContext ctx, ulong address, PthreadMutexState state) =>
        TryWriteUInt32(ctx, address + 0x20, unchecked((uint)state.Type)) &&
        TryWriteUInt32(ctx, address + 0x3C, unchecked((uint)state.Protocol));

    private static bool WriteMutexAttrObject(CpuContext ctx, ulong address, PthreadMutexAttrState state) =>
        TryWriteUInt32(ctx, address, unchecked((uint)state.Type)) &&
        TryWriteUInt32(ctx, address + 4, unchecked((uint)state.Protocol));

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BitConverter.TryWriteBytes(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int PthreadCondInitCore(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryAllocateOpaqueObject(ctx, CondObjectSize, out var handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        lock (_stateGate)
        {
            var state = new PthreadCondState();
            _condStates[condAddress] = state;
            _condStates[handle] = state;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, handle))
        {
            lock (_stateGate)
            {
                _condStates.Remove(condAddress);
                _condStates.Remove(handle);
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadCondDestroyCore(CpuContext ctx, ulong condAddress)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var resolvedAddress = ResolveCondHandle(ctx, condAddress);
        lock (_stateGate)
        {
            if (!_condStates.TryGetValue(resolvedAddress, out var state))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            lock (state.SyncRoot)
            {
                if (state.WaiterQueue.Count != 0)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
                }
            }

            _condStates.Remove(resolvedAddress);
            if (resolvedAddress != condAddress)
            {
                _condStates.Remove(condAddress);
            }
        }

        _ = KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, condAddress, 0);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PthreadCondWaitCore(
        CpuContext ctx,
        ulong condAddress,
        ulong mutexAddress,
        bool timed,
        uint timeoutUsec = 0,
        bool posixErrors = false)
    {
        if (condAddress == 0 || mutexAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveCondState(ctx, condAddress, createIfZero: true, out _, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!TryResolveMutexState(ctx, mutexAddress, createIfZero: true, out _, out var mutexState))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        lock (mutexState)
        {
            if (mutexState.OwnerThreadId != currentThreadId || mutexState.RecursionCount != 1)
            {
                return mutexState.OwnerThreadId == currentThreadId
                    ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }
        }

        var cooperative = GuestThreadExecution.IsGuestThread &&
            GuestThreadExecution.TryGetCurrentImportCallFrame(out _);
        var waiter = new PthreadCondWaiter
        {
            ThreadId = currentThreadId,
            MutexState = mutexState,
            Cooperative = cooperative,
            PosixErrors = posixErrors,
            WakeKey = cooperative
                ? $"pthread_cond_waiter:{Interlocked.Increment(ref _nextSynchronizationWaiterId)}"
                : string.Empty,
        };

        lock (state.SyncRoot)
        {
            waiter.Node = state.WaiterQueue.AddLast(waiter);
            state.Waiters++;
            TracePthreadCond("wait-enter", condAddress, mutexAddress, state, timed, (int)OrbisGen2Result.ORBIS_GEN2_OK);

            var unlockResult = PthreadMutexUnlockCore(ctx, mutexAddress, requireOwner: true);
            if (unlockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                RemoveCondWaiterLocked(state, waiter);
                TracePthreadCond("wait-unlock-fail", condAddress, mutexAddress, state, timed, unlockResult);
                return unlockResult;
            }

            if (cooperative && timed)
            {
                waiter.TimeoutTimer = new Timer(
                    static callbackState =>
                    {
                        var (condState, condWaiter) = ((PthreadCondState, PthreadCondWaiter))callbackState!;
                        CompleteCondWaiter(condState, condWaiter, timedOut: true);
                    },
                    (state, waiter),
                    GetCondWaitTimeout(timeoutUsec),
                    Timeout.InfiniteTimeSpan);
            }
        }

        if (cooperative &&
            GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                timed ? "pthread_cond_timedwait" : "pthread_cond_wait",
                waiter.WakeKey,
                () => CompleteBlockedCondWait(ctx, condAddress, mutexAddress, state, waiter),
                () => TryGrantCondWaiterMutex(waiter)))
        {
            TracePthreadCond("wait-block", condAddress, mutexAddress, state, timed, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Non-guest callers have no resumable CPU continuation. Park only
        // those host-side compatibility callers, preserving the same FIFO
        // mutex reacquisition rules as cooperative guest waiters.
        lock (state.SyncRoot)
        {
            var deadline = timed
                ? GuestThreadExecution.ComputeDeadlineTimestamp(GetCondWaitTimeout(timeoutUsec))
                : long.MaxValue;
            while (waiter.CompletionState == 0)
            {
                if (!timed)
                {
                    Monitor.Wait(state.SyncRoot);
                    continue;
                }

                var remaining = GetRemainingTimeout(deadline);
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(state.SyncRoot, remaining))
                {
                    CompleteCondWaiterLocked(state, waiter, timedOut: true);
                    break;
                }
            }
        }

        if (waiter.MutexWaiter is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        _ = WaitForHostMutexLock(mutexState, waiter.MutexWaiter);
        var waitResult = waiter.CompletionState == 2
            ? CondTimedOutResult(waiter)
            : (int)OrbisGen2Result.ORBIS_GEN2_OK;
        TracePthreadCond(waiter.CompletionState == 2 ? "wait-exit-timeout" : "wait-exit", condAddress, mutexAddress, state, timed, waitResult);
        return waitResult;
    }

    private static int PthreadCondSignalCore(CpuContext ctx, ulong condAddress, bool broadcast)
    {
        if (condAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryResolveCondState(ctx, condAddress, createIfZero: true, out _, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        List<PthreadCondWaiter>? completedWaiters = null;
        lock (state.SyncRoot)
        {
            state.SignalEpoch++;
            for (var node = state.WaiterQueue.First; node is not null;)
            {
                var next = node.Next;
                var waiter = node.Value;
                if (waiter.CompletionState == 0 && CompleteCondWaiterLocked(state, waiter, timedOut: false))
                {
                    (completedWaiters ??= new List<PthreadCondWaiter>()).Add(waiter);
                    if (!broadcast)
                    {
                        break;
                    }
                }

                node = next;
            }

            TracePthreadCond(broadcast ? "broadcast" : "signal", condAddress, mutexAddress: 0, state, timed: false, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        }

        if (completedWaiters is not null)
        {
            foreach (var waiter in completedWaiters)
            {
                WakeCooperativeWaiter(waiter);
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static PthreadMutexWaiter EnqueueMutexWaiterLocked(
        PthreadMutexState state,
        ulong threadId,
        bool cooperative,
        string? wakeKey = null)
    {
        var waiter = new PthreadMutexWaiter
        {
            ThreadId = threadId,
            Cooperative = cooperative,
            WakeKey = cooperative
                ? wakeKey ?? $"pthread_mutex_waiter:{Interlocked.Increment(ref _nextSynchronizationWaiterId)}"
                : string.Empty,
        };
        waiter.Node = state.Waiters.AddLast(waiter);
        return waiter;
    }

    [Conditional("DEBUG")]
    private static void RunSynchronizationSelfChecks()
    {
        var mutex = new PthreadMutexState();
        PthreadMutexWaiter first;
        PthreadMutexWaiter second;
        lock (mutex)
        {
            first = EnqueueMutexWaiterLocked(mutex, 0x101, cooperative: false);
            second = EnqueueMutexWaiterLocked(mutex, 0x202, cooperative: false);
            Debug.Assert(!TryGrantMutexWaiterLocked(mutex, second), "A mutex waiter bypassed FIFO order.");
            Debug.Assert(TryGrantMutexWaiterLocked(mutex, first), "The FIFO mutex head was not granted.");
            Debug.Assert(mutex.OwnerThreadId == first.ThreadId && mutex.RecursionCount == 1, "Mutex ownership was not transferred atomically.");
            mutex.OwnerThreadId = 0;
            mutex.RecursionCount = 0;
            Debug.Assert(TryGrantMutexWaiterLocked(mutex, second), "The second mutex waiter was not granted after release.");
        }

        var cond = new PthreadCondState();
        var condMutex = new PthreadMutexState();
        var condWaiter = new PthreadCondWaiter
        {
            ThreadId = 0x303,
            MutexState = condMutex,
            WakeKey = string.Empty,
            Cooperative = false,
        };
        lock (cond.SyncRoot)
        {
            condWaiter.Node = cond.WaiterQueue.AddLast(condWaiter);
            cond.Waiters++;
            Debug.Assert(CompleteCondWaiterLocked(cond, condWaiter, timedOut: false), "A condition waiter was not completed.");
            Debug.Assert(cond.WaiterQueue.Count == 0 && cond.Waiters == 0 && condWaiter.MutexWaiter is not null, "Condition completion did not atomically queue mutex reacquisition.");
        }
    }

    private static bool TryGrantMutexWaiterLocked(PthreadMutexState state, PthreadMutexWaiter waiter)
    {
        if (Volatile.Read(ref waiter.Granted) != 0)
        {
            return true;
        }

        if (state.OwnerThreadId != 0 ||
            waiter.Node is null ||
            !ReferenceEquals(state.Waiters.First, waiter.Node))
        {
            return false;
        }

        state.Waiters.Remove(waiter.Node);
        waiter.Node = null;
        state.OwnerThreadId = waiter.ThreadId;
        state.RecursionCount = 1;
        Volatile.Write(ref waiter.Granted, 1);
        Monitor.PulseAll(state);
        return true;
    }

    private static int WaitForHostMutexLock(PthreadMutexState state, PthreadMutexWaiter waiter)
    {
        lock (state)
        {
            while (!TryGrantMutexWaiterLocked(state, waiter))
            {
                Monitor.Wait(state);
            }
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryGrantBlockedMutexLock(
        CpuContext ctx,
        ulong mutexAddress,
        ulong resolvedAddress,
        PthreadMutexState state,
        PthreadMutexWaiter waiter)
    {
        var granted = false;
        lock (state)
        {
            granted = TryGrantMutexWaiterLocked(state, waiter);
        }

        TracePthreadMutex(
            ctx,
            granted ? "lock-reserve" : "lock-reserve-busy",
            mutexAddress,
            resolvedAddress,
            state,
            waiter.ThreadId,
            granted ? (int)OrbisGen2Result.ORBIS_GEN2_OK : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
        return granted;
    }

    private static int CompleteBlockedMutexLock(
        CpuContext ctx,
        ulong mutexAddress,
        ulong resolvedAddress,
        PthreadMutexState state,
        PthreadMutexWaiter waiter)
    {
        var currentThreadId = KernelPthreadState.GetCurrentThreadHandle();
        if (Volatile.Read(ref waiter.Granted) == 1)
        {
            TracePthreadMutex(ctx, "lock-resume", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        TracePthreadMutex(ctx, "lock-resume-ungranted", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
    }

    private static bool CompleteCondWaiterLocked(
        PthreadCondState state,
        PthreadCondWaiter waiter,
        bool timedOut)
    {
        if (waiter.CompletionState != 0)
        {
            return false;
        }

        waiter.CompletionState = timedOut ? 2 : 1;
        RemoveCondWaiterLocked(state, waiter);
        waiter.TimeoutTimer?.Dispose();
        waiter.TimeoutTimer = null;

        lock (waiter.MutexState)
        {
            waiter.MutexWaiter = EnqueueMutexWaiterLocked(
                waiter.MutexState,
                waiter.ThreadId,
                waiter.Cooperative,
                waiter.WakeKey);
        }

        Monitor.PulseAll(state.SyncRoot);
        return true;
    }

    private static void CompleteCondWaiter(
        PthreadCondState state,
        PthreadCondWaiter waiter,
        bool timedOut)
    {
        var completed = false;
        lock (state.SyncRoot)
        {
            completed = CompleteCondWaiterLocked(state, waiter, timedOut);
        }

        if (completed)
        {
            WakeCooperativeWaiter(waiter);
        }
    }

    private static void RemoveCondWaiterLocked(PthreadCondState state, PthreadCondWaiter waiter)
    {
        if (waiter.Node is not null)
        {
            state.WaiterQueue.Remove(waiter.Node);
            waiter.Node = null;
            state.Waiters = Math.Max(0, state.Waiters - 1);
        }
    }

    private static bool TryGrantCondWaiterMutex(PthreadCondWaiter waiter)
    {
        var mutexWaiter = waiter.MutexWaiter;
        if (waiter.CompletionState == 0 || mutexWaiter is null)
        {
            return false;
        }

        lock (waiter.MutexState)
        {
            return TryGrantMutexWaiterLocked(waiter.MutexState, mutexWaiter);
        }
    }

    private static int CompleteBlockedCondWait(
        CpuContext ctx,
        ulong condAddress,
        ulong mutexAddress,
        PthreadCondState state,
        PthreadCondWaiter waiter)
    {
        waiter.TimeoutTimer?.Dispose();
        waiter.TimeoutTimer = null;
        var result = waiter.MutexWaiter is not null &&
            Volatile.Read(ref waiter.MutexWaiter.Granted) == 1
                ? (waiter.CompletionState == 2
                    ? CondTimedOutResult(waiter)
                    : (int)OrbisGen2Result.ORBIS_GEN2_OK)
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        TracePthreadCond(
            waiter.CompletionState == 2 ? "wait-resume-timeout" : "wait-resume",
            condAddress,
            mutexAddress,
            state,
            waiter.CompletionState == 2,
            result);
        _ = ctx;
        return result;
    }

    private static int CondTimedOutResult(PthreadCondWaiter waiter) =>
        waiter.PosixErrors
            ? 60 // ETIMEDOUT on Orbis/FreeBSD; pthread APIs return errno directly.
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;

    private static void WakeCooperativeWaiter(PthreadCondWaiter waiter)
    {
        if (waiter.Cooperative)
        {
            _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(waiter.WakeKey, 1);
        }
    }

    private static TimeSpan GetCondWaitTimeout(uint timeoutUsec)
    {
        if (timeoutUsec == 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks((long)timeoutUsec * 10L);
    }

    private static TimeSpan GetRemainingTimeout(long deadlineTimestamp)
    {
        var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
    }

    private static int NormalizeMutexType(int type)
    {
        return type switch
        {
            0 => MutexTypeErrorCheck,
            1 => MutexTypeErrorCheck,
            2 => MutexTypeRecursive,
            3 => MutexTypeNormal,
            4 => MutexTypeAdaptiveNp,
            _ => MutexTypeErrorCheck,
        };
    }

    private static object GetPthreadOnceGate(ulong onceAddress)
    {
        lock (_stateGate)
        {
            if (!_onceGates.TryGetValue(onceAddress, out var gate))
            {
                gate = new object();
                _onceGates[onceAddress] = gate;
            }

            return gate;
        }
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }

    private static bool TryReadInt32(CpuContext ctx, ulong address, out int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryWriteInt32(CpuContext ctx, ulong address, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool CreateImplicitMutexState(CpuContext ctx, ulong mutexAddress, int type, out ulong resolvedAddress, [NotNullWhen(true)] out PthreadMutexState? state)
    {
        var createdState = new PthreadMutexState
        {
            Type = type,
        };

        if (!TryAllocateOpaqueObject(ctx, MutexObjectSize, out var handle))
        {
            resolvedAddress = 0;
            state = null;
            return false;
        }
        if (!InitializeMutexObject(ctx, handle, createdState))
        {
            resolvedAddress = 0;
            state = null;
            return false;
        }

        lock (_stateGate)
        {
            if (_mutexStates.TryGetValue(mutexAddress, out state))
            {
                resolvedAddress = mutexAddress;
                return true;
            }

            if (_mutexStates.TryGetValue(handle, out state))
            {
                resolvedAddress = handle;
                return true;
            }

            _mutexStates[mutexAddress] = createdState;
            _mutexStates[handle] = createdState;
        }

        if (!KernelMemoryCompatExports.TryWriteUInt64Compat(ctx, mutexAddress, handle))
        {
            _mutexStates.TryRemove(mutexAddress, out _);
            _mutexStates.TryRemove(handle, out _);

            resolvedAddress = 0;
            state = null;
            return false;
        }

        resolvedAddress = handle;
        state = createdState;
        return true;
    }

    private static void TracePthreadSelf(CpuContext ctx, ulong currentThreadHandle)
    {
        if (!ShouldTracePthread())
        {
            return;
        }

        var currentThreadId = KernelPthreadState.GetCurrentThreadUniqueId();
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_self: stale_rdi=0x{ctx[CpuRegister.Rdi]:X16} thread=0x{currentThreadHandle:X16} tid=0x{currentThreadId:X16}");
    }

    private static void TracePthreadOnce(ulong onceAddress, ulong initRoutine, string operation, string? error)
    {
        if (!ShouldTracePthread())
        {
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(error) ? string.Empty : $" error={error}";
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_once_{operation}: once=0x{onceAddress:X16} init=0x{initRoutine:X16}{suffix}");
    }

    private static void TracePthreadMutex(CpuContext ctx, string operation, ulong mutexAddress, ulong resolvedAddress, PthreadMutexState? state, ulong currentThreadId, int result)
    {
        if (!ShouldTracePthreadMutex(mutexAddress, resolvedAddress))
        {
            return;
        }

        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress, out var guestWord0);
        _ = KernelMemoryCompatExports.TryReadUInt64Compat(ctx, mutexAddress + 8, out var guestWord1);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_{operation}: mutex=0x{mutexAddress:X16} resolved=0x{resolvedAddress:X16} " +
            $"guest[0]=0x{guestWord0:X16} guest[8]=0x{guestWord1:X16} " +
            $"current=0x{currentThreadId:X16} owner=0x{(state?.OwnerThreadId ?? 0):X16} " +
            $"recursion={(state?.RecursionCount ?? 0)} type={(state?.Type ?? 0)} result=0x{unchecked((uint)result):X8}");
    }

    private static void TracePthreadCond(string operation, ulong condAddress, ulong mutexAddress, PthreadCondState? state, bool timed, int result)
    {
        if (!_tracePthreadConds)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] pthread_cond_{operation}: cond=0x{condAddress:X16} mutex=0x{mutexAddress:X16} " +
            $"waiters={(state?.Waiters ?? 0)} epoch=0x{(state?.SignalEpoch ?? 0):X} timed={timed} result=0x{unchecked((uint)result):X8}");
    }

    private static bool ShouldTracePthread()
    {
        return _tracePthreads;
    }

    private static bool ShouldTracePthreadMutex(ulong mutexAddress, ulong resolvedAddress)
    {
        if (_tracePthreadMutexFilter is null || _tracePthreadMutexFilter.Count == 0)
        {
            return _tracePthreads;
        }

        return _tracePthreadMutexFilter.Contains(mutexAddress) ||
            _tracePthreadMutexFilter.Contains(resolvedAddress);
    }

    private static HashSet<ulong>? ParseTraceAddressFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return null;
        }

        var addresses = new HashSet<ulong>();
        foreach (var token in filter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? token[2..]
                : token;
            normalized = normalized.TrimStart('0');

            if (ulong.TryParse(
                    normalized.Length == 0 ? "0" : normalized,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var address))
            {
                addresses.Add(address);
            }
        }

        return addresses.Count == 0 ? null : addresses;
    }
}
