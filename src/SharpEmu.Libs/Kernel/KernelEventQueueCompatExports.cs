// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

public static class KernelEventQueueCompatExports
{
    private const int KernelEventSize = 0x20;
    public const short KernelEventFilterRead = -1;
    public const short KernelEventFilterGraphics = -14;
    public const short KernelEventFilterUser = -11;
    public const short KernelEventFilterAmpr = -16;
    public const short KernelEventFilterAmprSystem = -17;
    public const ushort KernelEventFlagClear = 0x20;
    public const ushort KernelEventFlagEof = 0x8000;
    private const int ReadEventPollPeriodMilliseconds = 10;

    private static readonly object _eventQueueGate = new();
    private static readonly Dictionary<ulong, EventQueueState> _eventQueues = new();
    private static readonly ConditionalWeakTable<object, EventQueueRuntimeIdentity>
        _eventQueueRuntimeIdentities = new();
    private static readonly Dictionary<ulong, KernelEventDeque> _pendingEvents = new();
    private static readonly Dictionary<ulong, Dictionary<(ulong Ident, short Filter), KernelEventRegistration>> _registeredEvents = new();
    private static long _nextEventQueueHandle = 1;
    private static long _nextEventQueueWaiterId;
    private static long _nextEventRegistrationGeneration;
    private static long _nextEventQueueGeneration;
    private static long _nextEventQueueRuntimeId;
    private static int _readEventRegistrationCount;
    private static int _readEventPollActive;
    private static readonly Timer _readEventPollTimer = new(
        PollReadEvents,
        null,
        Timeout.Infinite,
        Timeout.Infinite);

    private sealed record EventQueueRuntimeIdentity(ulong Id);

    private sealed class EventQueueState
    {
        public required ulong Handle { get; init; }
        public required ulong RuntimeId { get; init; }
        public required ulong Generation { get; init; }
        public required string WakeKey { get; init; }
        public bool Deleted { get; set; }
    }

    public readonly record struct KernelQueuedEvent(
        ulong Ident,
        short Filter,
        ushort Flags,
        uint Fflags,
        ulong Data,
        ulong UserData);

    private readonly record struct KernelEventRegistration(
        ulong Ident,
        short Filter,
        ulong UserData,
        ushort Flags,
        ulong LowWater,
        ulong Generation);

    private readonly record struct ReadEventPollTarget(
        ulong EqueueHandle,
        ulong EqueueGeneration,
        int FileDescriptor,
        ulong UserData,
        ulong LowWater,
        ulong RegistrationGeneration);

    internal readonly record struct KernelEventRegistrationToken(
        ulong EqueueHandle,
        ulong EqueueGeneration,
        ulong Ident,
        short Filter,
        ulong Generation);

    internal sealed record KernelEventRegistrationSnapshot(
        ulong RuntimeId,
        ulong Ident,
        short Filter,
        KernelEventRegistrationToken[] Targets);

    internal readonly record struct CapturedEventDeliveryResult(
        int TriggeredCount,
        int StaleCount);

    // Grow-only ring buffer standing in for LinkedList<KernelQueuedEvent>, which
    // allocated a node per enqueue — steady churn at one enqueue per vblank/flip edge
    // per registered queue. Mutated only under _eventQueueGate.
    private sealed class KernelEventDeque
    {
        private KernelQueuedEvent[] _items = new KernelQueuedEvent[4];
        private int _head;

        public int Count { get; private set; }

        public KernelQueuedEvent this[int index]
        {
            get => _items[(_head + index) % _items.Length];
            set => _items[(_head + index) % _items.Length] = value;
        }

        public void AddLast(in KernelQueuedEvent item)
        {
            if (Count == _items.Length)
            {
                var grown = new KernelQueuedEvent[_items.Length * 2];
                for (var i = 0; i < Count; i++)
                {
                    grown[i] = this[i];
                }

                _items = grown;
                _head = 0;
            }

            _items[(_head + Count) % _items.Length] = item;
            Count++;
        }

        public KernelQueuedEvent RemoveFirst()
        {
            var value = _items[_head];
            _head = (_head + 1) % _items.Length;
            Count--;
            return value;
        }

        public int FindIndex(ulong ident, short filter)
        {
            for (var i = 0; i < Count; i++)
            {
                var candidate = this[i];
                if (candidate.Ident == ident && candidate.Filter == filter)
                {
                    return i;
                }
            }

            return -1;
        }

        public bool Remove(ulong ident, short filter)
        {
            var index = FindIndex(ident, filter);
            if (index < 0)
            {
                return false;
            }

            for (var i = index; i + 1 < Count; i++)
            {
                this[i] = this[i + 1];
            }

            Count--;
            return true;
        }
    }

    private sealed class EqueueWaiter : IGuestThreadBlockWaiter
    {
        private enum WaitCompletion
        {
            Waiting,
            Reserved,
            TimedOut,
            Deleted,
        }

        private KernelQueuedEvent[]? _reservedEvents;
        private int _reservedCount;
        private WaitCompletion _completion;

        public required CpuContext Ctx { get; init; }
        public required EventQueueState State { get; init; }
        public required ulong EventsAddress { get; init; }
        public required int EventCapacity { get; init; }
        public required ulong OutCountAddress { get; init; }
        public required long WaiterId { get; init; }

        public int Resume()
        {
            KernelQueuedEvent[]? reservedEvents;
            int reservedCount;
            WaitCompletion completion;
            lock (this)
            {
                if (_completion == WaitCompletion.Waiting)
                {
                    _completion = WaitCompletion.TimedOut;
                }

                completion = _completion;
                reservedEvents = _reservedEvents;
                reservedCount = _reservedCount;
                _reservedEvents = null;
                _reservedCount = 0;
            }

            var result = completion switch
            {
                WaitCompletion.Reserved when reservedEvents is not null =>
                    DeliverReservedEvents(
                        Ctx,
                        reservedEvents,
                        reservedCount,
                        EventsAddress,
                        OutCountAddress),
                WaitCompletion.Deleted =>
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
                _ => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            };

            if (_logEqueue)
            {
                TraceEventQueue(
                    Ctx,
                    "wait-resume",
                    State.Handle,
                    $"generation={State.Generation} waiter={WaiterId} " +
                    $"capacity={EventCapacity} result=0x{unchecked((uint)result):X8}");
            }
            return result;
        }

        public bool TryWake()
        {
            lock (this)
            {
                if (_completion != WaitCompletion.Waiting)
                {
                    return true;
                }

                if (!TryReserveEvents(
                        State,
                        EventCapacity,
                        out var events,
                        out var count,
                        out var deleted))
                {
                    if (deleted)
                    {
                        _completion = WaitCompletion.Deleted;
                        return true;
                    }

                    return false;
                }

                _reservedEvents = events;
                _reservedCount = count;
                _completion = WaitCompletion.Reserved;
                return true;
            }
        }
    }

    [SysAbiExport(
        Nid = "D0OdFMjp46I",
        ExportName = "sceKernelCreateEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateEqueue(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var handle = unchecked((ulong)Interlocked.Increment(ref _nextEventQueueHandle));
        var generation = unchecked((ulong)Interlocked.Increment(
            ref _nextEventQueueGeneration));
        var state = new EventQueueState
        {
            Handle = handle,
            RuntimeId = GetEventQueueRuntimeId(ctx.Memory),
            Generation = generation,
            WakeKey = $"sceKernelWaitEqueue:{handle:X16}:{generation:X16}",
        };
        lock (_eventQueueGate)
        {
            _eventQueues.Add(handle, state);
            _pendingEvents[handle] = new KernelEventDeque();
            _registeredEvents[handle] = new Dictionary<(ulong Ident, short Filter), KernelEventRegistration>();
        }

        if (!ctx.TryWriteUInt64(outAddress, handle))
        {
            lock (_eventQueueGate)
            {
                state.Deleted = true;
                _eventQueues.Remove(handle);
                _pendingEvents.Remove(handle);
                _registeredEvents.Remove(handle);
                Monitor.PulseAll(_eventQueueGate);
            }
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceEventQueue(ctx, "create", handle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jpFjmgAC5AE",
        ExportName = "sceKernelDeleteEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteEqueue(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.Remove(handle, out state!) || state.Deleted)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            state.Deleted = true;
            _pendingEvents.Remove(handle);
            if (_registeredEvents.Remove(handle, out var registrations))
            {
                _readEventRegistrationCount -= registrations.Values.Count(
                    registration => registration.Filter == KernelEventFilterRead);
                UpdateReadEventPollTimerLocked();
            }
            Monitor.PulseAll(_eventQueueGate);
        }

        WakeEventQueue(
            state,
            _logEqueue ? $"source=delete generation={state.Generation}" : null);
        TraceEventQueue(
            ctx,
            "delete",
            handle,
            $"generation={state.Generation}");
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "VHCS3rCd0PM",
        ExportName = "sceKernelAddReadEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddReadEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var fileDescriptor = unchecked((int)ctx[CpuRegister.Rsi]);
        var lowWater = Math.Max(1UL, ctx[CpuRegister.Rdx]);
        var userData = ctx[CpuRegister.Rcx];

        if (!TryGetDescriptorReadiness(
                fileDescriptor,
                lowWater,
                out _,
                out _,
                out _))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var registered = RegisterEvent(
            handle,
            unchecked((uint)fileDescriptor),
            KernelEventFilterRead,
            userData,
            flags: 0,
            lowWater);
        if (registered)
        {
            RefreshReadEvents(handle);
        }

        TraceEventQueue(
            ctx,
            "add_read",
            handle,
            $"fd={fileDescriptor} low_water={lowWater} user_data=0x{userData:X16}");
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "JxJ4tfgKlXA",
        ExportName = "sceKernelDeleteReadEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteReadEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var fileDescriptor = unchecked((int)ctx[CpuRegister.Rsi]);
        var deleted = DeleteRegisteredEvent(
            handle,
            unchecked((uint)fileDescriptor),
            KernelEventFilterRead);
        TraceEventQueue(ctx, "delete_read", handle, $"fd={fileDescriptor}");
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "WDszmSbWuDk",
        ExportName = "sceKernelAddUserEventEdge",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddUserEventEdge(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser,
            0,
            KernelEventFlagClear);
        TraceEventQueue(ctx, "add_user_edge", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "4R6-OvI2cEA",
        ExportName = "sceKernelAddUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddUserEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser,
            0,
            flags: 0);
        TraceEventQueue(ctx, "add_user", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "LJDwdSNTnDg",
        ExportName = "sceKernelDeleteUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteUserEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var deleted = DeleteRegisteredEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser);
        TraceEventQueue(ctx, "delete_user", handle);
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "F6e0kwo4cnk",
        ExportName = "sceKernelTriggerUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelTriggerUserEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var triggered = TriggerRegisteredEvent(
            handle,
            ctx[CpuRegister.Rsi],
            KernelEventFilterUser,
            userData: ctx[CpuRegister.Rdx]);
        TraceEventQueue(ctx, "trigger_user", handle);
        return triggered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "bBfz7kMF2Ho",
        ExportName = "sceKernelAddAmprEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddAmprEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmpr,
            ctx[CpuRegister.Rdx]);
        TraceEventQueue(ctx, "add_ampr", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "vuae5JPNt9A",
        ExportName = "sceKernelAddAmprSystemEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddAmprSystemEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var registered = RegisterEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmprSystem,
            ctx[CpuRegister.Rdx]);
        TraceEventQueue(ctx, "add_ampr_system", handle);
        return registered
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "bMmid3pfyjo",
        ExportName = "sceKernelDeleteAmprEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteAmprEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var deleted = DeleteRegisteredEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmpr);
        TraceEventQueue(ctx, "delete_ampr", handle);
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "Ij+ryuEClXQ",
        ExportName = "sceKernelDeleteAmprSystemEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteAmprSystemEvent(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var deleted = DeleteRegisteredEvent(
            handle,
            unchecked((uint)ctx[CpuRegister.Rsi]),
            KernelEventFilterAmprSystem);
        TraceEventQueue(ctx, "delete_ampr_system", handle);
        return deleted
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
    }

    [SysAbiExport(
        Nid = "QyrxcdBrb0M",
        ExportName = "sceKernelGetKqueueFromEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetKqueueFromEqueue(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = ctx[CpuRegister.Rdi];
        TraceEventQueue(ctx, "get_kqueue", ctx[CpuRegister.Rdi]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vz+pg2zdopI",
        ExportName = "sceKernelGetEventUserData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventUserData(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rdi] + 0x18, out var userData);
        ctx[CpuRegister.Rax] = userData;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mJ7aghmgvfc",
        ExportName = "sceKernelGetEventId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventId(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rdi], out var ident);
        ctx[CpuRegister.Rax] = ident;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "23CPPI1tyBY",
        ExportName = "sceKernelGetEventFilter",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventFilter(CpuContext ctx)
    {
        Span<byte> filterBytes = stackalloc byte[sizeof(short)];
        var filter = ctx.Memory.TryRead(ctx[CpuRegister.Rdi] + 0x08, filterBytes)
            ? BinaryPrimitives.ReadInt16LittleEndian(filterBytes)
            : (short)0;
        ctx[CpuRegister.Rax] = unchecked((uint)filter);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kwGyyjohI50",
        ExportName = "sceKernelGetEventData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetEventData(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rdi] + 0x10, out var data);
        ctx[CpuRegister.Rax] = data;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fzyMKs9kim0",
        ExportName = "sceKernelWaitEqueue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitEqueue(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var eventsAddress = ctx[CpuRegister.Rsi];
        var eventCapacity = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var outCountAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];

        if (!TryGetLiveEventQueue(handle, out var state))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (eventsAddress == 0 || eventCapacity < 1)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !TryReadUInt32(ctx, timeoutAddress, out timeoutUsec))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        RefreshReadEvents(handle);
        var deliveredCount = DequeueEvents(
            ctx,
            state,
            eventsAddress,
            eventCapacity);
        if (outCountAddress != 0 && !TryWriteUInt32(ctx, outCountAddress, (uint)deliveredCount))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (deliveredCount > 0)
        {
            if (_logEqueue)
            {
                TraceEventQueue(
                    ctx,
                    "wait-deliver",
                    handle,
                    $"delivered={deliveredCount} capacity={eventCapacity}");
            }
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        lock (_eventQueueGate)
        {
            if (!IsLiveEventQueueLocked(state))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
            }
        }

        if (IsSynchronousPoll(timeoutAddress, timeoutUsec))
        {
            if (_logEqueue)
            {
                TraceEventQueue(
                    ctx,
                    "wait-poll-timeout",
                    handle,
                    $"capacity={eventCapacity}");
            }
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        var waiterId = Interlocked.Increment(ref _nextEventQueueWaiterId);
        if (timeoutAddress == 0)
        {
            var requestedBlock = GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelWaitEqueue",
                state.WakeKey,
                new EqueueWaiter
                {
                    Ctx = ctx,
                    State = state,
                    EventsAddress = eventsAddress,
                    EventCapacity = eventCapacity,
                    OutCountAddress = outCountAddress,
                    WaiterId = waiterId,
                });
            if (requestedBlock)
            {
                var wakeAfterRegistration = false;
                lock (_eventQueueGate)
                {
                    wakeAfterRegistration =
                        !IsLiveEventQueueLocked(state) ||
                        HasPendingEventsLocked(state.Handle);
                }

                if (wakeAfterRegistration)
                {
                    WakeEventQueue(
                        state,
                        _logEqueue
                            ? "source=post-registration-state-check"
                            : null);
                }

                if (_logEqueue)
                {
                    TraceEventQueue(
                        ctx,
                        "wait-block",
                        handle,
                        $"generation={state.Generation} waiter={waiterId} " +
                        $"capacity={eventCapacity} timeout=infinite " +
                        $"events=0x{eventsAddress:X16} out_count=0x{outCountAddress:X16}");
                }
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }

        if (timeoutAddress != 0)
        {
            var deadline = Environment.TickCount64 +
                Math.Max(
                    1L,
                    (long)Math.Min(
                        ((ulong)timeoutUsec + 999UL) / 1000UL,
                        int.MaxValue));
            lock (_eventQueueGate)
            {
                while (IsLiveEventQueueLocked(state) &&
                       !HasPendingEventsLocked(handle))
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    Monitor.Wait(_eventQueueGate, (int)Math.Min(remaining, 100));
                }

                if (!IsLiveEventQueueLocked(state))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
                }
            }

            deliveredCount = DequeueEvents(
                ctx,
                state,
                eventsAddress,
                eventCapacity);
            if (outCountAddress != 0 && !TryWriteUInt32(ctx, outCountAddress, (uint)deliveredCount))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (deliveredCount > 0)
            {
                if (_logEqueue)
                {
                    TraceEventQueue(
                        ctx,
                        "wait-timed-deliver",
                        handle,
                        $"waiter={waiterId} delivered={deliveredCount} capacity={eventCapacity} " +
                        $"timeout_usec={timeoutUsec}");
                }
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            if (_logEqueue)
            {
                TraceEventQueue(
                    ctx,
                    "wait-timeout",
                    handle,
                    $"waiter={waiterId} capacity={eventCapacity} timeout_usec={timeoutUsec}");
            }
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        if (_logEqueue)
        {
            TraceEventQueue(
                ctx,
                "wait",
                handle,
                $"waiter={waiterId} capacity={eventCapacity} timeout_usec={timeoutUsec}");
        }
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    public static bool IsValidEqueue(ulong handle)
    {
        return TryGetLiveEventQueue(handle, out _);
    }

    private static bool TryGetLiveEventQueue(
        ulong handle,
        out EventQueueState state)
    {
        lock (_eventQueueGate)
        {
            return _eventQueues.TryGetValue(handle, out state!) &&
                !state.Deleted;
        }
    }

    private static bool IsLiveEventQueueLocked(EventQueueState state) =>
        !state.Deleted &&
        _eventQueues.TryGetValue(state.Handle, out var current) &&
        ReferenceEquals(current, state);

    private static ulong GetEventQueueRuntimeId(ICpuMemory memory)
    {
        object key = memory;
        while (key is ICpuMemoryWrapper wrapper &&
               !ReferenceEquals(wrapper.Inner, key))
        {
            key = wrapper.Inner;
        }

        return _eventQueueRuntimeIdentities.GetValue(
            key,
            static _ => new EventQueueRuntimeIdentity(
                unchecked((ulong)Interlocked.Increment(
                    ref _nextEventQueueRuntimeId)))).Id;
    }

    internal static bool IsSynchronousPoll(
        ulong timeoutAddress,
        uint timeoutUsec) =>
        timeoutAddress != 0 && timeoutUsec == 0;

    private static bool HasPendingEventsLocked(ulong handle) =>
        _pendingEvents.TryGetValue(handle, out var events) &&
        events.Count != 0;

    public static bool EnqueueEvent(ulong handle, KernelQueuedEvent queuedEvent)
    {
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out state!) ||
                state.Deleted)
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var queue))
            {
                queue = new KernelEventDeque();
                _pendingEvents[handle] = queue;
            }

            queue.AddLast(queuedEvent);
            Monitor.PulseAll(_eventQueueGate);
        }

        WakeEventQueue(
            state,
            _logEqueue
                ? $"source=enqueue ident=0x{queuedEvent.Ident:X16} " +
                  $"filter={queuedEvent.Filter} data=0x{queuedEvent.Data:X16}"
                : null);

        return true;
    }

    public static bool RegisterEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong userData,
        ushort flags = KernelEventFlagClear,
        ulong lowWater = 0)
    {
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out var state) ||
                state.Deleted)
            {
                return false;
            }

            if (!_registeredEvents.TryGetValue(handle, out var events))
            {
                events = new Dictionary<(ulong Ident, short Filter), KernelEventRegistration>();
                _registeredEvents[handle] = events;
            }

            var key = (ident, filter);
            var isNewReadRegistration =
                filter == KernelEventFilterRead &&
                !events.ContainsKey(key);
            events[(ident, filter)] = new KernelEventRegistration(
                ident,
                filter,
                userData,
                flags,
                lowWater,
                unchecked((ulong)Interlocked.Increment(
                    ref _nextEventRegistrationGeneration)));
            if (isNewReadRegistration)
            {
                _readEventRegistrationCount++;
                UpdateReadEventPollTimerLocked();
            }
            return true;
        }
    }

    /// <summary>
    /// Captures the exact lifetime of every matching registration owned by one
    /// guest runtime. A later delete/re-add of the same tuple receives a new
    /// generation and therefore cannot consume an interrupt that was already
    /// bound to this snapshot.
    /// </summary>
    internal static KernelEventRegistrationSnapshot CaptureRegisteredEvents(
        ICpuMemory memory,
        ulong ident,
        short filter)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var runtimeId = GetEventQueueRuntimeId(memory);
        List<KernelEventRegistrationToken>? targets = null;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted ||
                    state.RuntimeId != runtimeId ||
                    !registrations.TryGetValue((ident, filter), out var registration))
                {
                    continue;
                }

                (targets ??= []).Add(new KernelEventRegistrationToken(
                    handle,
                    state.Generation,
                    registration.Ident,
                    registration.Filter,
                    registration.Generation));
            }
        }

        return new KernelEventRegistrationSnapshot(
            runtimeId,
            ident,
            filter,
            targets?.ToArray() ?? []);
    }

    /// <summary>
    /// Delivers a previously captured interrupt only to registrations whose
    /// runtime, queue handle, and generation are still live.
    /// </summary>
    internal static CapturedEventDeliveryResult TriggerCapturedEvents(
        KernelEventRegistrationSnapshot snapshot,
        ulong data)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<EventQueueState>? wakeQueues = null;
        var triggeredCount = 0;
        var staleCount = 0;
        lock (_eventQueueGate)
        {
            foreach (var target in snapshot.Targets)
            {
                if (!_eventQueues.TryGetValue(
                        target.EqueueHandle,
                        out var state) ||
                    state.Deleted ||
                    state.Generation != target.EqueueGeneration ||
                    state.RuntimeId != snapshot.RuntimeId ||
                    !_registeredEvents.TryGetValue(
                        target.EqueueHandle,
                        out var registrations) ||
                    !registrations.TryGetValue(
                        (target.Ident, target.Filter),
                        out var registration) ||
                    registration.Generation != target.Generation)
                {
                    staleCount++;
                    continue;
                }

                if (!_pendingEvents.TryGetValue(
                        target.EqueueHandle,
                        out var queue))
                {
                    queue = new KernelEventDeque();
                    _pendingEvents[target.EqueueHandle] = queue;
                }

                QueueOrUpdateEvent(
                    queue,
                    new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        registration.Flags,
                        1,
                        data,
                        registration.UserData));
                (wakeQueues ??= []).Add(state);
                triggeredCount++;
            }
        }

        if (wakeQueues is not null)
        {
            foreach (var state in wakeQueues)
            {
                WakeEventQueue(
                    state,
                    _logEqueue
                        ? $"source=trigger-captured ident=0x{snapshot.Ident:X16} " +
                          $"filter={snapshot.Filter} data=0x{data:X16}"
                        : null);
            }
        }

        return new CapturedEventDeliveryResult(triggeredCount, staleCount);
    }

    public static bool DeleteRegisteredEvent(
        ulong handle,
        ulong ident,
        short filter)
    {
        lock (_eventQueueGate)
        {
            if (!_registeredEvents.TryGetValue(handle, out var events) ||
                !events.Remove((ident, filter), out var registration))
            {
                return false;
            }

            if (registration.Filter == KernelEventFilterRead)
            {
                _readEventRegistrationCount--;
                UpdateReadEventPollTimerLocked();
            }

            if (_pendingEvents.TryGetValue(handle, out var pending))
            {
                _ = pending.Remove(ident, filter);
            }

            return true;
        }
    }

    public static int TriggerRegisteredEvents(
        ulong ident,
        short filter,
        ulong data)
    {
        List<EventQueueState>? wakeQueues = null;
        var triggeredCount = 0;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted ||
                    !registrations.TryGetValue((ident, filter), out var registration))
                {
                    continue;
                }

                if (!_pendingEvents.TryGetValue(handle, out var queue))
                {
                    queue = new KernelEventDeque();
                    _pendingEvents[handle] = queue;
                }

                QueueOrUpdateEvent(
                    queue,
                    new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        registration.Flags,
                        1,
                        data,
                        registration.UserData));
                (wakeQueues ??= []).Add(state);
                triggeredCount++;
            }
        }

        if (wakeQueues is not null)
        {
            foreach (var state in wakeQueues)
            {
                WakeEventQueue(
                    state,
                    _logEqueue
                        ? $"source=trigger ident=0x{ident:X16} filter={filter} data=0x{data:X16}"
                        : null);
            }
        }

        return triggeredCount;
    }

    /// <summary>
    /// Triggers every registered event on every queue that matches <paramref name="filter"/>
    /// regardless of the registration's <c>ident</c>. This is a workaround for PS5 AGC command
    /// buffers, where <c>IT_EVENT_WRITE</c> carries a hardware <c>EVENT_TYPE</c> that does not
    /// match the <c>eventId</c> the guest registered with <c>sceAgcDriverAddEqEvent</c>.
    /// See issue #173.
    /// </summary>
    public static int TriggerRegisteredEventsByFilter(
        short filter,
        ulong data)
    {
        List<EventQueueState>? wakeQueues = null;
        var triggeredCount = 0;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted)
                {
                    continue;
                }

                foreach (var registration in registrations.Values)
                {
                    if (registration.Filter != filter)
                    {
                        continue;
                    }

                    if (!_pendingEvents.TryGetValue(handle, out var queue))
                    {
                        queue = new KernelEventDeque();
                        _pendingEvents[handle] = queue;
                    }

                    // GPU interrupt events must not coalesce: the AGC driver's
                    // interrupt thread accounts exactly one completion per
                    // delivered kevent (it never reads the kevent payload), so
                    // merging N triggers into one pending entry silently drops
                    // N-1 completions and wedges its dependency counters. Queue
                    // a distinct entry per trigger, with a defensive cap so an
                    // undrained queue cannot grow without bound.
                    var queuedEvent = new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        registration.Flags,
                        1,
                        data,
                        registration.UserData);
                    if (CountPendingEvents(queue, registration.Ident, registration.Filter) < 256)
                    {
                        queue.AddLast(queuedEvent);
                    }
                    else
                    {
                        QueueOrUpdateEvent(queue, queuedEvent);
                    }

                    (wakeQueues ??= []).Add(state);
                    triggeredCount++;

                    // A single queue only needs to be woken once, even if multiple
                    // registrations matched.
                    break;
                }
            }
        }

        if (wakeQueues is not null)
        {
            foreach (var state in wakeQueues)
            {
                WakeEventQueue(
                    state,
                    _logEqueue
                        ? $"source=trigger-filter filter={filter} data=0x{data:X16}"
                        : null);
            }
        }

        return triggeredCount;
    }

    /// <summary>
    /// Queues one event for every registration using <paramref name="filter"/>.
    /// Unlike <see cref="TriggerRegisteredEvents"/>, this preserves distinct
    /// event identifiers registered on the same queue. AGC driver completion
    /// queues use this form because the driver, rather than a packet-provided
    /// identifier, announces that the whole submission reached end-of-pipe.
    /// </summary>
    public static int TriggerRegisteredEventsDistinct(short filter)
    {
        HashSet<EventQueueState>? wakeQueues = null;
        var triggeredCount = 0;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!_eventQueues.TryGetValue(handle, out var state) ||
                    state.Deleted)
                {
                    continue;
                }

                foreach (var registration in registrations.Values)
                {
                    if (registration.Filter != filter)
                    {
                        continue;
                    }

                    if (!_pendingEvents.TryGetValue(handle, out var queue))
                    {
                        queue = new KernelEventDeque();
                        _pendingEvents[handle] = queue;
                    }

                    QueueOrUpdateEvent(
                        queue,
                        new KernelQueuedEvent(
                            registration.Ident,
                            registration.Filter,
                            registration.Flags,
                            1,
                            registration.Ident,
                            registration.UserData));
                    (wakeQueues ??= []).Add(state);
                    triggeredCount++;
                }
            }
        }

        if (wakeQueues is not null)
        {
            foreach (var state in wakeQueues)
            {
                WakeEventQueue(
                    state,
                    _logEqueue ? $"source=trigger-distinct filter={filter}" : null);
            }
        }

        return triggeredCount;
    }

    private static bool TriggerRegisteredEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong userData)
    {
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out state!) ||
                state.Deleted ||
                !_registeredEvents.TryGetValue(handle, out var registrations) ||
                !registrations.TryGetValue((ident, filter), out var registration))
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var queue))
            {
                queue = new KernelEventDeque();
                _pendingEvents[handle] = queue;
            }

            QueueOrUpdateEvent(
                queue,
                new KernelQueuedEvent(
                    registration.Ident,
                    registration.Filter,
                    registration.Flags,
                    0,
                    0,
                    userData));
        }

        WakeEventQueue(
            state,
            _logEqueue
                ? $"source=trigger-one ident=0x{ident:X16} filter={filter} " +
                  $"user_data=0x{userData:X16}"
                : null);
        return true;
    }

    public static bool TriggerDisplayEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong eventHint,
        ulong userData)
    {
        EventQueueState state;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.TryGetValue(handle, out state!) ||
                state.Deleted)
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var events))
            {
                events = new KernelEventDeque();
                _pendingEvents[handle] = events;
            }

            var count = 1UL;
            var pendingIndex = events.FindIndex(ident, filter);
            if (pendingIndex >= 0)
            {
                count = Math.Min(((events[pendingIndex].Data >> 12) & 0xFUL) + 1, 0xFUL);
            }

            var timeBits = unchecked((ulong)Environment.TickCount64) & 0xFFFUL;
            var eventData = timeBits | (count << 12) | (eventHint & 0xFFFF_FFFF_FFFF_0000UL);
            var triggeredEvent = new KernelQueuedEvent(
                ident,
                filter,
                0x20,
                0,
                eventData,
                userData);

            if (pendingIndex >= 0)
            {
                events[pendingIndex] = triggeredEvent;
            }
            else
            {
                events.AddLast(triggeredEvent);
            }
        }

        WakeEventQueue(
            state,
            _logEqueue
                ? $"source=display ident=0x{ident:X16} filter={filter} hint=0x{eventHint:X16}"
                : null);

        return true;
    }

    private static int DeliverReservedEvents(
        CpuContext ctx,
        KernelQueuedEvent[] events,
        int count,
        ulong eventsAddress,
        ulong outCountAddress)
    {
        var deliveredCount = 0;
        try
        {
            for (; deliveredCount < count; deliveredCount++)
            {
                if (!WriteKernelEvent(
                        ctx,
                        eventsAddress + ((ulong)deliveredCount * KernelEventSize),
                        events[deliveredCount]))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }
            }

            if (outCountAddress != 0 &&
                !TryWriteUInt32(ctx, outCountAddress, (uint)deliveredCount))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            return deliveredCount > 0
                ? (int)OrbisGen2Result.ORBIS_GEN2_OK
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }
        finally
        {
            ArrayPool<KernelQueuedEvent>.Shared.Return(events);
        }
    }

    private static int CountPendingEvents(
        KernelEventDeque queue,
        ulong ident,
        short filter)
    {
        var count = 0;
        for (var i = 0; i < queue.Count; i++)
        {
            var pending = queue[i];
            if (pending.Ident == ident && pending.Filter == filter)
            {
                count++;
            }
        }

        return count;
    }

    private static void PollReadEvents(object? state)
    {
        _ = state;
        if (Interlocked.Exchange(ref _readEventPollActive, 1) != 0)
        {
            return;
        }

        try
        {
            RefreshReadEvents(handle: null);
        }
        finally
        {
            Volatile.Write(ref _readEventPollActive, 0);
        }
    }

    private static void RefreshReadEvents(ulong? handle)
    {
        List<ReadEventPollTarget>? targets = null;
        lock (_eventQueueGate)
        {
            if (_readEventRegistrationCount == 0)
            {
                return;
            }

            foreach (var (equeueHandle, registrations) in _registeredEvents)
            {
                if (handle.HasValue && equeueHandle != handle.Value)
                {
                    continue;
                }

                if (!_eventQueues.TryGetValue(equeueHandle, out var state) ||
                    state.Deleted)
                {
                    continue;
                }

                foreach (var registration in registrations.Values)
                {
                    if (registration.Filter != KernelEventFilterRead)
                    {
                        continue;
                    }

                    (targets ??= []).Add(new ReadEventPollTarget(
                        equeueHandle,
                        state.Generation,
                        unchecked((int)registration.Ident),
                        registration.UserData,
                        Math.Max(1UL, registration.LowWater),
                        registration.Generation));
                }
            }
        }

        if (targets is null)
        {
            return;
        }

        foreach (var target in targets)
        {
            var exists = TryGetDescriptorReadiness(
                target.FileDescriptor,
                target.LowWater,
                out var ready,
                out var availableBytes,
                out var eventFlags);
            EventQueueState? wakeState = null;
            lock (_eventQueueGate)
            {
                if (!_eventQueues.TryGetValue(target.EqueueHandle, out var state) ||
                    state.Deleted ||
                    state.Generation != target.EqueueGeneration ||
                    !_registeredEvents.TryGetValue(
                        target.EqueueHandle,
                        out var registrations) ||
                    !registrations.TryGetValue(
                        (unchecked((uint)target.FileDescriptor), KernelEventFilterRead),
                        out var registration) ||
                    registration.Generation != target.RegistrationGeneration)
                {
                    continue;
                }

                if (!exists)
                {
                    registrations.Remove((registration.Ident, registration.Filter));
                    _readEventRegistrationCount--;
                    if (_pendingEvents.TryGetValue(target.EqueueHandle, out var staleQueue))
                    {
                        _ = staleQueue.Remove(registration.Ident, registration.Filter);
                    }
                    UpdateReadEventPollTimerLocked();
                    continue;
                }

                if (!ready)
                {
                    if (_pendingEvents.TryGetValue(target.EqueueHandle, out var inactiveQueue))
                    {
                        _ = inactiveQueue.Remove(registration.Ident, registration.Filter);
                    }
                    continue;
                }

                if (!_pendingEvents.TryGetValue(target.EqueueHandle, out var queue))
                {
                    queue = new KernelEventDeque();
                    _pendingEvents[target.EqueueHandle] = queue;
                }

                QueueOrUpdateEvent(
                    queue,
                    new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        eventFlags,
                        0,
                        availableBytes,
                        registration.UserData));
                Monitor.PulseAll(_eventQueueGate);
                wakeState = state;
            }

            if (wakeState is not null)
            {
                WakeEventQueue(
                    wakeState,
                    _logEqueue
                        ? $"source=read-ready fd={target.FileDescriptor} " +
                          $"available={availableBytes} flags=0x{eventFlags:X4}"
                        : null);
            }
        }
    }

    private static bool TryGetDescriptorReadiness(
        int fileDescriptor,
        ulong lowWater,
        out bool ready,
        out ulong availableBytes,
        out ushort eventFlags)
    {
        if (KernelSocketCompatExports.TryGetReadEventState(
                fileDescriptor,
                lowWater,
                out ready,
                out availableBytes,
                out eventFlags) ||
            KernelMemoryCompatExports.TryGetFileReadEventState(
                fileDescriptor,
                lowWater,
                out ready,
                out availableBytes,
                out eventFlags) ||
            NetExports.TryGetReadEventState(
                fileDescriptor,
                lowWater,
                out ready,
                out availableBytes,
                out eventFlags))
        {
            return true;
        }

        ready = false;
        availableBytes = 0;
        eventFlags = 0;
        return false;
    }

    private static void UpdateReadEventPollTimerLocked()
    {
        var period = _readEventRegistrationCount > 0
            ? ReadEventPollPeriodMilliseconds
            : Timeout.Infinite;
        _readEventPollTimer.Change(period, period);
    }

    private static void QueueOrUpdateEvent(
        KernelEventDeque queue,
        KernelQueuedEvent queuedEvent)
    {
        var pendingIndex = queue.FindIndex(queuedEvent.Ident, queuedEvent.Filter);
        if (pendingIndex < 0)
        {
            queue.AddLast(queuedEvent);
            return;
        }

        queue[pendingIndex] = queuedEvent.Filter is KernelEventFilterUser or KernelEventFilterRead
            ? queuedEvent
            : queuedEvent with
        {
            Fflags = Math.Max(queue[pendingIndex].Fflags + 1, queuedEvent.Fflags),
        };
    }

    private static void WakeEventQueue(
        EventQueueState state,
        string? detail = null)
    {
        if (_logEqueue)
        {
            TraceEventQueueHost(
                "wake",
                state.Handle,
                $"generation={state.Generation}" +
                (detail is null ? string.Empty : $" {detail}"));
        }
        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(state.WakeKey);
    }

    private static int DequeueEvents(
        CpuContext ctx,
        EventQueueState state,
        ulong eventsAddress,
        int eventCapacity)
    {
        if (eventsAddress == 0 || eventCapacity <= 0)
        {
            return 0;
        }

        if (!TryReserveEvents(
                state,
                eventCapacity,
                out var events,
                out var count,
                out _))
        {
            return 0;
        }

        var deliveredCount = 0;
        try
        {
            for (; deliveredCount < count; deliveredCount++)
            {
                if (!WriteKernelEvent(
                        ctx,
                        eventsAddress + ((ulong)deliveredCount * KernelEventSize),
                        events[deliveredCount]))
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<KernelQueuedEvent>.Shared.Return(events);
        }

        return deliveredCount;
    }

    private static bool TryReserveEvents(
        EventQueueState state,
        int eventCapacity,
        out KernelQueuedEvent[] events,
        out int count,
        out bool deleted)
    {
        events = null!;
        count = 0;
        deleted = false;
        lock (_eventQueueGate)
        {
            if (!IsLiveEventQueueLocked(state))
            {
                deleted = true;
                return false;
            }

            if (!_pendingEvents.TryGetValue(state.Handle, out var queue) ||
                queue.Count == 0)
            {
                return false;
            }

            count = Math.Min(eventCapacity, queue.Count);
            events = ArrayPool<KernelQueuedEvent>.Shared.Rent(count);
            for (var i = 0; i < count; i++)
            {
                events[i] = queue.RemoveFirst();
            }

            // Level-triggered events remain ready until their registration is
            // deleted or their source clears. EV_CLEAR events model edges and
            // are consumed by this delivery.
            for (var i = 0; i < count; i++)
            {
                if ((events[i].Flags & KernelEventFlagClear) == 0)
                {
                    queue.AddLast(events[i]);
                }
            }
        }

        return true;
    }

    internal static int ReservePendingEventCountForTest(
        ulong handle,
        int eventCapacity)
    {
        if (!TryGetLiveEventQueue(handle, out var state) ||
            !TryReserveEvents(
                state,
                eventCapacity,
                out var events,
                out var count,
                out _))
        {
            return 0;
        }

        try
        {
            return count;
        }
        finally
        {
            ArrayPool<KernelQueuedEvent>.Shared.Return(events);
        }
    }

    internal static bool TryReservePendingEventForTest(
        ulong handle,
        out KernelQueuedEvent queuedEvent)
    {
        queuedEvent = default;
        if (!TryGetLiveEventQueue(handle, out var state) ||
            !TryReserveEvents(
                state,
                1,
                out var events,
                out var count,
                out _))
        {
            return false;
        }

        try
        {
            queuedEvent = events[0];
            return count == 1;
        }
        finally
        {
            ArrayPool<KernelQueuedEvent>.Shared.Return(events);
        }
    }

    private static bool WriteKernelEvent(CpuContext ctx, ulong address, KernelQueuedEvent queuedEvent)
    {
        Span<byte> eventBytes = stackalloc byte[KernelEventSize];
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes[0x00..], queuedEvent.Ident);
        BinaryPrimitives.WriteInt16LittleEndian(eventBytes[0x08..], queuedEvent.Filter);
        BinaryPrimitives.WriteUInt16LittleEndian(eventBytes[0x0A..], queuedEvent.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(eventBytes[0x0C..], queuedEvent.Fflags);
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes[0x10..], queuedEvent.Data);
        BinaryPrimitives.WriteUInt64LittleEndian(eventBytes[0x18..], queuedEvent.UserData);
        return ctx.Memory.TryWrite(address, eventBytes);
    }

    private static readonly bool _logEqueue =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_EQUEUE"), "1", StringComparison.Ordinal);

    private static void TraceEventQueue(
        CpuContext ctx,
        string operation,
        ulong handle,
        string? detail = null)
    {
        if (!_logEqueue)
        {
            return;
        }

        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        Console.Error.WriteLine(
            $"[LOADER][TRACE] equeue.{operation}: handle=0x{handle:X16} " +
            $"depth={GetPendingEventCount(handle)} registrations={GetRegistrationCount(handle)}" +
            $"{suffix} {KernelSyncTraceFormatter.FormatContext(ctx)}");
    }

    private static void TraceEventQueueHost(
        string operation,
        ulong handle,
        string? detail = null)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";
        Console.Error.WriteLine(
            $"[LOADER][TRACE] equeue.{operation}: handle=0x{handle:X16} " +
            $"depth={GetPendingEventCount(handle)} registrations={GetRegistrationCount(handle)} " +
            $"{KernelSyncTraceFormatter.FormatCurrentThread()}{suffix}");
    }

    private static int GetPendingEventCount(ulong handle)
    {
        lock (_eventQueueGate)
        {
            return _pendingEvents.TryGetValue(handle, out var events) ? events.Count : 0;
        }
    }

    private static int GetRegistrationCount(ulong handle)
    {
        lock (_eventQueueGate)
        {
            return _registeredEvents.TryGetValue(handle, out var events) ? events.Count : 0;
        }
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
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
}
