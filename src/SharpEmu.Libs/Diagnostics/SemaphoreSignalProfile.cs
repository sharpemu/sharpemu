// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Diagnostics;

internal static class SemaphoreSignalProfile
{
    internal enum Stage { Entered, Published, WakeStarted, WakeFinished, Rejected }

    internal readonly record struct Signal(long Sequence, ulong GuestThread, ulong ReturnAddress,
        uint Handle, int Count)
    {
        internal void Record(Stage stage, int available = -1, int waiting = -1, int result = 0)
        {
            if (Sequence == 0) return;
            Storage.Events.Record(new TraceEvent(Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
                this, stage, available, waiting, result));
        }
    }

    internal readonly record struct TraceEvent(long Timestamp, int Thread, Signal Signal, Stage Stage,
        int Available, int Waiting, int Result);
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
                var result = new Snapshot(records, _count);
                _count = 0;
                return result;
            }
        }

        internal void Reset()
        {
            lock (_gate)
            {
                _count = 0;
                _closed = false;
            }
        }
    }

    private static class Storage
    {
        internal static readonly EventBuffer Events = new(32768);
        internal static long SignalSequence;
    }

    internal static Signal Begin(uint handle, int count)
    {
        if (!RenderPhaseProfile.FrameTraceEnabled) return default;
        GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame);
        var signal = new Signal(Interlocked.Increment(ref Storage.SignalSequence),
            GuestThreadExecution.CurrentGuestThreadHandle, frame.ReturnRip, handle, count);
        signal.Record(Stage.Entered);
        return signal;
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
        output.WriteLine($"[PERF][SEMAPHORE_SIGNAL_TRACE] frequency={Stopwatch.Frequency} retained={snapshot.Events.Length} overwritten={snapshot.TotalEvents - snapshot.Events.Length}");
        for (var index = 0; index < snapshot.Events.Length; index++)
        {
            var item = snapshot.Events[index];
            var signal = item.Signal;
            output.WriteLine($"[PERF][SEMAPHORE_SIGNAL] sequence={snapshot.TotalEvents - snapshot.Events.Length + index} timestamp={item.Timestamp} thread={item.Thread} guest=0x{signal.GuestThread:X} signal={signal.Sequence} stage={item.Stage} handle=0x{signal.Handle:X} add={signal.Count} available={item.Available} waiting={item.Waiting} result={item.Result} return=0x{signal.ReturnAddress:X}");
        }
        output.Flush();
    }
}
