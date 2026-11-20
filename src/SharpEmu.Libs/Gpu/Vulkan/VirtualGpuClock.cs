// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// The 64-bit GPU-clock values a guest asks for with end-of-pipe events are GPU time on the console. The host
// reference clock instead measures when the emulator publishes the event, so a game timing its GPU work (the
// dynamic resolution of Ghost of Yotei) sees nearly equal start and end stamps and believes the GPU is idle.
//
// This clock counts the host GPU's busy time instead, at 100 MHz, starting at the reference clock of its creation:
// every command buffer gets a top-of-pipe timestamp at its start and an all-commands one at its end, and the busy
// time is the union of those intervals (GpuBusyLedger). The value of an event is the busy time at the end of the
// submission that holds the work before it.
// Biases: emulator work recorded into the stream counts as GPU time, and guest waits become host gaps that do not.
internal sealed unsafe class VirtualGpuClock : IDisposable
{
    private const ulong ClockFrequency = 100_000_000UL;
    // Slot 0 is the buffer's start, slot 1 its end, the rest are stamps taken at the point of an event.
    private const uint QueriesPerBuffer = 66;
    private const int FixedSlots = 2;
    private const double ReportIntervalSeconds = 5;

    private static VirtualGpuClock? s_active;

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<ulong> _readCompletedTick;
    private readonly double _periodNanoseconds;
    private readonly ulong _timestampMask;
    private readonly ulong _baseClock;
    private readonly object _gate = new();
    private readonly Dictionary<nint, BufferQueries> _buffers = new();
    private readonly Queue<(ulong Tick, nint[] Buffers)> _pending = new();
    private readonly GpuBusyLedger _ledger = new();
    private readonly bool _trace = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_VGPU_CLOCK") == "1";
    private ulong _lastReturned;
    private ulong _events;
    private ulong _stamped;
    private ulong _overflow;
    private ulong _nextStampId;
    private readonly ulong[] _sampleRing = new ulong[7];
    private int _sampleCount;
    private ulong _unavailable;
    private ulong _reportEvents;
    private ulong _reportBusy;
    private long _reportStart = Stopwatch.GetTimestamp();

    private sealed class BufferQueries(QueryPool pool)
    {
        public readonly QueryPool Pool = pool;
        public bool Unprocessed;
        public readonly List<ulong> StampIds = new();
    }

    private VirtualGpuClock(Vk vk, Device device, double periodNanoseconds, uint timestampBits, Func<ulong> readCompletedTick)
    {
        _vk = vk;
        _device = device;
        _periodNanoseconds = periodNanoseconds;
        _timestampMask = timestampBits >= 64 ? ulong.MaxValue : (1UL << (int)timestampBits) - 1;
        _readCompletedTick = readCompletedTick;
        _baseClock = EndOfPipe.ReadReferenceClock();
    }

    internal static VirtualGpuClock? Active => Volatile.Read(ref s_active);

    // Null when the queue cannot time its work or the clock is turned off.
    internal static VirtualGpuClock? TryCreate(Vk vk, PhysicalDevice physical, Device device, uint queueFamily, Func<ulong> readCompletedTick)
    {
        if (physical.Handle == 0 || Environment.GetEnvironmentVariable("SHARPEMU_VIRTUAL_GPU_CLOCK") == "0")
        {
            return null;
        }

        vk.GetPhysicalDeviceProperties(physical, out var properties);
        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pointer = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, pointer);
        }

        var bits = families[queueFamily].TimestampValidBits;
        var period = properties.Limits.TimestampPeriod;
        if (bits == 0 || period <= 0)
        {
            return null;
        }

        var clock = new VirtualGpuClock(vk, device, period, bits, readCompletedTick);
        Volatile.Write(ref s_active, clock);
        return clock;
    }

    public void BeginBuffer(CommandBuffer command)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(command.Handle, out var queries))
            {
                var createInfo = new QueryPoolCreateInfo
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Timestamp,
                    QueryCount = QueriesPerBuffer,
                };
                if (_vk.CreateQueryPool(_device, &createInfo, null, out var pool) != Result.Success)
                {
                    return;
                }

                queries = new BufferQueries(pool);
                _buffers.Add(command.Handle, queries);
            }
            else if (queries.Unprocessed)
            {
                // A buffer is reused only after its submission completed, and the queue completes in order, so
                // every earlier submission is complete as well; read them before the reset overwrites the queries.
                ProcessCompletedLocked(_readCompletedTick());
            }

            queries.Unprocessed = false;
            queries.StampIds.Clear();
            _vk.CmdResetQueryPool(command, queries.Pool, 0, QueriesPerBuffer);
            _vk.CmdWriteTimestamp2(command, PipelineStageFlags2.TopOfPipeBit, queries.Pool, 0);
        }
    }

    public void EndBuffer(CommandBuffer command)
    {
        lock (_gate)
        {
            if (_buffers.TryGetValue(command.Handle, out var queries))
            {
                _vk.CmdWriteTimestamp2(command, PipelineStageFlags2.AllCommandsBit, queries.Pool, 1);
                queries.Unprocessed = true;
            }
        }
    }

    // Called once the queue accepted the submission; tick is the value it signals on the scheduler timeline.
    public void Submitted(ReadOnlySpan<nint> buffers, ulong tick)
    {
        lock (_gate)
        {
            _pending.Enqueue((tick, buffers.ToArray()));
        }
    }

    public void ProcessCompleted(ulong completedTick)
    {
        lock (_gate)
        {
            ProcessCompletedLocked(completedTick);
        }
    }

    public ulong ValueAt(ulong tick)
    {
        lock (_gate)
        {
            _events++;
            ProcessCompletedLocked(_readCompletedTick());
            return Publish(_ledger.TryBusyAt(tick, out var busy) ? busy : _ledger.TotalBusy);
        }
    }

    // Writes a timestamp at this point of the command stream, so an event reads the GPU time at its own position
    // and not at the end of the batch of draws it was submitted with. Null when the buffer is not timed or its
    // stamp slots are used up; the caller then falls back to the per-submission value.
    public StampHandle? Stamp(CommandBuffer command)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(command.Handle, out var queries) ||
                FixedSlots + queries.StampIds.Count >= QueriesPerBuffer)
            {
                _overflow++;
                return null;
            }

            var id = ++_nextStampId;
            _vk.CmdWriteTimestamp2(command, PipelineStageFlags2.AllCommandsBit, queries.Pool, (uint)(FixedSlots + queries.StampIds.Count));
            queries.StampIds.Add(id);
            _stamped++;
            return new StampHandle(id);
        }
    }

    // The clock value at a stamp, once the submission holding it completed; null before that.
    public ulong? ValueAt(StampHandle stamp)
    {
        lock (_gate)
        {
            _events++;
            ProcessCompletedLocked(_readCompletedTick());
            if (!_ledger.TryStampBusy(stamp.Id, out var busy))
            {
                return null;
            }

            // Not clamped to the last value handed out: stamps resolve in stream order, and clamping would
            // erase the real difference between two stamps that are published out of order.
            var value = ToClock(busy);
            _sampleRing[_sampleCount++ % _sampleRing.Length] = value;
            Report();
            return value;
        }
    }

    internal readonly record struct StampHandle(ulong Id);

    public ulong CurrentValue()
    {
        lock (_gate)
        {
            _events++;
            ProcessCompletedLocked(_readCompletedTick());
            return Publish(_ledger.TotalBusy);
        }
    }

    private ulong ToClock(ulong busyTicks) =>
        _baseClock + (ulong)(busyTicks * _periodNanoseconds * ClockFrequency / 1e9);

    private ulong Publish(ulong busyTicks)
    {
        var value = ToClock(busyTicks);
        // A later event never reads earlier than one already handed out.
        if (value < _lastReturned)
        {
            value = _lastReturned;
        }

        _lastReturned = value;
        Report();
        return value;
    }

    private void ProcessCompletedLocked(ulong completedTick)
    {
        Span<ulong> values = stackalloc ulong[(int)QueriesPerBuffer];
        while (_pending.Count > 0 && _pending.Peek().Tick <= completedTick)
        {
            var (tick, buffers) = _pending.Dequeue();
            var before = _ledger.TotalBusy;
            foreach (var handle in buffers)
            {
                if (!_buffers.TryGetValue(handle, out var queries))
                {
                    continue;
                }

                var slots = (uint)(FixedSlots + queries.StampIds.Count);
                Result result;
                fixed (ulong* data = values)
                {
                    // No wait flag: a buffer whose queries are not ready is counted and skipped.
                    result = _vk.GetQueryPoolResults(_device, queries.Pool, 0, slots,
                        slots * sizeof(ulong), data, sizeof(ulong), QueryResultFlags.Result64Bit);
                }

                queries.Unprocessed = false;
                if (result != Result.Success)
                {
                    _unavailable++;
                    continue;
                }

                var stamps = new (ulong Id, ulong Timestamp)[queries.StampIds.Count];
                for (var index = 0; index < stamps.Length; index++)
                {
                    stamps[index] = (queries.StampIds[index], values[FixedSlots + index] & _timestampMask);
                }

                _ledger.AddBuffer(values[0] & _timestampMask, values[1] & _timestampMask, stamps);
            }

            _ledger.CompleteSubmission(tick);
            _reportBusy += _ledger.TotalBusy - before;
        }
    }

    // Microsecond differences between the last consecutive stamped events, oldest first.
    private string SampleDeltas()
    {
        var count = Math.Min(_sampleCount, _sampleRing.Length);
        var deltas = new List<string>();
        for (var index = _sampleCount - count + 1; index < _sampleCount; index++)
        {
            var previous = _sampleRing[(index - 1) % _sampleRing.Length];
            var current = _sampleRing[index % _sampleRing.Length];
            deltas.Add(((long)(current - previous) / 100).ToString());
        }

        return string.Join(",", deltas);
    }

    private void Report()
    {
        if (!_trace)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _reportStart) / (double)Stopwatch.Frequency;
        if (elapsed < ReportIntervalSeconds)
        {
            return;
        }

        var busyMilliseconds = _reportBusy * _periodNanoseconds / 1e6;
        Console.Error.WriteLine(
            $"[PERF][VGPU_CLOCK] events={_events - _reportEvents} busy_ms_per_s={busyMilliseconds / elapsed:F1} unavailable={_unavailable} stamped={_stamped} overflow={_overflow} pending={_pending.Count} sample_deltas={SampleDeltas()}");
        _reportEvents = _events;
        _reportBusy = 0;
        _reportStart = now;
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref s_active, null, this);
        lock (_gate)
        {
            foreach (var queries in _buffers.Values)
            {
                _vk.DestroyQueryPool(_device, queries.Pool, null);
            }

            _buffers.Clear();
            _pending.Clear();
        }
    }
}
