// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE.GuestMemory;

public static class GuestMemoryProfile
{
    public enum Operation
    {
        MappingRequest,
        MappingDrain,
        MappingApply,
        MappingRegister,
        BackingMap,
        BackingUnmap,
        UnregisterDrain,
        UnregisterBuffers,
        UnregisterImages,
        UnregisterSpans,
        UnregisterPermissions,
        Reservation,
        ReservationSearch,
        ReservationHost,
        ReservationPublish,
        BufferReadback,
        Count,
    }

    private static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"), "1", StringComparison.Ordinal);
    private static readonly Measurements Counters = new();
    public static readonly bool ReadbackDetailsEnabled = Enabled && string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE"), "1", StringComparison.Ordinal);
    private static readonly ReadbackMeasurements ReadbackCounters = new();

    public enum ReadbackSource
    {
        ExplicitReadback,
        CpuReadSynchronization,
        CommandMemoryRead,
        ShaderResourceRead,
        StoreDownload,
        CpuWriteInvalidation,
    }

    internal readonly record struct ReadbackRange(ulong Address, ulong Size, bool CpuWrite,
        ReadbackSource Source = ReadbackSource.ExplicitReadback);
    internal readonly record struct ReadbackMeasurement(long Calls, long Downloads, ulong Bytes, long Ticks, long MaximumTicks);

    internal sealed class ReadbackMeasurements
    {
        internal const int Capacity = 128;
        private readonly object _gate = new();
        private readonly Dictionary<ReadbackRange, ReadbackMeasurement> _ranges = new();
        private long _untrackedCalls;

        internal void Record(ReadbackRange range, ulong bytes, long ticks)
        {
            lock (_gate)
            {
                if (!_ranges.TryGetValue(range, out var value) && _ranges.Count == Capacity)
                {
                    _untrackedCalls++;
                    return;
                }
                _ranges[range] = new ReadbackMeasurement(value.Calls + 1, value.Downloads + (bytes != 0 ? 1 : 0),
                    value.Bytes + bytes, value.Ticks + ticks, Math.Max(value.MaximumTicks, ticks));
            }
        }

        internal (KeyValuePair<ReadbackRange, ReadbackMeasurement>[] Ranges, long UntrackedCalls) TakeSnapshot()
        {
            lock (_gate)
            {
                var snapshot = (_ranges.ToArray(), _untrackedCalls);
                _ranges.Clear();
                _untrackedCalls = 0;
                return snapshot;
            }
        }
    }

    public static void RecordBufferReadback(ulong address, ulong size, bool cpuWrite, ulong bytes, long ticks,
        ReadbackSource source = ReadbackSource.ExplicitReadback)
    {
        if (ReadbackDetailsEnabled)
            ReadbackCounters.Record(new ReadbackRange(address, size, cpuWrite, source), bytes, ticks);
    }

    internal readonly record struct Measurement(long Calls, long Ticks, long MaximumTicks);

    internal sealed class Measurements
    {
        private readonly object _gate = new();
        private readonly Measurement[] _values = new Measurement[(int)Operation.Count];

        internal void Record(Operation operation, long ticks)
        {
            lock (_gate)
            {
                ref var value = ref _values[(int)operation];
                value = new Measurement(value.Calls + 1, value.Ticks + ticks, Math.Max(value.MaximumTicks, ticks));
            }
        }

        internal Measurement[] TakeSnapshot()
        {
            lock (_gate)
            {
                var snapshot = (Measurement[])_values.Clone();
                Array.Clear(_values);
                return snapshot;
            }
        }
    }

    public readonly ref struct Scope
    {
        private readonly Operation _operation;
        private readonly long _started;
        private readonly bool _active;

        internal Scope(Operation operation)
        {
            _operation = operation;
            _started = Stopwatch.GetTimestamp();
            _active = true;
        }

        public void Dispose()
        {
            if (_active)
                Counters.Record(_operation, Stopwatch.GetTimestamp() - _started);
        }
    }

    public static Scope Measure(Operation operation) => Enabled ? new Scope(operation) : default;

    // Nested operations overlap. These totals describe completed calls, not exclusive render time.
    public static void WriteReport()
    {
        if (!Enabled)
            return;
        var snapshot = Counters.TakeSnapshot();
        for (var index = 0; index < snapshot.Length; index++)
        {
            var value = snapshot[index];
            if (value.Calls == 0)
                continue;
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][GUEST_MEMORY] operation={(Operation)index} calls={value.Calls} inclusive_ms={value.Ticks * 1000.0 / Stopwatch.Frequency:F3} max_ms={value.MaximumTicks * 1000.0 / Stopwatch.Frequency:F3}"));
        }
        if (!ReadbackDetailsEnabled)
            return;
        var readbacks = ReadbackCounters.TakeSnapshot();
        foreach (var (range, value) in readbacks.Ranges.OrderByDescending(entry => entry.Value.Ticks))
            Console.Error.WriteLine(FormattableString.Invariant(
                $"[PERF][BUFFER_READBACK] address=0x{range.Address:X} size=0x{range.Size:X} cpu_write={range.CpuWrite} source={range.Source} calls={value.Calls} downloads={value.Downloads} bytes={value.Bytes} inclusive_ms={value.Ticks * 1000.0 / Stopwatch.Frequency:F3} max_ms={value.MaximumTicks * 1000.0 / Stopwatch.Frequency:F3}"));
        if (readbacks.UntrackedCalls != 0)
            Console.Error.WriteLine($"[PERF][BUFFER_READBACK] untracked_calls={readbacks.UntrackedCalls}");
    }
}
