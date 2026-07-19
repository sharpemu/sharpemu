// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.CxxAbi;

public static class CxaGuardExports
{
    private const ulong GuardCompleteValue = 0x0000_0000_0000_0001;
    private const ulong GuardPendingValue = 0x0000_0000_0000_0100;
    private const ulong GuardStateMask = 0x0000_0000_0000_FFFF;

    private sealed class GuardState
    {
        public int OwnerThreadId { get; set; }
        public int RecursionDepth { get; set; }
    }

    private static readonly ConcurrentDictionary<ulong, GuardState> _inProgress = new();

    [SysAbiExport(
        Nid = "3GPpjQdAMTw",
        ExportName = "__cxa_guard_acquire",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaGuardAcquire(CpuContext ctx)
    {
        var guardPtr = ctx[CpuRegister.Rdi];
        if (guardPtr == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var currentThreadId = Environment.CurrentManagedThreadId;
        var spinner = new SpinWait();
        while (true)
        {
            if (!TryReadGuardState(ctx, guardPtr, out _, out var initialized, out var inProgress))
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            LogGuardState(ctx, "guard_acquire", guardPtr, initialized, inProgress);

            if (initialized)
            {
                ctx[CpuRegister.Rax] = 0;
                LogGuardResult("guard_acquire", guardPtr, result: 0, initialized, inProgress: false, ownerThreadId: 0);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            var newState = new GuardState
            {
                OwnerThreadId = currentThreadId,
                RecursionDepth = 1,
            };
            if (_inProgress.TryAdd(guardPtr, newState))
            {
                if (!TryWriteGuardState(ctx, guardPtr, GuardPendingValue))
                {
                    _inProgress.TryRemove(guardPtr, out _);
                    ctx[CpuRegister.Rax] = 0;
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                ctx[CpuRegister.Rax] = 1;
                LogGuardResult("guard_acquire", guardPtr, result: 1, initialized, inProgress: true, ownerThreadId: currentThreadId);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (_inProgress.TryGetValue(guardPtr, out var state))
            {
                if (state.OwnerThreadId == currentThreadId)
                {
                    ctx[CpuRegister.Rax] = 0;
                    LogGuardResult("guard_acquire", guardPtr, result: 0, initialized, inProgress: true, ownerThreadId: state.OwnerThreadId);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }

            spinner.SpinOnce();
            if (spinner.Count % 32 == 0)
            {
                Thread.Yield();
            }
        }
    }

    [SysAbiExport(
        Nid = "9rAeANT2tyE",
        ExportName = "__cxa_guard_release",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaGuardRelease(CpuContext ctx)
    {
        var guardPtr = ctx[CpuRegister.Rdi];
        if (guardPtr == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (_inProgress.TryGetValue(guardPtr, out var state) &&
            state.OwnerThreadId != Environment.CurrentManagedThreadId)
        {
            ctx[CpuRegister.Rax] = 0;
            LogGuardResult("guard_release", guardPtr, result: 0, initialized: false, inProgress: true, ownerThreadId: state.OwnerThreadId);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (state is not null)
        {
            lock (state)
            {
                if (state.RecursionDepth > 1)
                {
                    state.RecursionDepth--;
                    ctx[CpuRegister.Rax] = 0;
                    LogGuardResult("guard_release", guardPtr, result: 0, initialized: false, inProgress: true, ownerThreadId: state.OwnerThreadId);
                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }
        }

        if (!TryWriteGuardState(ctx, guardPtr, GuardCompleteValue))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _inProgress.TryRemove(guardPtr, out _);
        LogGuardState(ctx, "guard_release", guardPtr, initialized: true, inProgress: false);

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "2emaaluWzUw",
        ExportName = "__cxa_guard_abort",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaGuardAbort(CpuContext ctx)
    {
        var guardPtr = ctx[CpuRegister.Rdi];
        if (guardPtr == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (_inProgress.TryGetValue(guardPtr, out var state) &&
            state.OwnerThreadId != Environment.CurrentManagedThreadId)
        {
            ctx[CpuRegister.Rax] = 0;
            LogGuardResult("guard_abort", guardPtr, result: 0, initialized: false, inProgress: true, ownerThreadId: state.OwnerThreadId);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        _ = TryWriteGuardState(ctx, guardPtr, 0);
        _inProgress.TryRemove(guardPtr, out _);
        LogGuardState(ctx, "guard_abort", guardPtr, initialized: false, inProgress: false);

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryReadGuardState(CpuContext ctx, ulong guardPtr, out ulong word, out bool initialized, out bool inProgress)
    {
        word = 0;
        initialized = false;
        inProgress = false;
        if (!ctx.TryReadUInt64(guardPtr, out word))
        {
            return false;
        }

        initialized = (word & GuardCompleteValue) != 0;
        inProgress = (word & 0x0000_0000_0000_FF00) != 0;
        return true;
    }

    private static bool TryWriteGuardState(CpuContext ctx, ulong guardPtr, ulong stateValue)
    {
        if (!ctx.TryReadUInt64(guardPtr, out var word))
        {
            return false;
        }

        var newWord = (word & ~GuardStateMask) | (stateValue & GuardStateMask);
        return ctx.TryWriteUInt64(guardPtr, newWord);
    }

    private static void LogGuardState(CpuContext ctx, string op, ulong guardPtr, bool initialized, bool inProgress)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUARDS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var readable = ctx.TryReadUInt64(guardPtr, out var word);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] {op}: guard=0x{guardPtr:X16} init={initialized} in_progress={inProgress} word={(readable ? $"0x{word:X16}" : "<unreadable>")}");
    }

    private static void LogGuardResult(string op, ulong guardPtr, int result, bool initialized, bool inProgress, int ownerThreadId)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUARDS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] {op}: guard=0x{guardPtr:X16} result={result} init={initialized} in_progress={inProgress} owner_thread={ownerThreadId}");
    }
}

/// <summary>
/// libstdc++'s std::call_once engine (NID DiGVep5yB5w =
/// _ZSt13_Execute_onceRSt9once_flagPFiPvS1_PS1_ES1_, i.e.
/// std::_Execute_once(std::once_flag&amp;, int(*)(void*,void*,void**), void**)) and the
/// Itanium C++ ABI's __cxa_decrement_exception_refcount (NID MQFPAqQPt1s). Both are
/// public/standardized C++ runtime internals rather than proprietary SCE behavior;
/// see testing_instructions.md for how they were identified (SHA1 hash match against
/// scripts/ps5_names.txt) and how the Metal Slug Tactics boot crash traced back to
/// std::_Execute_once never being implemented, leaving a call_once-guarded singleton
/// permanently null.
/// </summary>
public static class StdOnceExports
{
    private const int ExecuteOnceNotStarted = 0;
    private const int ExecuteOnceInProgress = 1;
    private const int ExecuteOnceDone = 2;

    private sealed class ExecuteOnceState
    {
        public int Status;
        public ulong ReturnValue;
    }

    private static readonly object _stateGate = new();
    private static readonly Dictionary<ulong, ExecuteOnceState> _onceStates = new();

    /// <summary>
    /// Unlike __cxa_guard_acquire/release (which only manage lock state around
    /// compiler-inlined initializer code), call_once passes its callable as an
    /// argument, so _Execute_once must genuinely invoke it. Tracking is kept
    /// entirely host-side, keyed by the once_flag address itself: nothing else in
    /// the guest reads/writes a once_flag's internal byte layout (only
    /// _Execute_once ever touches it), so there is no need to reproduce SCE/
    /// libstdc++'s exact once_flag representation.
    /// </summary>
    [SysAbiExport(
        Nid = "DiGVep5yB5w",
        ExportName = "_ZSt13_Execute_onceRSt9once_flagPFiPvS1_PS1_ES1_",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int StdExecuteOnce(CpuContext ctx)
    {
        var onceFlagAddress = ctx[CpuRegister.Rdi];
        var callback = ctx[CpuRegister.Rsi];
        var state = ctx[CpuRegister.Rdx];
        if (onceFlagAddress == 0 || callback == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var flagState = GetExecuteOnceState(onceFlagAddress);
        bool shouldCall;
        lock (flagState)
        {
            while (flagState.Status == ExecuteOnceInProgress)
            {
                Monitor.Wait(flagState, TimeSpan.FromMilliseconds(1));
            }

            if (flagState.Status == ExecuteOnceDone)
            {
                ctx[CpuRegister.Rax] = flagState.ReturnValue;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            flagState.Status = ExecuteOnceInProgress;
            shouldCall = true;
        }

        var scheduler = GuestThreadExecution.Scheduler;
        string? error = null;
        var invoked = shouldCall &&
            scheduler is not null &&
            scheduler.TryCallGuestFunction(ctx, callback, 0, 0, state, 0, 0, "std::_Execute_once", out _, out error);

        lock (flagState)
        {
            if (invoked)
            {
                // Real _Execute_once returns 0 (success) when the callable ran
                // without an exception escaping it; the callable's own int return
                // value is a separate, ABI-internal detail we don't need to
                // forward. The void** state slot deliberately stays untouched
                // here (no exception to report), matching the "callback
                // succeeded" path.
                flagState.Status = ExecuteOnceDone;
                flagState.ReturnValue = 0;
            }
            else
            {
                flagState.Status = ExecuteOnceNotStarted;
                TraceExecuteOnce(onceFlagAddress, callback, "failed", error);
            }

            Monitor.PulseAll(flagState);
        }

        if (!invoked)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
        }

        TraceExecuteOnce(onceFlagAddress, callback, "call", null);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Per the public Itanium C++ ABI spec, a null thrown_exception is always a
    /// no-op. SharpEmu never intercepts __cxa_throw/__cxa_allocate_exception, so
    /// there is no host-tracked refcount to decrement for a genuinely non-null
    /// exception object here; treat that case as a safe no-op too rather than
    /// fabricate behavior for a case not yet observed in practice.
    /// </summary>
    [SysAbiExport(
        Nid = "MQFPAqQPt1s",
        ExportName = "__cxa_decrement_exception_refcount",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CxaDecrementExceptionRefcount(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static ExecuteOnceState GetExecuteOnceState(ulong onceFlagAddress)
    {
        lock (_stateGate)
        {
            if (!_onceStates.TryGetValue(onceFlagAddress, out var state))
            {
                state = new ExecuteOnceState();
                _onceStates[onceFlagAddress] = state;
            }

            return state;
        }
    }

    private static void TraceExecuteOnce(ulong onceFlagAddress, ulong callback, string outcome, string? error)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_GUARDS"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] std::_Execute_once: flag=0x{onceFlagAddress:X16} callback=0x{callback:X16} outcome={outcome}" +
            (error is null ? string.Empty : $" error={error}"));
    }
}

/// <summary>
/// Dinkumware's C11-threads-style internal mutex/condition-variable primitives that back
/// std::mutex/std::recursive_mutex/std::condition_variable (_Mtx_init/_Cnd_init and their
/// lock/unlock/wait/signal siblings). Same family and public/standardized character as
/// StdOnceExports's std::_Execute_once — see testing_instructions.md for how this game's
/// third boot crash traced circumstantially to _Mtx_init/_Cnd_init being unresolved, leaving
/// their out-param handles at zero/garbage for whatever the guest does with them next.
///
/// Unlike pthread_mutex_t/pthread_cond_t (embedded structs at a fixed guest address that
/// KernelPthreadCompatExports.cs tracks by that address), Dinkumware's _Mtx_t/_Cnd_t are
/// opaque handles written into a caller-provided out-slot by _Mtx_init/_Cnd_init, then
/// passed by value to every other function — so handles here are just host-assigned ids,
/// with no guest-visible representation to get wrong.
/// </summary>
public static class StdMutexExports
{
    // Dinkumware's <mutex>/xthreads.h type flags for _Mtx_init's second argument.
    private const int MtxRecursive = 0x100;

    private const int ThrdSuccess = 0;
    private const int ThrdBusy = 4;

    private sealed class MutexState
    {
        public bool Recursive;
        public int OwnerThreadId = -1;
        public int RecursionDepth;
    }

    private sealed class CondState
    {
    }

    private static long _nextHandle = 1;
    private static readonly ConcurrentDictionary<ulong, MutexState> _mutexes = new();
    private static readonly ConcurrentDictionary<ulong, CondState> _conds = new();

    [SysAbiExport(
        Nid = "YaHc3GS7y7g",
        ExportName = "_Mtx_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int MtxInit(CpuContext ctx)
    {
        var mtxSlot = ctx[CpuRegister.Rdi];
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        if (mtxSlot == 0)
        {
            return ThrdBusy;
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextHandle));
        _mutexes[handle] = new MutexState { Recursive = (type & MtxRecursive) != 0 };
        ctx.TryWriteUInt64(mtxSlot, handle);
        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "5Lf51jvohTQ",
        ExportName = "_Mtx_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int MtxDestroy(CpuContext ctx)
    {
        _mutexes.TryRemove(ctx[CpuRegister.Rdi], out _);
        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "iS4aWbUonl0",
        ExportName = "_Mtx_lock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int MtxLock(CpuContext ctx)
    {
        if (!_mutexes.TryGetValue(ctx[CpuRegister.Rdi], out var state))
        {
            return ThrdBusy;
        }

        var currentThreadId = Environment.CurrentManagedThreadId;
        lock (state)
        {
            if (state.Recursive && state.OwnerThreadId == currentThreadId)
            {
                state.RecursionDepth++;
                return ThrdSuccess;
            }

            while (state.OwnerThreadId != -1)
            {
                Monitor.Wait(state);
            }

            state.OwnerThreadId = currentThreadId;
            state.RecursionDepth = 1;
        }

        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "k6pGNMwJB08",
        ExportName = "_Mtx_trylock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int MtxTrylock(CpuContext ctx)
    {
        if (!_mutexes.TryGetValue(ctx[CpuRegister.Rdi], out var state))
        {
            return ThrdBusy;
        }

        var currentThreadId = Environment.CurrentManagedThreadId;
        lock (state)
        {
            if (state.OwnerThreadId == currentThreadId && state.Recursive)
            {
                state.RecursionDepth++;
                return ThrdSuccess;
            }

            if (state.OwnerThreadId != -1)
            {
                return ThrdBusy;
            }

            state.OwnerThreadId = currentThreadId;
            state.RecursionDepth = 1;
            return ThrdSuccess;
        }
    }

    [SysAbiExport(
        Nid = "gTuXQwP9rrs",
        ExportName = "_Mtx_unlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int MtxUnlock(CpuContext ctx)
    {
        if (!_mutexes.TryGetValue(ctx[CpuRegister.Rdi], out var state))
        {
            return ThrdBusy;
        }

        lock (state)
        {
            if (--state.RecursionDepth <= 0)
            {
                state.RecursionDepth = 0;
                state.OwnerThreadId = -1;
                Monitor.PulseAll(state);
            }
        }

        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "SreZybSRWpU",
        ExportName = "_Cnd_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CndInit(CpuContext ctx)
    {
        var condSlot = ctx[CpuRegister.Rdi];
        if (condSlot == 0)
        {
            return ThrdBusy;
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextHandle));
        _conds[handle] = new CondState();
        ctx.TryWriteUInt64(condSlot, handle);
        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "7yMFgcS8EPA",
        ExportName = "_Cnd_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CndDestroy(CpuContext ctx)
    {
        _conds.TryRemove(ctx[CpuRegister.Rdi], out _);
        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "vEaqE-7IZYc",
        ExportName = "_Cnd_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CndWait(CpuContext ctx)
    {
        if (!_conds.TryGetValue(ctx[CpuRegister.Rdi], out var cond) ||
            !_mutexes.TryGetValue(ctx[CpuRegister.Rsi], out var mtx))
        {
            return ThrdBusy;
        }

        // Standard condvar wait semantics: release the mutex fully (regardless of
        // recursion depth), wait, then reacquire it at the same recursion depth. The
        // cond's lock is taken before releasing the mutex (and Monitor.Wait releases
        // it again only once actually waiting) so a signal can't slip in between the
        // unlock and the wait and get lost.
        int savedDepth;
        lock (cond)
        {
            lock (mtx)
            {
                savedDepth = mtx.RecursionDepth;
                mtx.RecursionDepth = 0;
                mtx.OwnerThreadId = -1;
                Monitor.PulseAll(mtx);
            }

            Monitor.Wait(cond);
        }

        var currentThreadId = Environment.CurrentManagedThreadId;
        lock (mtx)
        {
            while (mtx.OwnerThreadId != -1)
            {
                Monitor.Wait(mtx);
            }

            mtx.OwnerThreadId = currentThreadId;
            mtx.RecursionDepth = savedDepth;
        }

        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "0uuqgRz9qfo",
        ExportName = "_Cnd_signal",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CndSignal(CpuContext ctx)
    {
        if (_conds.TryGetValue(ctx[CpuRegister.Rdi], out var cond))
        {
            lock (cond)
            {
                Monitor.Pulse(cond);
            }
        }

        return ThrdSuccess;
    }

    [SysAbiExport(
        Nid = "VsP3daJgmVA",
        ExportName = "_Cnd_broadcast",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int CndBroadcast(CpuContext ctx)
    {
        if (_conds.TryGetValue(ctx[CpuRegister.Rdi], out var cond))
        {
            lock (cond)
            {
                Monitor.PulseAll(cond);
            }
        }

        return ThrdSuccess;
    }
}
