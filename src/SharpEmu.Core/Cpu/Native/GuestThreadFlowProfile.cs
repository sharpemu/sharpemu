// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Core.Cpu.Native;

internal sealed class GuestThreadFlowProfile(int capacity = 131072)
{
    internal static readonly bool Enabled =
        (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ||
         Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_GUEST_SCHEDULER") == "1") &&
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE") == "1";

    internal enum EventKind
    {
        Blocked, Ready, Claimed, Scheduled, RunStarted, RunStopped, CallbackResumed,
        ExecutorReleased, ClaimDeferred
    }

    internal readonly record struct TraceEvent(long Timestamp, int HostThreadId, ulong GuestThreadHandle,
        string Name, EventKind Kind, string? Reason, string? WakeKey, string? ImportNid,
        ulong Argument0, ulong Argument1, long DeadlineTimestamp);

    internal readonly record struct Snapshot(TraceEvent[] Events, long TotalEvents);

    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private TraceEvent[]? _events;
    private long _eventCount;
    private bool _closed;

    internal void StartSession()
    {
        lock (_writeGate)
        {
            lock (_gate)
            {
                _events = null;
                _eventCount = 0;
                _closed = false;
            }
        }
    }

    internal void Record(in TraceEvent traceEvent)
    {
        lock (_gate)
        {
            if (_closed) return;
            _events ??= new TraceEvent[_capacity];
            _events[(int)(_eventCount % _capacity)] = traceEvent;
            _eventCount++;
        }
    }

    internal Snapshot Close()
    {
        lock (_gate)
        {
            _closed = true;
            var retained = (int)Math.Min(_eventCount, _capacity);
            var records = new TraceEvent[retained];
            var first = _eventCount - retained;
            for (var index = 0; index < retained; index++)
                records[index] = _events![(int)((first + index) % _capacity)];
            var snapshot = new Snapshot(records, _eventCount);
            _events = null;
            _eventCount = 0;
            return snapshot;
        }
    }

    internal void WriteTrace(TextWriter output)
    {
        // A concurrent shutdown caller must wait for the first writer to finish.
        lock (_writeGate)
        {
            WriteRetainedEvents(output);
        }
    }

    private void WriteRetainedEvents(TextWriter output)
    {
        var snapshot = Close();
        if (snapshot.TotalEvents == 0) return;
        output.WriteLine($"[PERF][GUEST_FLOW_TRACE] frequency={Stopwatch.Frequency} retained={snapshot.Events.Length} overwritten={snapshot.TotalEvents - snapshot.Events.Length}");
        for (var index = 0; index < snapshot.Events.Length; index++)
        {
            var item = snapshot.Events[index];
            output.WriteLine($"[PERF][GUEST_FLOW] sequence={snapshot.TotalEvents - snapshot.Events.Length + index} timestamp={item.Timestamp} thread={item.HostThreadId} guest=0x{item.GuestThreadHandle:X} name='{Quote(item.Name)}' event={item.Kind} reason='{Quote(item.Reason)}' wake='{Quote(item.WakeKey)}' nid='{Quote(item.ImportNid)}' arg0=0x{item.Argument0:X} arg1=0x{item.Argument1:X} deadline={item.DeadlineTimestamp}");
        }
        output.Flush();
    }

    // Guest names must not split one trace event across multiple output lines.
    private static string Quote(string? value) => (value ?? "none")
        .Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n");
}
