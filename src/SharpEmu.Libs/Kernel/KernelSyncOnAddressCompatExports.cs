// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// SyncOnAddress waits only if the guest value equals the expected value.
// Wake selects a fixed number of registered waiters at the same address.
public static class KernelSyncOnAddressCompatExports
{
    private const int UmtxOperationWait = 2;
    private const int UmtxOperationWake = 3;
    private const int PosixEperm = 1;
    private const int PosixEacces = 13;
    private const int PosixEfault = 14;
    private const int PosixEinval = 22;
    private const int PosixEagain = 35;
    private const int PosixEtimedout = 60;
    private const int PosixEcanceled = 85;
    private const int PosixEother = 1062;

    private sealed class SyncWaiter : IGuestThreadBlockWaiter
    {
        private readonly CpuContext _context;
        private readonly ulong _expected;
        private readonly bool _is64Bit;
        private readonly bool _usePosixResult;
        private readonly long _deadlineTimestamp;
        private readonly object _hostGate = new();
        private readonly long _registeredTicks;
        private readonly int _registeredManagedThread;
        private int _wakeRequested;
        private int _resumeRecorded;

        public SyncWaiter(
            CpuContext context,
            ulong address,
            ulong expected,
            bool is64Bit,
            bool usePosixResult,
            long deadlineTimestamp)
        {
            _context = context;
            Address = address;
            _expected = expected;
            _is64Bit = is64Bit;
            _usePosixResult = usePosixResult;
            _deadlineTimestamp = deadlineTimestamp;
            _registeredTicks = KernelSyncOnAddressProfile.Enabled
                ? Stopwatch.GetTimestamp()
                : 0L;
            _registeredManagedThread = KernelSyncOnAddressProfile.Enabled
                ? Environment.CurrentManagedThreadId
                : 0;
            Id = Interlocked.Increment(ref _nextWaiterId);
        }

        public ulong Address { get; }

        public long Id { get; }

        public LinkedListNode<SyncWaiter>? RegistryNode { get; set; }

        public string WakeKey => $"sceKernelSyncOnAddress:{Address:X16}:{Id:X16}";

        public int Resume()
        {
            Unregister(this);
            if (Volatile.Read(ref _wakeRequested) != 0)
            {
                RecordResume(explicitWake: true, valueChanged: false, timedOut: false, faulted: false);
                return Complete((int)OrbisGen2Result.ORBIS_GEN2_OK);
            }

            if (!TryReadValue(_context, Address, _is64Bit, out var value))
            {
                RecordResume(explicitWake: false, valueChanged: false, timedOut: false, faulted: true);
                return Complete((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            var valueChanged = value != _expected;
            RecordResume(
                explicitWake: false,
                valueChanged,
                timedOut: !valueChanged,
                faulted: false);
            var result = valueChanged
                ? (int)OrbisGen2Result.ORBIS_GEN2_OK
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
            return Complete(result);
        }

        public bool TryWake() => Volatile.Read(ref _wakeRequested) != 0;

        public bool RequestWake()
        {
            if (_deadlineTimestamp != 0 && Stopwatch.GetTimestamp() >= _deadlineTimestamp)
            {
                return false;
            }

            if (Interlocked.Exchange(ref _wakeRequested, 1) != 0)
            {
                return false;
            }

            lock (_hostGate)
            {
                Monitor.Pulse(_hostGate);
            }

            return true;
        }

        public int WaitOnHost()
        {
            while (Volatile.Read(ref _wakeRequested) == 0)
            {
                if (!TryReadValue(_context, Address, _is64Bit, out var current) ||
                    current != _expected)
                {
                    break;
                }

                var waitSlice = TimeSpan.FromMilliseconds(10);
                if (_deadlineTimestamp != 0)
                {
                    var remainingTicks = _deadlineTimestamp - Stopwatch.GetTimestamp();
                    if (remainingTicks <= 0)
                    {
                        break;
                    }

                    var remainingSeconds = remainingTicks / (double)Stopwatch.Frequency;
                    if (remainingSeconds < waitSlice.TotalSeconds)
                    {
                        waitSlice = TimeSpan.FromSeconds(remainingSeconds);
                    }
                }

                lock (_hostGate)
                {
                    if (Volatile.Read(ref _wakeRequested) == 0)
                    {
                        // Round up so a sub-millisecond remainder does not spin.
                        Monitor.Wait(_hostGate, (int)Math.Ceiling(waitSlice.TotalMilliseconds));
                    }
                }

                GuestThreadExecution.Scheduler?.DeliverPendingGuestExceptionIfReady(_context);
            }

            return Resume();
        }

        private int Complete(int result) => _usePosixResult
            ? SetPosixReturn(_context, result)
            : result;

        private void RecordResume(
            bool explicitWake,
            bool valueChanged,
            bool timedOut,
            bool faulted)
        {
            if (!KernelSyncOnAddressProfile.Enabled ||
                Interlocked.Exchange(ref _resumeRecorded, 1) != 0)
            {
                return;
            }

            KernelSyncOnAddressProfile.RecordResume(
                Address,
                _registeredManagedThread,
                Stopwatch.GetTimestamp() - _registeredTicks,
                explicitWake,
                valueChanged,
                timedOut,
                faulted);
        }
    }

    private static readonly object _registryGate = new();
    private static readonly Dictionary<ulong, LinkedList<SyncWaiter>> _waitersByAddress = new();
    private static long _nextWaiterId;

    [SysAbiExport(
        Nid = "Hc4CaR6JBL0",
        ExportName = "sceKernelSyncOnAddressWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait(CpuContext ctx) => Wait(ctx, is64Bit: false);

    [SysAbiExport(
        Nid = "B2n8aDorSH4",
        ExportName = "sceKernelSyncOnAddressWait32",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait32(CpuContext ctx) => Wait(ctx, is64Bit: false);

    [SysAbiExport(
        Nid = "PZQhiiLXRFs",
        ExportName = "sceKernelSyncOnAddressWait64",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWait64(CpuContext ctx) => Wait(ctx, is64Bit: true);

    [SysAbiExport(
        Nid = "04AjkP0jO9U",
        ExportName = "_umtx_op",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int UmtxOp(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var operation = unchecked((int)ctx[CpuRegister.Rsi]);
        var value = ctx[CpuRegister.Rdx];
        var secondaryAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];

        if (secondaryAddress != 0)
        {
            return SetPosixReturn(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        switch (operation)
        {
            case UmtxOperationWait:
            {
                TimeSpan? timeout = null;
                long deadline = 0;
                if (timeoutAddress != 0)
                {
                    if (!ctx.TryReadUInt64(timeoutAddress, out var secondsBits) ||
                        !ctx.TryReadUInt64(timeoutAddress + sizeof(long), out var nanosecondsBits))
                    {
                        return SetPosixReturn(
                            ctx,
                            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                    }

                    var seconds = unchecked((long)secondsBits);
                    var nanoseconds = unchecked((long)nanosecondsBits);
                    if (seconds < 0 || nanoseconds < 0 || nanoseconds >= 1_000_000_000L)
                    {
                        return SetPosixReturn(
                            ctx,
                            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
                    }

                    timeout = CreateTimespecDuration(seconds, nanoseconds);
                    deadline = GuestThreadExecution.ComputeDeadlineTimestamp(timeout.Value);
                }

                return WaitCore(
                    ctx,
                    is64Bit: true,
                    address,
                    value,
                    timeout,
                    deadline,
                    usePosixResult: true,
                    reason: "_umtx_op.wait");
            }

            case UmtxOperationWake:
                return Wake(
                    ctx,
                    address,
                    unchecked((int)value),
                    usePosixResult: true);

            default:
                return SetPosixReturn(
                    ctx,
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
    }

    [SysAbiExport(
        Nid = "q2y-wDIVWZA",
        ExportName = "sceKernelSyncOnAddressWake",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int SyncOnAddressWake(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var requested = unchecked((int)ctx[CpuRegister.Rsi]);
        return Wake(ctx, address, requested, usePosixResult: false);
    }

    private static int Wake(
        CpuContext ctx,
        ulong address,
        int requested,
        bool usePosixResult)
    {
        if (address == 0 || (address & 3) != 0 || requested < 0)
        {
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                usePosixResult);
        }

        if (requested == 0)
        {
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                usePosixResult);
        }

        List<SyncWaiter>? selected = null;
        lock (_registryGate)
        {
            if (_waitersByAddress.TryGetValue(address, out var waiters))
            {
                selected = new List<SyncWaiter>(Math.Min(requested, waiters.Count));
                var node = waiters.First;
                while (node is not null && selected.Count < requested)
                {
                    var next = node.Next;
                    var waiter = node.Value;
                    waiters.Remove(node);
                    waiter.RegistryNode = null;
                    if (waiter.RequestWake())
                    {
                        selected.Add(waiter);
                    }

                    node = next;
                }

                if (waiters.Count == 0)
                {
                    _waitersByAddress.Remove(address);
                }
            }
        }

        if (selected is not null)
        {
            foreach (var waiter in selected)
            {
                _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(waiter.WakeKey, 1);
            }
        }

        var returnRip = 0UL;
        var outerCallerRip = 0UL;
        var parentCallerRip = 0UL;
        var ancestorCallerRip = 0UL;
        var outerCallerRip1 = 0UL;
        var outerCallerRip2 = 0UL;
        var outerCallerRip3 = 0UL;
        if (KernelSyncOnAddressProfile.DetailedEnabled)
        {
            CaptureCallStack(
                ctx,
                out returnRip,
                out outerCallerRip,
                out parentCallerRip,
                out ancestorCallerRip,
                out outerCallerRip1,
                out outerCallerRip2,
                out outerCallerRip3);
        }
        KernelSyncOnAddressProfile.RecordWake(
            address,
            requested,
            selected?.Count ?? 0,
            returnRip,
            outerCallerRip,
            parentCallerRip,
            ancestorCallerRip,
            outerCallerRip1,
            outerCallerRip2,
            outerCallerRip3,
            GuestThreadExecution.CurrentGuestThreadHandle,
            Environment.CurrentManagedThreadId);

        return CompleteResult(
            ctx,
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            usePosixResult);
    }

    private static void CaptureCallStack(
        CpuContext ctx,
        out ulong returnRip,
        out ulong callerRip,
        out ulong parentRip,
        out ulong ancestorRip,
        out ulong outerRip1,
        out ulong outerRip2,
        out ulong outerRip3)
    {
        returnRip = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var immediateReturnRip)
            ? immediateReturnRip
            : 0UL;
        var framePointer = ctx[CpuRegister.Rbp];
        callerRip = TryReadFrameReturnRip(ctx, framePointer, out var parentFramePointer);
        parentRip = TryReadFrameReturnRip(ctx, parentFramePointer, out var ancestorFramePointer);
        ancestorRip = TryReadFrameReturnRip(ctx, ancestorFramePointer, out var outerFramePointer1);
        outerRip1 = TryReadFrameReturnRip(ctx, outerFramePointer1, out var outerFramePointer2);
        outerRip2 = TryReadFrameReturnRip(ctx, outerFramePointer2, out var outerFramePointer3);
        outerRip3 = TryReadFrameReturnRip(ctx, outerFramePointer3, out _);
    }

    private static ulong TryReadFrameReturnRip(
        CpuContext ctx,
        ulong framePointer,
        out ulong parentFramePointer)
    {
        parentFramePointer = 0;
        if (framePointer == 0 || framePointer > ulong.MaxValue - sizeof(ulong))
        {
            return 0;
        }

        _ = ctx.TryReadUInt64(framePointer, out parentFramePointer);
        return ctx.TryReadUInt64(framePointer + sizeof(ulong), out var returnRip)
            ? returnRip
            : 0UL;
    }

    private static int Wait(CpuContext ctx, bool is64Bit)
    {
        var address = ctx[CpuRegister.Rdi];
        var expected = is64Bit
            ? ctx[CpuRegister.Rsi]
            : unchecked((uint)ctx[CpuRegister.Rsi]);
        var alignmentMask = is64Bit ? 7UL : 3UL;
        if (address == 0 || (address & alignmentMask) != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadValue(ctx, address, is64Bit, out var current))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (current != expected)
        {
            KernelSyncOnAddressProfile.RecordImmediate(address);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var timeoutAddress = ctx[CpuRegister.Rdx];
        TimeSpan? timeout = null;
        long deadline = 0;
        if (timeoutAddress != 0)
        {
            if (!ctx.TryReadUInt32(timeoutAddress, out var timeoutMicroseconds))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            timeout = TimeSpan.FromTicks((long)timeoutMicroseconds * 10L);
            deadline = GuestThreadExecution.ComputeDeadlineTimestamp(timeout.Value);
        }

        return WaitCore(
            ctx,
            is64Bit,
            address,
            expected,
            timeout,
            deadline,
            usePosixResult: false,
            reason: is64Bit
                ? "sceKernelSyncOnAddressWait64"
                : "sceKernelSyncOnAddressWait32");
    }

    private static int WaitCore(
        CpuContext ctx,
        bool is64Bit,
        ulong address,
        ulong expected,
        TimeSpan? timeout,
        long deadline,
        bool usePosixResult,
        string reason)
    {
        var alignmentMask = is64Bit ? 7UL : 3UL;
        if (address == 0 || (address & alignmentMask) != 0)
        {
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                usePosixResult);
        }

        if (!TryReadValue(ctx, address, is64Bit, out var current))
        {
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                usePosixResult);
        }

        if (current != expected)
        {
            KernelSyncOnAddressProfile.RecordImmediate(address);
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                usePosixResult);
        }

        if (timeout == TimeSpan.Zero)
        {
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
                usePosixResult);
        }

        var waiter = new SyncWaiter(
            ctx,
            address,
            expected,
            is64Bit,
            usePosixResult,
            deadline);
        Register(address, waiter);
        if (!TryReadValue(ctx, address, is64Bit, out current))
        {
            Unregister(waiter);
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                usePosixResult);
        }

        if (current != expected || waiter.TryWake())
        {
            Unregister(waiter);
            KernelSyncOnAddressProfile.RecordImmediate(address);
            return CompleteResult(
                ctx,
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                usePosixResult);
        }

        var returnRip = 0UL;
        var callerRip = 0UL;
        var parentRip = 0UL;
        if (KernelSyncOnAddressProfile.DetailedEnabled)
        {
            CaptureCallStack(
                ctx,
                out returnRip,
                out callerRip,
                out parentRip,
                out _,
                out _,
                out _,
                out _);
        }
        KernelSyncOnAddressProfile.RecordBlocked(
            address,
            returnRip,
            callerRip,
            parentRip,
            GuestThreadExecution.CurrentGuestThreadHandle,
            Environment.CurrentManagedThreadId);

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                reason,
                waiter.WakeKey,
                waiter,
                deadline))
        {
            // A wake can select this waiter before the scheduler parks it.
            // The scheduler checks TryWake after it registers the continuation.
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        return SetReturn(ctx, waiter.WaitOnHost());
    }

    private static TimeSpan CreateTimespecDuration(long seconds, long nanoseconds)
    {
        var subsecondTicks = (nanoseconds + 99L) / 100L;
        if (seconds > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerSecond)
        {
            return TimeSpan.MaxValue;
        }

        var secondTicks = seconds * TimeSpan.TicksPerSecond;
        if (secondTicks > TimeSpan.MaxValue.Ticks - subsecondTicks)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks(secondTicks + subsecondTicks);
    }

    private static void Register(ulong address, SyncWaiter waiter)
    {
        lock (_registryGate)
        {
            if (!_waitersByAddress.TryGetValue(address, out var waiters))
            {
                waiters = new LinkedList<SyncWaiter>();
                _waitersByAddress.Add(address, waiters);
            }

            waiter.RegistryNode = waiters.AddLast(waiter);
        }
    }

    private static void Unregister(SyncWaiter waiter)
    {
        lock (_registryGate)
        {
            var node = waiter.RegistryNode;
            if (node?.List is not { } waiters)
            {
                return;
            }

            waiters.Remove(node);
            waiter.RegistryNode = null;
            if (waiters.Count == 0)
            {
                _waitersByAddress.Remove(waiter.Address);
            }
        }
    }

    private static bool TryReadValue(
        CpuContext ctx,
        ulong address,
        bool is64Bit,
        out ulong value)
    {
        if (is64Bit)
        {
            return ctx.TryReadUInt64(address, out value);
        }

        if (ctx.TryReadUInt32(address, out var value32))
        {
            value = value32;
            return true;
        }

        value = 0;
        return false;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result) =>
        SetReturn(ctx, (int)result);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }

    private static int CompleteResult(CpuContext ctx, int result, bool usePosixResult) =>
        usePosixResult
            ? SetPosixReturn(ctx, result)
            : SetReturn(ctx, result);

    private static int SetPosixReturn(CpuContext ctx, int result)
    {
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return SetReturn(ctx, 0);
        }

        var errno = result switch
        {
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => PosixEperm,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED => PosixEacces,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT => PosixEfault,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT => PosixEinval,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN => PosixEagain,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT => PosixEtimedout,
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED => PosixEcanceled,
            _ => PosixEother,
        };
        _ = KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        return SetReturn(ctx, -1);
    }
}
