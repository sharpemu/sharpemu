// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.VideoOut;

internal static class SubmissionFlowProfile
{
    internal enum EventKind
    {
        SubmitEntered,
        GeometryCaptureStarted,
        GeometryCaptureFinished,
        BackendEntered,
        PresenterEntered,
        Enqueued,
        ProcessingStarted,
        BackendReturned,
        WakeRequested,
        WakeSignaled,
        WaitStarted,
        WaitSignaled,
        WaitTimedOut,
    }

    internal readonly record struct TraceEvent(long Timestamp, int ThreadId, EventKind Kind,
        int QueueId, ulong SubmissionId, ulong Address, uint DwordCount, int Detail, ulong GuestThreadHandle = 0);

    internal readonly record struct Snapshot(TraceEvent[] Events, long TotalEvents);

    internal sealed class EventBuffer(int capacity)
    {
        private readonly object _gate = new();
        private readonly TraceEvent[] _events = new TraceEvent[capacity > 0
            ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
        private long _eventCount;
        private bool _closed;

        internal void Record(in TraceEvent traceEvent)
        {
            lock (_gate)
            {
                if (_closed) return;
                _events[(int)(_eventCount % _events.Length)] = traceEvent;
                _eventCount++;
            }
        }

        internal Snapshot Close()
        {
            lock (_gate)
            {
                _closed = true;
                var retainedCount = (int)Math.Min(_eventCount, _events.Length);
                var records = new TraceEvent[retainedCount];
                var first = _eventCount - retainedCount;
                for (var index = 0; index < retainedCount; index++)
                    records[index] = _events[(int)((first + index) % _events.Length)];
                var snapshot = new Snapshot(records, _eventCount);
                _eventCount = 0;
                return snapshot;
            }
        }

        internal void Reset()
        {
            lock (_gate)
            {
                _eventCount = 0;
                _closed = false;
            }
        }
    }

    // Allocate storage only when the detailed trace is enabled; no events are formatted on the hot path.
    private static class Storage
    {
        internal static readonly EventBuffer Events = new(65536);
        internal static readonly object WriteGate = new();
    }

    internal static void StartSession()
    {
        if (!RenderPhaseProfile.FrameTraceEnabled) return;
        lock (Storage.WriteGate) Storage.Events.Reset();
    }

    internal static void Record(EventKind kind, int queueId = -1, ulong submissionId = 0,
        ulong address = 0, uint dwordCount = 0, int detail = 0)
    {
        if (!RenderPhaseProfile.FrameTraceEnabled) return;
        Storage.Events.Record(new TraceEvent(Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
            kind, queueId, submissionId, address, dwordCount, detail, GuestThreadExecution.CurrentGuestThreadHandle));
    }

    internal static void RecordGuest(EventKind kind, uint queue, ulong submissionId, ulong address, uint dwordCount)
    {
        if (!RenderPhaseProfile.FrameTraceEnabled) return;
        Record(kind, queue == 0 ? 0 : unchecked((int)queue - GpuCommandInterpreter.ComputeQueueBase + 1),
            submissionId, address, dwordCount);
    }

    internal static void WriteTrace() => WriteTrace(Console.Error);

    internal static void WriteTrace(TextWriter output)
    {
        if (!RenderPhaseProfile.FrameTraceEnabled) return;
        lock (Storage.WriteGate)
        {
            var snapshot = Storage.Events.Close();
            if (snapshot.TotalEvents == 0) return;
            try
            {
                WriteSnapshot(output, snapshot);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // An unavailable trace output must not interrupt session cleanup.
            }
        }
    }

    private static void WriteSnapshot(TextWriter output, Snapshot snapshot)
    {
        output.WriteLine($"[PERF][SUBMISSION_FLOW_TRACE] frequency={Stopwatch.Frequency} retained={snapshot.Events.Length} overwritten={snapshot.TotalEvents - snapshot.Events.Length}");
        // Concurrent producers can publish out of timestamp order; retain both sequence and timestamp.
        for (var index = 0; index < snapshot.Events.Length; index++)
        {
            var traceEvent = snapshot.Events[index];
            output.WriteLine($"[PERF][SUBMISSION_FLOW] sequence={snapshot.TotalEvents - snapshot.Events.Length + index} timestamp={traceEvent.Timestamp} thread={traceEvent.ThreadId} guest=0x{traceEvent.GuestThreadHandle:X} event={traceEvent.Kind} queue={traceEvent.QueueId} submission={traceEvent.SubmissionId} address=0x{traceEvent.Address:X} dwords={traceEvent.DwordCount} detail={traceEvent.Detail}");
        }
        output.Flush();
    }
}
