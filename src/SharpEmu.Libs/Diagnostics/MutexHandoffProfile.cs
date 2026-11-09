// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Diagnostics;

internal static class MutexHandoffProfile
{
    internal static bool Enabled => RenderPhaseProfile.FrameTraceEnabled;
    internal readonly record struct TraceEvent(long Timestamp, int HostThread, ulong GuestThread,
        long MutexIdentity, string Stage, ulong Address, ulong Owner, ulong Waiter,
        string WakeKey, int Waiting, int Result);
    internal readonly record struct Snapshot(TraceEvent[] Events, long TotalEvents);

    internal sealed class EventBuffer(int capacity)
    {
        private readonly object _gate = new();
        private readonly TraceEvent[] _events = new TraceEvent[capacity > 0
            ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
        private long _count;
        private bool _closed;

        internal void Record(in TraceEvent traceEvent)
        {
            lock (_gate)
            {
                if (_closed) return;
                _events[(int)(_count % _events.Length)] = traceEvent;
                _count++;
            }
        }

        internal Snapshot Close()
        {
            lock (_gate)
            {
                _closed = true;
                var retained = (int)Math.Min(_count, _events.Length);
                var records = new TraceEvent[retained];
                for (var index = 0; index < retained; index++)
                    records[index] = _events[(int)((_count - retained + index) % _events.Length)];
                var snapshot = new Snapshot(records, _count);
                _count = 0;
                Array.Clear(_events);
                return snapshot;
            }
        }

        internal void Reset()
        {
            lock (_gate)
            {
                _count = 0;
                _closed = false;
                Array.Clear(_events);
            }
        }
    }

    private static class Storage
    {
        internal static readonly EventBuffer Events = new(131072);
        internal static long NextMutexIdentity;
    }

    internal static long CreateIdentity() => Enabled
        ? Interlocked.Increment(ref Storage.NextMutexIdentity) : 0;

    internal static void Record(long identity, string stage, ulong address, ulong owner,
        ulong waiter = 0, string wakeKey = "none", int waiting = 0, int result = 0)
    {
        if (!Enabled) return;
        Storage.Events.Record(new TraceEvent(Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
            GuestThreadExecution.CurrentGuestThreadHandle, identity, stage, address, owner,
            waiter, wakeKey, waiting, result));
    }

    internal static void StartSession()
    {
        if (RenderPhaseProfile.FrameTraceEnabled) Storage.Events.Reset();
    }

    internal static Snapshot Close() => RenderPhaseProfile.FrameTraceEnabled
        ? Storage.Events.Close() : new Snapshot([], 0);

    internal static void WriteTrace(TextWriter output, Snapshot snapshot)
    {
        if (snapshot.TotalEvents == 0) return;
        output.WriteLine($"[PERF][MUTEX_HANDOFF_TRACE] frequency={Stopwatch.Frequency} retained={snapshot.Events.Length} overwritten={snapshot.TotalEvents - snapshot.Events.Length}");
        for (var index = 0; index < snapshot.Events.Length; index++)
        {
            var item = snapshot.Events[index];
            output.WriteLine($"[PERF][MUTEX_HANDOFF] sequence={snapshot.TotalEvents - snapshot.Events.Length + index} timestamp={item.Timestamp} thread={item.HostThread} guest=0x{item.GuestThread:X} mutex={item.MutexIdentity} stage={item.Stage} address=0x{item.Address:X} owner=0x{item.Owner:X} waiter=0x{item.Waiter:X} wake={item.WakeKey} waiting={item.Waiting} result={item.Result}");
        }
        output.Flush();
    }
}
