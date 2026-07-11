// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
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
