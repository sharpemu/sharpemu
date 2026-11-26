// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Diagnostics;

namespace SharpEmu.Libs.Kernel;

public static class KernelSemaphoreCompatExports
{
    private const int MaxSemaphoreNameLength = 128;
    // These exports return kernel errno values, not the runtime's synthetic result codes.
    private const OrbisGen2Result KernelInvalidArgument = unchecked((OrbisGen2Result)0x80020016);
    private const OrbisGen2Result KernelNoSuchObject = unchecked((OrbisGen2Result)0x80020003);
    private const OrbisGen2Result KernelBadAddress = unchecked((OrbisGen2Result)0x8002000E);
    private static readonly ConcurrentDictionary<uint, KernelSemaphoreState> _semaphores = new();
    private static int _nextSemaphoreHandle = 1;

    private sealed class KernelSemaphoreState
    {
        public required string Name { get; init; }
        // Formatted once at creation; signal/wait/cancel/delete all wake through this key.
        public required string WakeKey { get; init; }
        public required int InitialCount { get; init; }
        public required int MaxCount { get; init; }
        public int Count { get; set; }
        public int WaitingThreads { get; set; }
        public object Gate { get; } = new();
    }

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
            return SetReturn(ctx, KernelInvalidArgument);
        }

        if (!KernelMemoryCompatExports.TryReadNullTerminatedUtf8(
                ctx, nameAddress, MaxSemaphoreNameLength, out var name))
        {
            return SetReturn(ctx, KernelBadAddress);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        _semaphores[handle] = new KernelSemaphoreState
        {
            Name = name,
            WakeKey = GetSemaphoreWakeKey(handle),
            InitialCount = initialCount,
            MaxCount = maxCount,
            Count = initialCount,
        };

        if (!ctx.TryWriteUInt64(semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            return SetReturn(ctx, KernelBadAddress);
        }

        if (_traceSema)
        {
            TraceSemaphore($"create handle=0x{handle:X8} name='{name}' attr=0x{attr:X} init={initialCount} max={maxCount}");
        }
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
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
            return SetReturn(ctx, KernelNoSuchObject);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return SetReturn(ctx, KernelInvalidArgument);
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !TryReadUInt32(ctx, timeoutAddress, out timeoutUsec))
        {
            return SetReturn(ctx, KernelBadAddress);
        }

        return WaitSemaphoreCore(
            ctx,
            semaphore,
            handle,
            needCount,
            hasTimeout: timeoutAddress != 0,
            timeoutAddress,
            timeoutUsec,
            "sceKernelWaitSema");
    }

    // The POSIX sem_* calls follow libc: 0, or -1 with errno set (FreeBSD numbers). Mono's mono_os_sem_timedwait
    // takes sem_trywait failing with anything but EAGAIN or EINTR as fatal, and an SCE code left errno at 0
    // ("sem_trywait failed with No error"), which killed the Mono thread and left Wukong's UObjects half-built.
    private const int ErrnoEfault = 14;
    private const int ErrnoEinval = 22;
    private const int ErrnoEagain = 35;
    private const int ErrnoEtimedout = 60;

    // Set while a POSIX sem_* export runs its core, so a wait that blocks answers its resume the libc way too.
    [ThreadStatic]
    private static bool _posixLibcCall;

    private static int LibcCall(CpuContext ctx, Func<CpuContext, int> core, bool tryWait = false)
    {
        var previous = _posixLibcCall;
        _posixLibcCall = true;
        int result;
        try
        {
            result = core(ctx);
        }
        finally
        {
            _posixLibcCall = previous;
        }

        return LibcResult(ctx, result, tryWait);
    }

    private static int LibcResult(CpuContext ctx, int result, bool tryWait = false)
    {
        if (result == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var errno = result switch
        {
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY or (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN => tryWait ? ErrnoEagain : 16,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT => ErrnoEtimedout,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT or (int)KernelBadAddress => ErrnoEfault,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT or (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND or
                (int)KernelNoSuchObject or (int)KernelInvalidArgument => ErrnoEinval,
            _ when ((uint)result & 0xFFFF_0000u) == 0x8002_0000u => result & 0xFFFF,
            _ => ErrnoEinval,
        };
        return LibcFailure(ctx, errno);
    }

    private static int LibcFailure(CpuContext ctx, int errno)
    {
        _ = KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = unchecked((ulong)-1L);
        return -1;
    }

    private static int WaitSemaphoreCore(
        CpuContext ctx,
        KernelSemaphoreState semaphore,
        uint handle,
        int needCount,
        bool hasTimeout,
        ulong timeoutAddress,
        uint timeoutUsec,
        string operationName)
    {
        var takeLatePost = _posixTimedWait;
        var libc = _posixLibcCall;
        lock (semaphore.Gate)
        {
            if (semaphore.Count >= needCount)
            {
                semaphore.Count -= needCount;
                if (timeoutAddress != 0)
                {
                    _ = TryWriteUInt32(ctx, timeoutAddress, timeoutUsec);
                }

                if (_traceSema)
                {
                    TraceSemaphore($"wait handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} timeout={(hasTimeout ? timeoutUsec : "infinite")}");
                }
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
            }

            semaphore.WaitingThreads++;
        }

        // Block cooperatively: the wake predicate atomically acquires the
        // tokens (so a wake commits the acquisition), while the resume
        // handler distinguishes a real acquisition from a deadline expiry.
        var acquired = false;
        var deadline = hasTimeout
            ? GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMicroseconds(timeoutUsec))
            : 0;

        bool WakePredicate()
        {
            lock (semaphore.Gate)
            {
                if (semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    acquired = true;
                    return true;
                }

                return false;
            }
        }

        int ResumeWait()
        {
            if (timeoutAddress != 0)
            {
                _ = TryWriteUInt32(ctx, timeoutAddress, 0);
            }

            if (acquired)
            {
                if (_traceSema)
                {
                    TraceSemaphore($"wait-wake handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                }
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            lock (semaphore.Gate)
            {
                semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                // FreeBSD libc's sem_timedwait (sem_new.c) re-checks the count
                // after the kernel wait times out and takes a unit posted meanwhile instead of failing. Mono retires a
                // thread-pool worker whose park times out; a post made to wake it in that gap stayed in the count while
                // the worker exited, so the queued work had no thread and Wukong's level load never finished.
                if (takeLatePost && semaphore.WaitingThreads == 0 && semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    if (_traceSema)
                    {
                        TraceSemaphore($"wait-late-take handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                    }

                    return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                }
            }

            if (_traceSema)
            {
                TraceSemaphore($"wait-timeout handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
            }
            return libc ? LibcFailure(ctx, ErrnoEtimedout) : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                operationName,
                GetSemaphoreWakeKey(handle),
                ResumeWait,
                WakePredicate,
                deadline))
        {
            // A signal may have arrived between releasing the semaphore gate
            // (after incrementing WaitingThreads) and the scheduler registering
            // this block.  When that happens WakeBlockedThreads cannot find the
            // waiter yet and the exit-handler re-check runs later; a re-check
            // here keeps the thread from yielding to the scheduler at all when
            // the count is already sufficient.
            lock (semaphore.Gate)
            {
                if (semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    GuestThreadExecution.TryConsumeCurrentThreadBlock(out _);
                    if (_traceSema)
                    {
                        TraceSemaphore($"wait-recheck handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} {FormatCallSite(ctx)}");
                    }
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
                }
            }

            if (_traceSema)
            {
                TraceSemaphore($"wait-block handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} timeout={(hasTimeout ? timeoutUsec : "infinite")} waiters={semaphore.WaitingThreads} {FormatCallSite(ctx)}");
            }
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // Not a guest thread (or no scheduler): fall back to a host-thread
        // wait so the semantics still hold on non-cooperative callers.
        return WaitSemaphoreOnHostThread(
            ctx,
            semaphore,
            handle,
            needCount,
            hasTimeout,
            timeoutAddress,
            timeoutUsec);
    }

    private static int WaitSemaphoreOnHostThread(
        CpuContext ctx,
        KernelSemaphoreState semaphore,
        uint handle,
        int needCount,
        bool hasTimeout,
        ulong timeoutAddress,
        uint timeoutUsec)
    {
        var deadlineMs = hasTimeout
            ? Environment.TickCount64 + Math.Max(1L, timeoutUsec / 1000L)
            : long.MaxValue;
        if (_traceSema)
        {
            lock (semaphore.Gate)
            {
                TraceSemaphore(
                    $"wait-host-block handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} " +
                    $"count={semaphore.Count} timeout={(hasTimeout ? timeoutUsec : "infinite")} {FormatCallSite(ctx)}");
            }
        }

        while (true)
        {
            lock (semaphore.Gate)
            {
                if (semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    if (_traceSema)
                    {
                        TraceSemaphore(
                            $"wait-host-wake handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} {FormatCallSite(ctx)}");
                    }
                    if (timeoutAddress != 0)
                    {
                        _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                    }

                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
                }

                var remaining = deadlineMs - Environment.TickCount64;
                if (hasTimeout && remaining <= 0)
                {
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    if (timeoutAddress != 0)
                    {
                        _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                    }
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                }

                var waitMilliseconds = hasTimeout
                    ? (int)Math.Max(1L, Math.Min(remaining, 10L))
                    : 10;
                Monitor.Wait(semaphore.Gate, waitMilliseconds);
            }

            GuestThreadExecution.Scheduler?.DeliverPendingGuestExceptionIfReady(ctx);
        }
    }

    private static string GetSemaphoreWakeKey(uint handle) => $"sceKernelWaitSema:{handle:X8}";

    [SysAbiExport(
        Nid = "12wOHk8ywb0",
        ExportName = "sceKernelPollSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollSema(CpuContext ctx, uint handle, int needCount)
    {
        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, KernelNoSuchObject);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return SetReturn(ctx, KernelInvalidArgument);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count < needCount)
            {
                if (_traceSema)
                {
                    TraceSemaphore($"poll-busy handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                }
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            semaphore.Count -= needCount;
            if (_traceSema)
            {
                TraceSemaphore($"poll handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
            }
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "4czppHBiriw",
        ExportName = "sceKernelSignalSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSignalSema(CpuContext ctx, uint handle, int signalCount)
    {
        var signalTrace = SemaphoreSignalProfile.Begin(handle, signalCount);
        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            signalTrace.Record(SemaphoreSignalProfile.Stage.Rejected,
                result: (int)KernelNoSuchObject);
            return SetReturn(ctx, KernelNoSuchObject);
        }

        if (signalCount < 0)
        {
            signalTrace.Record(SemaphoreSignalProfile.Stage.Rejected,
                result: (int)KernelInvalidArgument);
            return SetReturn(ctx, KernelInvalidArgument);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count > semaphore.MaxCount - signalCount)
            {
                signalTrace.Record(SemaphoreSignalProfile.Stage.Rejected,
                    result: (int)KernelInvalidArgument);
                return SetReturn(ctx, KernelInvalidArgument);
            }

            semaphore.Count += signalCount;
            signalTrace.Record(SemaphoreSignalProfile.Stage.Published, semaphore.Count, semaphore.WaitingThreads);
            // Wake host-thread waiters parked in the fallback path.
            Monitor.PulseAll(semaphore.Gate);
            if (_traceSema)
            {
                TraceSemaphore($"signal handle=0x{handle:X8} name='{semaphore.Name}' signal={signalCount} count={semaphore.Count} waiters={semaphore.WaitingThreads} {FormatCallSite(ctx)}");
            }
        }

        // Wake cooperatively-blocked guest threads; their wake predicate
        // acquires the tokens atomically, so this respects the new count.
        signalTrace.Record(SemaphoreSignalProfile.Stage.WakeStarted);
        var wakeCount = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle)) ?? 0;
        signalTrace.Record(SemaphoreSignalProfile.Stage.WakeFinished, result: wakeCount);
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "4DM06U2BNEY",
        ExportName = "sceKernelCancelSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelSema(CpuContext ctx, uint handle, int setCount, ulong waitingThreadsAddress)
    {
        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, KernelNoSuchObject);
        }

        if (setCount > semaphore.MaxCount)
        {
            return SetReturn(ctx, KernelInvalidArgument);
        }

        lock (semaphore.Gate)
        {
            if (waitingThreadsAddress != 0 && !TryWriteUInt32(ctx, waitingThreadsAddress, unchecked((uint)semaphore.WaitingThreads)))
            {
                return SetReturn(ctx, KernelBadAddress);
            }

            semaphore.Count = setCount < 0 ? semaphore.InitialCount : setCount;
            semaphore.WaitingThreads = 0;
            Monitor.PulseAll(semaphore.Gate);
            if (_traceSema)
            {
                TraceSemaphore($"cancel handle=0x{handle:X8} name='{semaphore.Name}' set={setCount} count={semaphore.Count}");
            }
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
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
            return SetReturn(ctx, KernelNoSuchObject);
        }

        if (_traceSema)
        {
            TraceSemaphore($"delete handle=0x{handle:X8} name='{semaphore.Name}'");
        }
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pDuPEf3m4fI",
        ExportName = "sem_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemInit(CpuContext ctx) => LibcCall(ctx, PosixSemInitCore);

    private static int PosixSemInitCore(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var initialCountValue = ctx[CpuRegister.Rdx];
        if (semaphoreAddress == 0 || initialCountValue > int.MaxValue)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        var initialCount = unchecked((int)initialCountValue);
        _semaphores[handle] = new KernelSemaphoreState
        {
            Name = $"posix@0x{semaphoreAddress:X16}",
            WakeKey = GetSemaphoreWakeKey(handle),
            InitialCount = initialCount,
            MaxCount = int.MaxValue,
            Count = initialCount,
        };
        if (!TryWriteUInt32(ctx, semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceSema)
        {
            TraceSemaphore($"posix-init address=0x{semaphoreAddress:X16} handle=0x{handle:X8} count={initialCount}");
        }
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "GEnUkDZoUwY",
        ExportName = "scePthreadSemInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemInit(CpuContext ctx)
    {
        // scePthreadSemInit(sem, flag, value, name) seems to only support private semaphores
        if (ctx[CpuRegister.Rsi] != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return PosixSemInitCore(ctx);
    }

    [SysAbiExport(
        Nid = "YCV5dGGBcCo",
        ExportName = "sem_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemWait(CpuContext ctx) => LibcCall(ctx, PosixSemWaitCore);

    private static int PosixSemWaitCore(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        return KernelWaitSema(ctx);
    }

    [SysAbiExport(
        Nid = "C36iRE0F5sE",
        ExportName = "scePthreadSemWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemWait(CpuContext ctx) => PosixSemWaitCore(ctx);

    [SysAbiExport(
        Nid = "WBWzsRifCEA",
        ExportName = "sem_trywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTryWait(CpuContext ctx) => LibcCall(ctx, PosixSemTryWaitCore, tryWait: true);

    private static int PosixSemTryWaitCore(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        return KernelPollSema(ctx, handle, 1);
    }

    [SysAbiExport(
        Nid = "H2a+IN9TP0E",
        ExportName = "scePthreadSemTrywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemTryWait(CpuContext ctx)
    {
        var result = PosixSemTryWaitCore(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN)
            : result;
    }

    [SysAbiExport(
        Nid = "fjN6NQHhK8k",
        ExportName = "scePthreadSemTimedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemTimedWait(CpuContext ctx)
    {
        var timeoutUsec = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        }

        return WaitSemaphoreCore(
            ctx,
            semaphore,
            handle,
            needCount: 1,
            hasTimeout: true,
            timeoutAddress: 0,
            timeoutUsec,
            "scePthreadSemTimedwait");
    }

    [SysAbiExport(
        Nid = "w5IHyvahg-o",
        ExportName = "sem_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTimedWait(CpuContext ctx) => LibcCall(ctx, PosixSemTimedWaitCore);

    // abs_timeout is a CLOCK_REALTIME timespec, not the SCE wait's pointer to a relative microsecond count: read as
    // one, the low bits of tv_sec made a wait of about half an hour and the remaining time was written over tv_sec.
    private static int PosixSemTimedWaitCore(CpuContext ctx)
    {
        var deadlineAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle) ||
            !_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (deadlineAddress == 0 ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, deadlineAddress, out var rawSeconds) ||
            !KernelMemoryCompatExports.TryReadUInt64Compat(ctx, deadlineAddress + sizeof(long), out var rawNanoseconds))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var seconds = unchecked((long)rawSeconds);
        var nanoseconds = unchecked((long)rawNanoseconds);
        if (nanoseconds is < 0 or >= 1_000_000_000)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        KernelRuntimeCompatExports.GetClockTime(0, out var nowSeconds, out var nowNanoseconds);
        var deltaSeconds = seconds - nowSeconds;
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
            var remainingNanoseconds = deltaSeconds * 1_000_000_000L + nanoseconds - nowNanoseconds;
            var remainingUsec = remainingNanoseconds <= 0 ? 0 : (remainingNanoseconds + 999L) / 1_000L;
            timeoutUsec = (uint)Math.Min(remainingUsec, uint.MaxValue);
        }

        _posixTimedWait = true;
        try
        {
            return WaitSemaphoreCore(
                ctx,
                semaphore,
                handle,
                needCount: 1,
                hasTimeout: true,
                timeoutAddress: 0,
                timeoutUsec,
                "sem_timedwait");
        }
        finally
        {
            _posixTimedWait = false;
        }
    }

    // Set while sem_timedwait runs KernelWaitSema, so a timed-out wait follows libc (see ResumeWait).
    [ThreadStatic]
    private static bool _posixTimedWait;

    [SysAbiExport(
        Nid = "IKP8typ0QUk",
        ExportName = "sem_post",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemPost(CpuContext ctx) => LibcCall(ctx, PosixSemPostCore);

    private static int PosixSemPostCore(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        return KernelSignalSema(ctx, handle, 1);
    }

    [SysAbiExport(
        Nid = "aishVAiFaYM",
        ExportName = "scePthreadSemPost",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemPost(CpuContext ctx) => PosixSemPostCore(ctx);

    [SysAbiExport(
        Nid = "Bq+LRV-N6Hk",
        ExportName = "sem_getvalue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemGetValue(CpuContext ctx) => LibcCall(ctx, PosixSemGetValueCore);

    private static int PosixSemGetValueCore(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0 ||
            !TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle) ||
            !_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        int count;
        lock (semaphore.Gate)
        {
            count = semaphore.Count;
        }

        return TryWriteUInt32(ctx, valueAddress, unchecked((uint)count))
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cDW233RAwWo",
        ExportName = "sem_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemDestroy(CpuContext ctx) => LibcCall(ctx, PosixSemDestroyCore);

    private static int PosixSemDestroyCore(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        var result = KernelDeleteSema(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            _ = TryWriteUInt32(ctx, semaphoreAddress, 0);
        }

        return result;
    }

    [SysAbiExport(
        Nid = "Vwc+L05e6oE",
        ExportName = "scePthreadSemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemDestroy(CpuContext ctx) => PosixSemDestroyCore(ctx);

    private static bool TryGetPosixSemaphoreHandle(CpuContext ctx, ulong semaphoreAddress, out uint handle)
    {
        handle = 0;
        return semaphoreAddress != 0 &&
               TryReadUInt32(ctx, semaphoreAddress, out handle) &&
               handle != 0;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0 || maxLength <= 0)
        {
            return false;
        }

        var bytes = new byte[Math.Min(maxLength, 4096)];
        Span<byte> current = stackalloc byte[1];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)i, current))
            {
                return false;
            }

            if (current[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, i);
                return true;
            }

            bytes[i] = current[0];
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    // Call sites must check this before building the interpolated message; the trace
    // strings would otherwise be allocated on every semaphore op even with tracing off.
    private static readonly bool _traceSema =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SEMA"), "1", StringComparison.Ordinal);

    private static void TraceSemaphore(string message)
    {
        if (!_traceSema)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] sema.{message}");
    }

    private static string FormatCallSite(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var returnAddress);
        return $"guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} ret=0x{returnAddress:X16}";
    }
}
