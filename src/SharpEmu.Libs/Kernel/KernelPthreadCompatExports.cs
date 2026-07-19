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

    // Blocking model: waiters block their own host thread in place via
    // Monitor.Wait on the state object (mutexes) or SyncRoot (condvars).
    // Block-and-wake is therefore atomic — no waiter queues, wake keys, or
    // continuation hand-offs, and no lost-wakeup window between a thread
    // deciding to block and registering as blocked.
    private sealed class PthreadMutexState
    {
        public ulong OwnerThreadId { get; set; }
        public int RecursionCount { get; set; }
        public int Type { get; set; } = MutexTypeErrorCheck;
        public int Protocol { get; set; }
        // Threads currently blocked in PthreadMutexLockCore; destroy reports BUSY while nonzero.
        public int WaiterCount { get; set; }
    }

    private sealed class PthreadCondState
    {
        public object SyncRoot { get; } = new();
        public ulong SignalEpoch { get; set; }
        public int Waiters { get; set; }
        // Signals produced but not yet consumed by a waiter. A signal only
        // increments this when an unserved waiter exists (POSIX: signaling an
        // empty condvar is a no-op), so stale signals cannot accumulate.
        public int SignalsPending { get; set; }
    }

    private readonly record struct PthreadMutexAttrState(int Type, int Protocol);

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
        Nid = "9vyP6Z7bqzc",
        ExportName = "pthread_rename_np",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadRenameNp(CpuContext ctx) => PthreadRename(ctx);

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
            if (state.OwnerThreadId != 0 || state.RecursionCount != 0 || state.WaiterCount != 0)
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

                    // Several Gen5 runtimes layer their own owner/count bookkeeping
                    // over a NORMAL or ADAPTIVE kernel mutex. Returning EDEADLK here
                    // leaves that guest bookkeeping out of sync with the HLE owner and
                    // turns the wrapper into a permanent lock/unlock retry loop. Keep
                    // the compatibility recursion used by the original implementation;
                    // ERRORCHECK mutexes still take the strict EDEADLK path below.
                    state.RecursionCount++;
                    TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
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

            if (state.OwnerThreadId == 0)
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

            // Contended: block this host thread in place until the owner
            // releases. Monitor.Wait atomically releases the state lock and
            // parks, so an unlock's PulseAll cannot be missed. Waits are
            // sliced only so teardown can unwind parked threads.
            TracePthreadMutex(ctx, "lock-block", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            GuestThreadBlocking.NoteBlocked(currentThreadId, "pthread_mutex_lock");
            state.WaiterCount++;
            try
            {
                while (state.OwnerThreadId != 0)
                {
                    if (GuestThreadBlocking.ShutdownRequested)
                    {
                        TracePthreadMutex(ctx, "lock-shutdown", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
                    }

                    GuestThreadBlocking.Checkpoint(currentThreadId, state);
                    _ = Monitor.Wait(state, GuestThreadBlocking.WaitSliceMilliseconds);
                }

                state.OwnerThreadId = currentThreadId;
                state.RecursionCount = 1;
            }
            finally
            {
                state.WaiterCount--;
                GuestThreadBlocking.NoteUnblocked(currentThreadId);
            }

            TracePthreadMutex(ctx, "lock", mutexAddress, resolvedAddress, state, currentThreadId, (int)OrbisGen2Result.ORBIS_GEN2_OK);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
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
                if (state.WaiterCount != 0)
                {
                    Monitor.PulseAll(state);
                }
            }
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
                if (state.Waiters != 0)
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
            if (mutexState.OwnerThreadId == 0 && mutexState.RecursionCount == 0)
            {
                // The guest holds the mutex through a path our host-side tracking
                // never observed — most commonly libkernel's uncontended userspace
                // fast-path, which locks the mutex word directly without an HLE
                // call. Real pthread_cond_wait requires the caller to own the
                // mutex and does not verify it for normal mutexes, so returning
                // EPERM here is wrong: it spins the guest and, worse, leaves the
                // mutex held (the unlock below is skipped), wedging every thread
                // that later blocks on pthread_mutex_lock. Adopt ownership so the
                // unlock/wait/re-lock cycle is balanced and releases the mutex.
                mutexState.OwnerThreadId = currentThreadId;
                mutexState.RecursionCount = 1;
            }
            else if (mutexState.OwnerThreadId != currentThreadId || mutexState.RecursionCount != 1)
            {
                return mutexState.OwnerThreadId == currentThreadId
                    ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT
                    : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            }
        }

        var signaled = false;
        lock (state.SyncRoot)
        {
            state.Waiters++;
            TracePthreadCond("wait-enter", condAddress, mutexAddress, state, timed, (int)OrbisGen2Result.ORBIS_GEN2_OK);

            // POSIX atomicity: we are registered as a waiter (Waiters++ under
            // SyncRoot) before the mutex is released, so a signal issued the
            // instant the mutex unlocks already counts us and lands in
            // SignalsPending — checked before the first Monitor.Wait. No
            // window exists where a wake can be lost.
            var unlockResult = PthreadMutexUnlockCore(ctx, mutexAddress, requireOwner: true);
            if (unlockResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
            {
                state.Waiters--;
                TracePthreadCond("wait-unlock-fail", condAddress, mutexAddress, state, timed, unlockResult);
                return unlockResult;
            }

            var deadline = timed
                ? GuestThreadExecution.ComputeDeadlineTimestamp(GetCondWaitTimeout(timeoutUsec))
                : long.MaxValue;
            GuestThreadBlocking.NoteBlocked(currentThreadId, timed ? "pthread_cond_timedwait" : "pthread_cond_wait");
            try
            {
                while (state.SignalsPending == 0 && !GuestThreadBlocking.ShutdownRequested)
                {
                    var remaining = timed
                        ? GetRemainingTimeout(deadline)
                        : TimeSpan.FromMilliseconds(GuestThreadBlocking.WaitSliceMilliseconds);
                    if (timed && remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    if (remaining > TimeSpan.FromMilliseconds(GuestThreadBlocking.WaitSliceMilliseconds))
                    {
                        remaining = TimeSpan.FromMilliseconds(GuestThreadBlocking.WaitSliceMilliseconds);
                    }

                    GuestThreadBlocking.Checkpoint(currentThreadId, state.SyncRoot);
                    _ = Monitor.Wait(state.SyncRoot, remaining);
                }
            }
            finally
            {
                GuestThreadBlocking.NoteUnblocked(currentThreadId);
            }

            if (state.SignalsPending > 0)
            {
                state.SignalsPending--;
                signaled = true;
            }

            state.Waiters--;
            if (state.SignalsPending > state.Waiters)
            {
                // A timed-out waiter left a signal unconsumed with nobody
                // remaining to take it; drop it so a future wait does not
                // observe a phantom wake (signals on an empty condvar are
                // no-ops on real hardware).
                state.SignalsPending = state.Waiters;
            }
        }

        // POSIX guarantees the mutex is re-acquired on every return path,
        // signaled or timed out. Blocks in place like any other locker.
        _ = PthreadMutexLockCore(ctx, mutexAddress, tryOnly: false);

        var waitResult = signaled || !timed
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : CondTimedOutResult(posixErrors);
        TracePthreadCond(signaled ? "wait-exit" : "wait-exit-timeout", condAddress, mutexAddress, state, timed, waitResult);
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

        lock (state.SyncRoot)
        {
            state.SignalEpoch++;
            if (broadcast)
            {
                state.SignalsPending = state.Waiters;
            }
            else if (state.SignalsPending < state.Waiters)
            {
                // Only count a signal an unserved waiter can consume; signaling
                // an empty condvar is a no-op per POSIX.
                state.SignalsPending++;
            }

            if (state.Waiters != 0)
            {
                Monitor.PulseAll(state.SyncRoot);
            }

            TracePthreadCond(broadcast ? "broadcast" : "signal", condAddress, mutexAddress: 0, state, timed: false, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int CondTimedOutResult(bool posixErrors) =>
        posixErrors
            ? 60 // ETIMEDOUT on Orbis/FreeBSD; pthread APIs return errno directly.
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;

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
