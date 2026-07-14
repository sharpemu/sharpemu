// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelSemaphoreCompatExports
{
    private const int MaxSemaphoreNameLength = 128;
    private static readonly ConcurrentDictionary<uint, KernelSemaphoreState> _semaphores = new();
    private static int _nextSemaphoreHandle = 1;

    [ThreadStatic]
    private static int _semaPollBackoffCount;

    private sealed class KernelSemaphoreState
    {
        public required string Name { get; init; }
        public required int InitialCount { get; init; }
        public required int MaxCount { get; init; }
        public int Count { get; set; }
        public int WaitingThreads { get; set; }
        public int CancelEpoch { get; set; }
        public bool Deleted { get; set; }
        public object Gate { get; } = new();
    }

    private sealed class SemaphoreWaiter
    {
        public required int NeedCount { get; init; }
        public required int CancelEpochAtBlock { get; init; }
        public bool Timed { get; init; }

        // Written and read only under the owning semaphore's Gate.
        public int? Result { get; set; }
    }

    private static string GetSemaphoreWakeKey(uint handle) => $"kernel_sema:0x{handle:X8}";

    [SysAbiExport(
        Nid = "188x57JYp0g",
        ExportName = "sceKernelCreateSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateSema(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var attr = unchecked((uint)ctx[CpuRegister.Rdx]);
        var initialCount = unchecked((int)ctx[CpuRegister.Rcx]);
        var maxCount = unchecked((int)ctx[CpuRegister.R8]);
        var optionAddress = ctx[CpuRegister.R9];

        if (semaphoreAddress == 0 ||
            nameAddress == 0 ||
            attr > 2 ||
            initialCount < 0 ||
            maxCount <= 0 ||
            initialCount > maxCount ||
            optionAddress != 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadNullTerminatedUtf8(nameAddress, MaxSemaphoreNameLength, out var name))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        var state = new KernelSemaphoreState
        {
            Name = name,
            InitialCount = initialCount,
            MaxCount = maxCount,
            Count = initialCount,
        };
        _semaphores[handle] = state;

        if (!ctx.TryWriteUInt32(semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            // Handles are sequential and guest-predictable, so a hostile guest can
            // race a WaitSema onto the handle between publication above and this
            // rollback. Strand-proof that waiter exactly like DeleteSema does.
            lock (state.Gate)
            {
                state.Deleted = true;
            }

            _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceSemaphore($"create handle=0x{handle:X8} name='{name}' attr=0x{attr:X} init={initialCount} max={maxCount}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "Zxa0VhQVTsk",
        ExportName = "sceKernelWaitSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var needCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var pollTimedOut = false;
        lock (semaphore.Gate)
        {
            if (semaphore.Count >= needCount)
            {
                semaphore.Count -= needCount;
                TraceSemaphore($"wait handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
            }

            if (timeoutAddress != 0)
            {
                if (!ctx.TryReadUInt32(timeoutAddress, out var timeoutMicros))
                {
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                if (timeoutMicros == 0)
                {
                    _ = ctx.TryWriteUInt32(timeoutAddress, 0);
                    TraceSemaphore($"wait-timeout handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                    pollTimedOut = true;
                }
                else
                {
                    var deadline = GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromTicks((long)timeoutMicros * 10L));
                    var timedWaiter = new SemaphoreWaiter
                    {
                        NeedCount = needCount,
                        CancelEpochAtBlock = semaphore.CancelEpoch,
                        Timed = true,
                    };
                    if (GuestThreadExecution.RequestCurrentThreadBlock(
                            ctx,
                            "sceKernelWaitSema",
                            GetSemaphoreWakeKey(handle),
                            resumeHandler: () => CompleteBlockedTimedSemaWait(ctx, semaphore, timedWaiter, timeoutAddress, deadline),
                            wakeHandler: () => TryConsumeBlockedSemaWait(semaphore, timedWaiter),
                            blockDeadlineTimestamp: deadline))
                    {
                        semaphore.WaitingThreads++;
                        TraceSemaphore($"wait-block-timed handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} timeout_us={timeoutMicros} waiters={semaphore.WaitingThreads}");
                        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
                    }

                    // Host-owned threads cannot park in the guest scheduler; degrade to the
                    // immediate-timeout poll the callers already tolerate.
                    _ = ctx.TryWriteUInt32(timeoutAddress, 0);
                    TraceSemaphore($"wait-timeout handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                    pollTimedOut = true;
                }
            }

            if (!pollTimedOut)
            {
                var waiter = new SemaphoreWaiter
                {
                    NeedCount = needCount,
                    CancelEpochAtBlock = semaphore.CancelEpoch,
                };
                if (!GuestThreadExecution.RequestCurrentThreadBlock(
                        ctx,
                        "sceKernelWaitSema",
                        GetSemaphoreWakeKey(handle),
                        resumeHandler: () => CompleteBlockedSemaWait(semaphore, waiter),
                        wakeHandler: () => TryConsumeBlockedSemaWait(semaphore, waiter)))
                {
                    TraceSemaphore($"wait-would-block handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                    return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN);
                }

                semaphore.WaitingThreads++;
                TraceSemaphore($"wait-block handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
            }
        }

        GuestThreadExecution.Scheduler?.Pump(ctx, "sceKernelWaitSema");
        if ((++_semaPollBackoffCount & 255) == 0)
        {
            Thread.Sleep(0);
        }
        else
        {
            Thread.Yield();
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
    }

    [SysAbiExport(
        Nid = "12wOHk8ywb0",
        ExportName = "sceKernelPollSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var needCount = unchecked((int)ctx[CpuRegister.Rsi]);

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count < needCount)
            {
                TraceSemaphore($"poll-busy handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            semaphore.Count -= needCount;
            TraceSemaphore($"poll handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "4czppHBiriw",
        ExportName = "sceKernelSignalSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSignalSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var signalCount = unchecked((int)ctx[CpuRegister.Rsi]);

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (signalCount <= 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count > semaphore.MaxCount - signalCount)
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            semaphore.Count += signalCount;
            TraceSemaphore($"signal handle=0x{handle:X8} name='{semaphore.Name}' signal={signalCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
        }

        // Wake after releasing the gate (lock order: scheduler gate -> semaphore gate).
        // Wake everyone; the wake handler consumes the count per waiter, so a waiter
        // whose needCount exceeds the remaining count stays parked while a smaller
        // waiter can proceed.
        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "4DM06U2BNEY",
        ExportName = "sceKernelCancelSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var setCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var waitingThreadsAddress = ctx[CpuRegister.Rdx];

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        if (setCount > semaphore.MaxCount)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (waitingThreadsAddress != 0 && !ctx.TryWriteUInt32(waitingThreadsAddress, unchecked((uint)semaphore.WaitingThreads)))
            {
                return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            semaphore.Count = setCount < 0 ? semaphore.InitialCount : setCount;
            semaphore.CancelEpoch++;
            // WaitingThreads is NOT zeroed here: each canceled waiter decrements it
            // exactly once in its wake handler. Zeroing here as well would double-count
            // and silently absorb the increment of a waiter that parks between this
            // gate release and the wake-all below.
            TraceSemaphore($"cancel handle=0x{handle:X8} name='{semaphore.Name}' set={setCount} count={semaphore.Count}");
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "R1Jvn8bSCW8",
        ExportName = "sceKernelDeleteSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!_semaphores.TryRemove(handle, out var semaphore))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        // Delete succeeds even with blocked waiters; they wake with the deleted
        // result (the SCE kernel wakes waiters with the EACCES-class code).
        lock (semaphore.Gate)
        {
            semaphore.Deleted = true;
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        TraceSemaphore($"delete handle=0x{handle:X8} name='{semaphore.Name}'");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // Wake handler: runs under the scheduler's guest-thread gate (lock order:
    // scheduler gate -> semaphore gate). Returns true iff the waiter has a final
    // result and should be re-readied; false leaves it parked.
    private static bool TryConsumeBlockedSemaWait(KernelSemaphoreState semaphore, SemaphoreWaiter waiter)
    {
        lock (semaphore.Gate)
        {
            return TryConsumeBlockedSemaWaitLocked(semaphore, waiter);
        }
    }

    private static bool TryConsumeBlockedSemaWaitLocked(KernelSemaphoreState semaphore, SemaphoreWaiter waiter)
    {
        if (waiter.Result is not null)
        {
            return true;
        }

        if (semaphore.Deleted)
        {
            waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            TraceSemaphore($"wake-deleted name='{semaphore.Name}' need={waiter.NeedCount}");
            return true;
        }

        if (semaphore.CancelEpoch != waiter.CancelEpochAtBlock)
        {
            waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED;
            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            TraceSemaphore($"wake-canceled name='{semaphore.Name}' need={waiter.NeedCount}");
            return true;
        }

        if (semaphore.Count >= waiter.NeedCount)
        {
            semaphore.Count -= waiter.NeedCount;
            waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_OK;
            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            TraceSemaphore($"wake-consume name='{semaphore.Name}' need={waiter.NeedCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
            return true;
        }

        return false;
    }

    // Resume handler: runs on the woken guest thread outside the scheduler gate;
    // its return value becomes the guest's RAX for the resumed sceKernelWaitSema.
    private static int CompleteBlockedSemaWait(KernelSemaphoreState semaphore, SemaphoreWaiter waiter)
    {
        lock (semaphore.Gate)
        {
            if (waiter.Result is null && !TryConsumeBlockedSemaWaitLocked(semaphore, waiter))
            {
                // Nothing readies a parked semaphore waiter without the wake handler
                // resolving it, so reaching here means the scheduler contract changed.
                Console.Error.WriteLine(
                    $"[LOADER][GAP] sema.resume-no-outcome name='{semaphore.Name}' need={waiter.NeedCount} count={semaphore.Count}");
                waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
                semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            }

            return waiter.Result!.Value;
        }
    }

    private static int CompleteBlockedTimedSemaWait(
        CpuContext ctx,
        KernelSemaphoreState semaphore,
        SemaphoreWaiter waiter,
        ulong timeoutAddress,
        long deadlineTimestamp)
    {
        int result;
        lock (semaphore.Gate)
        {
            if (waiter.Result is null && !TryConsumeBlockedSemaWaitLocked(semaphore, waiter))
            {
                waiter.Result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
                semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                TraceSemaphore($"wake-timeout name='{semaphore.Name}' need={waiter.NeedCount} count={semaphore.Count} waiters={semaphore.WaitingThreads}");
            }

            result = waiter.Result!.Value;
        }

        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            var remainingTicks = deadlineTimestamp - Stopwatch.GetTimestamp();
            var remainingMicros = remainingTicks <= 0
                ? 0u
                : (uint)Math.Min(uint.MaxValue, remainingTicks / (double)Stopwatch.Frequency * 1_000_000d);
            _ = ctx.TryWriteUInt32(timeoutAddress, remainingMicros);
        }
        else
        {
            _ = ctx.TryWriteUInt32(timeoutAddress, 0);
        }

        return result;
    }

    private static void TraceSemaphore(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SEMA"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] sema.{message}");
        }
    }
}
