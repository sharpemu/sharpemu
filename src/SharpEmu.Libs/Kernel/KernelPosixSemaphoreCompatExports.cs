// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelPosixSemaphoreCompatExports
{
    private const int ErrnoBusy = 16;
    private const int ErrnoFault = 14;
    private const int ErrnoInvalidArgument = 22;
    private const int ErrnoTryAgain = 35;
    private const int ErrnoTimedOut = 60;
    private const int ErrnoOverflow = 84;
    private const uint SemaphoreValueMax = int.MaxValue;

    private static readonly ConcurrentDictionary<ulong, PosixSemaphoreState> _semaphores = new();

    private sealed class PosixSemaphoreState
    {
        public required ulong Address { get; init; }
        public required string WakeKey { get; init; }
        public int Count { get; set; }
        public int WaitingThreads { get; set; }
        public bool Destroyed { get; set; }
        public object Gate { get; } = new();
    }

    private sealed class PosixSemaphoreWaiter : IGuestThreadBlockWaiter
    {
        public required CpuContext Context { get; init; }
        public required PosixSemaphoreState Semaphore { get; init; }
        public bool Timed { get; init; }
        public int? Result { get; set; }
        public int ErrorNumber { get; set; }

        public int Resume() => CompleteBlockedWait(this);

        public bool TryWake() => TryReserveBlockedWait(this);
    }

    [SysAbiExport(
        Nid = "pDuPEf3m4fI",
        ExportName = "sem_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemaphoreInit(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var initialValue = unchecked((uint)ctx[CpuRegister.Rdx]);

        if (semaphoreAddress == 0 || initialValue > SemaphoreValueMax)
        {
            return PosixFailure(ctx, ErrnoInvalidArgument);
        }

        if (!ctx.TryReadUInt32(semaphoreAddress, out _))
        {
            return PosixFailure(ctx, ErrnoFault);
        }

        if (_semaphores.TryGetValue(semaphoreAddress, out var previous))
        {
            lock (previous.Gate)
            {
                if (!previous.Destroyed && previous.WaitingThreads != 0)
                {
                    return PosixFailure(ctx, ErrnoBusy);
                }
            }
        }

        if (!ctx.TryWriteUInt32(semaphoreAddress, initialValue))
        {
            return PosixFailure(ctx, ErrnoFault);
        }

        var state = new PosixSemaphoreState
        {
            Address = semaphoreAddress,
            WakeKey = GetWakeKey(semaphoreAddress),
            Count = checked((int)initialValue),
        };

        if (previous is not null)
        {
            lock (previous.Gate)
            {
                previous.Destroyed = true;
            }
        }

        _semaphores[semaphoreAddress] = state;
        return PosixSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "cDW233RAwWo",
        ExportName = "sem_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemaphoreDestroy(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetSemaphore(semaphoreAddress, out var semaphore))
        {
            return PosixFailure(ctx, ErrnoInvalidArgument);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Destroyed)
            {
                return PosixFailure(ctx, ErrnoInvalidArgument);
            }

            if (semaphore.WaitingThreads != 0)
            {
                return PosixFailure(ctx, ErrnoBusy);
            }

            semaphore.Destroyed = true;
        }

        _semaphores.TryRemove(new KeyValuePair<ulong, PosixSemaphoreState>(semaphoreAddress, semaphore));
        _ = ctx.TryWriteUInt32(semaphoreAddress, 0);
        return PosixSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "YCV5dGGBcCo",
        ExportName = "sem_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemaphoreWait(CpuContext ctx) =>
        WaitCore(ctx, timed: false, deadlineTimestamp: 0);

    [SysAbiExport(
        Nid = "WBWzsRifCEA",
        ExportName = "sem_trywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemaphoreTryWait(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetSemaphore(semaphoreAddress, out var semaphore))
        {
            return PosixFailure(ctx, ErrnoInvalidArgument);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Destroyed)
            {
                return PosixFailure(ctx, ErrnoInvalidArgument);
            }

            if (semaphore.Count == 0)
            {
                return PosixFailure(ctx, ErrnoTryAgain);
            }

            semaphore.Count--;
            SyncGuestCount(ctx, semaphore);
        }

        return PosixSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "w5IHyvahg-o",
        ExportName = "sem_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemaphoreTimedWait(CpuContext ctx)
    {
        var timeoutAddress = ctx[CpuRegister.Rsi];
        Span<byte> timeout = stackalloc byte[sizeof(long) * 2];
        if (timeoutAddress == 0 || !ctx.Memory.TryRead(timeoutAddress, timeout))
        {
            return PosixFailure(ctx, ErrnoFault);
        }

        var seconds = BinaryPrimitives.ReadInt64LittleEndian(timeout);
        var nanoseconds = BinaryPrimitives.ReadInt64LittleEndian(timeout[sizeof(long)..]);
        if (nanoseconds < 0 || nanoseconds >= 1_000_000_000L)
        {
            return PosixFailure(ctx, ErrnoInvalidArgument);
        }

        var deadlineTimestamp = ComputeRealtimeDeadline(seconds, nanoseconds, out var expired);
        if (expired)
        {
            var semaphoreAddress = ctx[CpuRegister.Rdi];
            if (!TryGetSemaphore(semaphoreAddress, out var semaphore))
            {
                return PosixFailure(ctx, ErrnoInvalidArgument);
            }

            lock (semaphore.Gate)
            {
                if (semaphore.Destroyed)
                {
                    return PosixFailure(ctx, ErrnoInvalidArgument);
                }

                if (semaphore.Count == 0)
                {
                    return PosixFailure(ctx, ErrnoTimedOut);
                }

                semaphore.Count--;
                SyncGuestCount(ctx, semaphore);
                return PosixSuccess(ctx);
            }
        }

        return WaitCore(ctx, timed: true, deadlineTimestamp);
    }

    [SysAbiExport(
        Nid = "IKP8typ0QUk",
        ExportName = "sem_post",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemaphorePost(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetSemaphore(semaphoreAddress, out var semaphore))
        {
            return PosixFailure(ctx, ErrnoInvalidArgument);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Destroyed)
            {
                return PosixFailure(ctx, ErrnoInvalidArgument);
            }

            if (semaphore.Count == SemaphoreValueMax)
            {
                return PosixFailure(ctx, ErrnoOverflow);
            }

            semaphore.Count++;
            SyncGuestCount(ctx, semaphore);
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(semaphore.WakeKey, maxCount: 1);
        return PosixSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "Bq+LRV-N6Hk",
        ExportName = "sem_getvalue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemaphoreGetValue(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return PosixFailure(ctx, ErrnoFault);
        }

        if (!TryGetSemaphore(semaphoreAddress, out var semaphore))
        {
            return PosixFailure(ctx, ErrnoInvalidArgument);
        }

        int value;
        lock (semaphore.Gate)
        {
            if (semaphore.Destroyed)
            {
                return PosixFailure(ctx, ErrnoInvalidArgument);
            }

            value = semaphore.Count;
        }

        if (!ctx.TryWriteInt32(valueAddress, value))
        {
            return PosixFailure(ctx, ErrnoFault);
        }

        return PosixSuccess(ctx);
    }

    internal static void ResetForTests()
    {
        foreach (var semaphore in _semaphores.Values)
        {
            lock (semaphore.Gate)
            {
                semaphore.Destroyed = true;
            }
        }

        _semaphores.Clear();
    }

    private static int WaitCore(CpuContext ctx, bool timed, long deadlineTimestamp)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetSemaphore(semaphoreAddress, out var semaphore))
        {
            return PosixFailure(ctx, ErrnoInvalidArgument);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Destroyed)
            {
                return PosixFailure(ctx, ErrnoInvalidArgument);
            }

            if (semaphore.Count != 0)
            {
                semaphore.Count--;
                SyncGuestCount(ctx, semaphore);
                return PosixSuccess(ctx);
            }

            var waiter = new PosixSemaphoreWaiter
            {
                Context = ctx,
                Semaphore = semaphore,
                Timed = timed,
            };
            if (GuestThreadExecution.RequestCurrentThreadBlock(
                    ctx,
                    timed ? "sem_timedwait" : "sem_wait",
                    semaphore.WakeKey,
                    waiter,
                    blockDeadlineTimestamp: deadlineTimestamp))
            {
                semaphore.WaitingThreads++;
                return PosixSuccess(ctx);
            }
        }

        return PosixFailure(ctx, timed ? ErrnoTimedOut : ErrnoTryAgain);
    }

    private static bool TryReserveBlockedWait(PosixSemaphoreWaiter waiter)
    {
        var semaphore = waiter.Semaphore;
        lock (semaphore.Gate)
        {
            if (waiter.Result.HasValue)
            {
                return true;
            }

            if (semaphore.Destroyed)
            {
                waiter.Result = -1;
                waiter.ErrorNumber = ErrnoInvalidArgument;
            }
            else if (semaphore.Count != 0)
            {
                semaphore.Count--;
                waiter.Result = 0;
                SyncGuestCount(waiter.Context, semaphore);
            }
            else
            {
                return false;
            }

            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            return true;
        }
    }

    private static int CompleteBlockedWait(PosixSemaphoreWaiter waiter)
    {
        var semaphore = waiter.Semaphore;
        lock (semaphore.Gate)
        {
            if (!waiter.Result.HasValue)
            {
                if (!semaphore.Destroyed && semaphore.Count != 0)
                {
                    semaphore.Count--;
                    waiter.Result = 0;
                    SyncGuestCount(waiter.Context, semaphore);
                }
                else
                {
                    waiter.Result = -1;
                    waiter.ErrorNumber = semaphore.Destroyed
                        ? ErrnoInvalidArgument
                        : waiter.Timed
                            ? ErrnoTimedOut
                            : ErrnoTryAgain;
                }

                semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
            }
        }

        if (waiter.Result.Value == -1)
        {
            _ = KernelRuntimeCompatExports.TrySetErrno(waiter.Context, waiter.ErrorNumber);
        }

        return waiter.Result.Value;
    }

    private static long ComputeRealtimeDeadline(long seconds, long nanoseconds, out bool expired)
    {
        KernelRuntimeCompatExports.ResolveClockTime(
            KernelRuntimeCompatExports.ClockRealtime,
            out var nowSeconds,
            out var nowNanoseconds);

        var totalSeconds = (double)seconds - nowSeconds +
            ((double)nanoseconds - nowNanoseconds) / 1_000_000_000d;
        if (totalSeconds <= 0)
        {
            expired = true;
            return Stopwatch.GetTimestamp();
        }

        expired = false;
        var timeout = totalSeconds >= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(totalSeconds);
        return GuestThreadExecution.ComputeDeadlineTimestamp(timeout);
    }

    private static bool TryGetSemaphore(ulong address, out PosixSemaphoreState semaphore)
    {
        if (address != 0 && _semaphores.TryGetValue(address, out var found))
        {
            semaphore = found;
            return true;
        }

        semaphore = null!;
        return false;
    }

    private static void SyncGuestCount(CpuContext ctx, PosixSemaphoreState semaphore) =>
        _ = ctx.TryWriteUInt32(semaphore.Address, unchecked((uint)semaphore.Count));

    private static string GetWakeKey(ulong address) => $"posix_sem:0x{address:X16}";

    private static int PosixSuccess(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int PosixFailure(CpuContext ctx, int errorNumber)
    {
        _ = KernelRuntimeCompatExports.TrySetErrno(ctx, errorNumber);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
