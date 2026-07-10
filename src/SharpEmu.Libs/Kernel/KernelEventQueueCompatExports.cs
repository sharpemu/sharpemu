// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

public static class KernelEventQueueCompatExports
{
    private const int KernelEventSize = 0x20;
    public const short KernelEventFilterGraphics = -14;
    public const short KernelEventFilterAmpr = -16;
    public const short KernelEventFilterAmprSystem = -17;

    private static readonly object _eventQueueGate = new();
    private static readonly HashSet<ulong> _eventQueues = new();
    private static readonly Dictionary<ulong, LinkedList<KernelQueuedEvent>> _pendingEvents = new();
    private static readonly Dictionary<ulong, Dictionary<(ulong Ident, short Filter), KernelEventRegistration>> _registeredEvents = new();
    private static long _nextEventQueueHandle = 1;

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
        ulong UserData);

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
        lock (_eventQueueGate)
        {
            _eventQueues.Add(handle);
            _pendingEvents[handle] = new LinkedList<KernelQueuedEvent>();
            _registeredEvents[handle] = new Dictionary<(ulong Ident, short Filter), KernelEventRegistration>();
        }

        if (!ctx.TryWriteUInt64(outAddress, handle))
        {
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
        lock (_eventQueueGate)
        {
            _eventQueues.Remove(handle);
            _pendingEvents.Remove(handle);
            _registeredEvents.Remove(handle);
        }

        TraceEventQueue(ctx, "delete", handle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "WDszmSbWuDk",
        ExportName = "sceKernelAddUserEventEdge",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddUserEventEdge(CpuContext ctx)
    {
        TraceEventQueue(ctx, "add_user_edge", ctx[CpuRegister.Rdi]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4R6-OvI2cEA",
        ExportName = "sceKernelAddUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelAddUserEvent(CpuContext ctx)
    {
        TraceEventQueue(ctx, "add_user", ctx[CpuRegister.Rdi]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LJDwdSNTnDg",
        ExportName = "sceKernelDeleteUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteUserEvent(CpuContext ctx)
    {
        TraceEventQueue(ctx, "delete_user", ctx[CpuRegister.Rdi]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "F6e0kwo4cnk",
        ExportName = "sceKernelTriggerUserEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelTriggerUserEvent(CpuContext ctx)
    {
        TraceEventQueue(ctx, "trigger_user", ctx[CpuRegister.Rdi]);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
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

        if (!IsValidEqueue(handle))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (eventsAddress == 0 || eventCapacity < 1)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !ctx.TryReadUInt32(timeoutAddress, out timeoutUsec))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var deliveredCount = DequeueEvents(ctx, handle, eventsAddress, eventCapacity);
        if (outCountAddress != 0 && !ctx.TryWriteUInt32(outCountAddress, (uint)deliveredCount))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (deliveredCount > 0)
        {
            TraceEventQueue(ctx, "wait-deliver", handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (timeoutAddress == 0 &&
            GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelWaitEqueue",
                GetEventQueueWakeKey(handle),
                () => ResumeWaitEqueue(ctx, handle, eventsAddress, eventCapacity, outCountAddress),
                () => HasPendingEvents(handle)))
        {
            TraceEventQueue(ctx, "wait-block", handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (timeoutAddress != 0 && ctx.TryReadUInt64(timeoutAddress, out var timeoutRaw))
        {
            var timeoutMicros = timeoutRaw & 0xFFFF_FFFFUL;
            var deadline = Environment.TickCount64 +
                Math.Max(1L, (long)Math.Min(timeoutMicros / 1000, int.MaxValue));
            lock (_eventQueueGate)
            {
                while (!HasPendingEvents(handle))
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        break;
                    }

                    Monitor.Wait(_eventQueueGate, (int)Math.Min(remaining, 100));
                }
            }

            deliveredCount = DequeueEvents(ctx, handle, eventsAddress, eventCapacity);
            if (outCountAddress != 0 && !ctx.TryWriteUInt32(outCountAddress, (uint)deliveredCount))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (deliveredCount > 0)
            {
                TraceEventQueue(ctx, "wait-timed-deliver", handle);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            TraceEventQueue(ctx, "wait-timeout", handle);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
        }

        TraceEventQueue(ctx, "wait", handle);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    public static bool IsValidEqueue(ulong handle)
    {
        lock (_eventQueueGate)
        {
            return _eventQueues.Contains(handle);
        }
    }

    public static bool EnqueueEvent(ulong handle, KernelQueuedEvent queuedEvent)
    {
        var queued = false;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.Contains(handle))
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var queue))
            {
                queue = new LinkedList<KernelQueuedEvent>();
                _pendingEvents[handle] = queue;
            }

            queue.AddLast(queuedEvent);
            queued = true;
            Monitor.PulseAll(_eventQueueGate);
        }

        if (queued)
        {
            WakeEventQueue(handle);
        }

        return queued;
    }

    public static bool RegisterEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong userData)
    {
        lock (_eventQueueGate)
        {
            if (!_eventQueues.Contains(handle))
            {
                return false;
            }

            if (!_registeredEvents.TryGetValue(handle, out var events))
            {
                events = new Dictionary<(ulong Ident, short Filter), KernelEventRegistration>();
                _registeredEvents[handle] = events;
            }

            events[(ident, filter)] = new KernelEventRegistration(ident, filter, userData);
            return true;
        }
    }

    public static bool DeleteRegisteredEvent(
        ulong handle,
        ulong ident,
        short filter)
    {
        lock (_eventQueueGate)
        {
            return _registeredEvents.TryGetValue(handle, out var events) &&
                   events.Remove((ident, filter));
        }
    }

    public static int TriggerRegisteredEvents(
        ulong ident,
        short filter,
        ulong data)
    {
        List<ulong>? wakeHandles = null;
        var triggeredCount = 0;
        lock (_eventQueueGate)
        {
            foreach (var (handle, registrations) in _registeredEvents)
            {
                if (!registrations.TryGetValue((ident, filter), out var registration))
                {
                    continue;
                }

                if (!_pendingEvents.TryGetValue(handle, out var queue))
                {
                    queue = new LinkedList<KernelQueuedEvent>();
                    _pendingEvents[handle] = queue;
                }

                QueueOrUpdateEvent(
                    queue,
                    new KernelQueuedEvent(
                        registration.Ident,
                        registration.Filter,
                        0,
                        1,
                        data,
                        registration.UserData));
                (wakeHandles ??= new List<ulong>()).Add(handle);
                triggeredCount++;
            }
        }

        if (wakeHandles is not null)
        {
            foreach (var handle in wakeHandles)
            {
                WakeEventQueue(handle);
            }
        }

        return triggeredCount;
    }

    public static bool TriggerDisplayEvent(
        ulong handle,
        ulong ident,
        short filter,
        ulong eventHint,
        ulong userData)
    {
        var triggered = false;
        lock (_eventQueueGate)
        {
            if (!_eventQueues.Contains(handle))
            {
                return false;
            }

            if (!_pendingEvents.TryGetValue(handle, out var events))
            {
                events = new LinkedList<KernelQueuedEvent>();
                _pendingEvents[handle] = events;
            }

            var count = 1UL;
            var pendingNode = FindPendingEvent(events, ident, filter);
            if (pendingNode is not null)
            {
                count = Math.Min(((pendingNode.Value.Data >> 12) & 0xFUL) + 1, 0xFUL);
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

            if (pendingNode is not null)
            {
                pendingNode.Value = triggeredEvent;
            }
            else
            {
                events.AddLast(triggeredEvent);
            }

            triggered = true;
        }

        if (triggered)
        {
            WakeEventQueue(handle);
        }

        return triggered;
    }

    private static int ResumeWaitEqueue(
        CpuContext ctx,
        ulong handle,
        ulong eventsAddress,
        int eventCapacity,
        ulong outCountAddress)
    {
        var deliveredCount = DequeueEvents(ctx, handle, eventsAddress, eventCapacity);
        if (outCountAddress != 0 && !ctx.TryWriteUInt32(outCountAddress, (uint)deliveredCount))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return deliveredCount > 0
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
    }

    private static bool HasPendingEvents(ulong handle)
    {
        lock (_eventQueueGate)
        {
            return _pendingEvents.TryGetValue(handle, out var events) && events.Count != 0;
        }
    }

    private static void QueueOrUpdateEvent(
        LinkedList<KernelQueuedEvent> queue,
        KernelQueuedEvent queuedEvent)
    {
        var pendingNode = FindPendingEvent(queue, queuedEvent.Ident, queuedEvent.Filter);
        if (pendingNode is null)
        {
            queue.AddLast(queuedEvent);
            return;
        }

        pendingNode.Value = queuedEvent with
        {
            Fflags = Math.Max(pendingNode.Value.Fflags + 1, queuedEvent.Fflags),
        };
    }

    private static LinkedListNode<KernelQueuedEvent>? FindPendingEvent(
        LinkedList<KernelQueuedEvent> queue,
        ulong ident,
        short filter)
    {
        for (var node = queue.First; node is not null; node = node.Next)
        {
            if (node.Value.Ident == ident && node.Value.Filter == filter)
            {
                return node;
            }
        }

        return null;
    }

    private static string GetEventQueueWakeKey(ulong handle) =>
        $"sceKernelWaitEqueue:{handle:X16}";

    private static void WakeEventQueue(ulong handle)
    {
        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetEventQueueWakeKey(handle));
    }

    private static int DequeueEvents(CpuContext ctx, ulong handle, ulong eventsAddress, int eventCapacity)
    {
        if (eventsAddress == 0 || eventCapacity <= 0)
        {
            return 0;
        }

        KernelQueuedEvent[] events;
        lock (_eventQueueGate)
        {
            if (!_pendingEvents.TryGetValue(handle, out var queue) || queue.Count == 0)
            {
                return 0;
            }

            var count = Math.Min(eventCapacity, queue.Count);
            events = new KernelQueuedEvent[count];
            for (var i = 0; i < count; i++)
            {
                events[i] = queue.First!.Value;
                queue.RemoveFirst();
            }
        }

        for (var i = 0; i < events.Length; i++)
        {
            if (!WriteKernelEvent(ctx, eventsAddress + ((ulong)i * KernelEventSize), events[i]))
            {
                return i;
            }
        }

        return events.Length;
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

    private static void TraceEventQueue(CpuContext ctx, string operation, ulong handle)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_EQUEUE"), "1", StringComparison.Ordinal))
        {
            return;
        }

        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out ulong returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] equeue.{operation}: handle=0x{handle:X16} rsi=0x{ctx[CpuRegister.Rsi]:X16} rdx=0x{ctx[CpuRegister.Rdx]:X16} ret=0x{returnRip:X16}");
    }
}
