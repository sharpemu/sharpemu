// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed unsafe class VulkanCommandProfile : IDisposable
{
    internal const int QueryCapacity = 512;
    private const int BufferCapacity = 64;
    private readonly Vk _vulkan;
    private readonly Device _device;
    private readonly float _timestampPeriod;
    private readonly uint _timestampBits;
    private readonly Dictionary<nint, BufferQueries> _buffers = new();
    private readonly Dictionary<IntervalKey, IntervalTotal> _totals = new();
    private readonly Action<string> _write;
    private long _reportStart = Stopwatch.GetTimestamp();
    private ulong _skippedMarkers;
    private ulong _unavailableBuffers;

    internal enum IntervalKind { Preparation, Draw, DrawIndexed, Dispatch, Tail, Overflow }
    private readonly record struct IntervalKey(IntervalKind Kind, ulong Pipeline);
    private readonly record struct Marker(IntervalKey Key, uint First, uint Second, uint Third);
    private sealed class IntervalTotal
    {
        public ulong Count;
        public double Milliseconds;
        public double MaximumMilliseconds;
        public Marker MaximumMarker;
    }

    private sealed class BufferQueries(QueryPool pool)
    {
        public readonly QueryPool Pool = pool;
        public readonly Marker[] Markers = new Marker[QueryCapacity];
        public int Count;
        public bool Submitted;
    }

    public VulkanCommandProfile(Vk vulkan, PhysicalDevice physical, Device device, uint queueFamily, Action<string>? write = null)
    {
        _vulkan = vulkan;
        _device = device;
        _write = write ?? Console.Error.WriteLine;
        vulkan.GetPhysicalDeviceProperties(physical, out var properties);
        _timestampPeriod = properties.Limits.TimestampPeriod;
        uint familyCount = 0;
        vulkan.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pointer = families)
            vulkan.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, pointer);
        _timestampBits = families[queueFamily].TimestampValidBits;
        _write($"[PERF][GPU_INTERVAL] timestamp_bits={_timestampBits} timestamp_period_ns={_timestampPeriod} queries_per_buffer={QueryCapacity} max_buffers={BufferCapacity}");
    }

    internal ulong CollectedIntervals { get; private set; }
    internal int ProfiledBufferCount => _buffers.Count;
    internal bool Supported => _timestampBits > 0 && _timestampPeriod > 0;

    public void BeginBuffer(CommandBuffer command)
    {
        if (!Supported)
            return;
        if (!_buffers.TryGetValue(command.Handle, out var queries))
        {
            if (_buffers.Count == BufferCapacity)
                return;
            var createInfo = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = QueryCapacity,
            };
            var result = _vulkan.CreateQueryPool(_device, &createInfo, null, out var pool);
            if (result != Result.Success)
            {
                _write($"[PERF][GPU_INTERVAL] query_pool_failed={result}");
                return;
            }
            queries = new BufferQueries(pool);
            _buffers.Add(command.Handle, queries);
        }

        // The command ring reuses a buffer only after its submission completes.
        Collect(queries);
        queries.Count = 1;
        queries.Submitted = false;
        _vulkan.CmdResetQueryPool(command, queries.Pool, 0, QueryCapacity);
        _vulkan.CmdWriteTimestamp(command, PipelineStageFlags.TopOfPipeBit, queries.Pool, 0);
    }

    public void WriteMarker(CommandBuffer command, IntervalKind kind, ulong pipeline = 0, uint first = 0, uint second = 0, uint third = 0)
    {
        if (!_buffers.TryGetValue(command.Handle, out var queries))
        {
            _skippedMarkers++;
            return;
        }
        if (queries.Count >= QueryCapacity - (kind == IntervalKind.Tail ? 0 : 1))
        {
            _skippedMarkers++;
            return;
        }

        var index = queries.Count++;
        queries.Markers[index] = new Marker(new IntervalKey(kind, pipeline), first, second, third);
        // Completion intervals do not isolate shader stages or eliminate overlap with later work.
        _vulkan.CmdWriteTimestamp(command, PipelineStageFlags.BottomOfPipeBit, queries.Pool, (uint)index);
    }

    public void MarkSubmitted(nint command)
    {
        if (_buffers.TryGetValue(command, out var queries))
            queries.Submitted = true;
    }

    internal static ulong ElapsedTicks(ulong start, ulong end, uint validBits) =>
        unchecked(end - start) & (validBits == 64 ? ulong.MaxValue : (1UL << (int)validBits) - 1);

    private void Collect(BufferQueries queries)
    {
        if (!queries.Submitted)
            return;
        queries.Submitted = false;
        Span<ulong> values = stackalloc ulong[QueryCapacity * 2];
        fixed (ulong* pointer = values)
        {
            var result = _vulkan.GetQueryPoolResults(_device, queries.Pool, 0, (uint)queries.Count,
                (nuint)(queries.Count * 2 * sizeof(ulong)), pointer, 2 * sizeof(ulong),
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWithAvailabilityBit);
            if (result != Result.Success)
            {
                _unavailableBuffers++;
                return;
            }
        }

        for (var index = 1; index < queries.Count; index++)
        {
            if (values[index * 2 + 1] == 0 || values[(index - 1) * 2 + 1] == 0)
                continue;
            var marker = queries.Markers[index];
            var key = marker.Key;
            if (!_totals.ContainsKey(key) && _totals.Count >= 256)
                key = new IntervalKey(IntervalKind.Overflow, 0);
            if (!_totals.TryGetValue(key, out var total))
                _totals.Add(key, total = new IntervalTotal());
            var milliseconds = ElapsedTicks(values[(index - 1) * 2], values[index * 2], _timestampBits) * (double)_timestampPeriod / 1_000_000;
            total.Count++;
            total.Milliseconds += milliseconds;
            if (milliseconds >= total.MaximumMilliseconds)
            {
                total.MaximumMilliseconds = milliseconds;
                total.MaximumMarker = marker;
            }
            CollectedIntervals++;
        }
        if (Stopwatch.GetElapsedTime(_reportStart).TotalSeconds >= 5)
            Report();
    }

    private void Report()
    {
        if (_totals.Count != 0 || _skippedMarkers != 0 || _unavailableBuffers != 0)
        {
            _write($"[PERF][GPU_INTERVAL] completed_ms={_totals.Values.Sum(total => total.Milliseconds):F3} intervals={_totals.Values.Sum(total => (long)total.Count)} skipped_markers={_skippedMarkers} unavailable_buffers={_unavailableBuffers}");
            foreach (var (key, total) in _totals.OrderByDescending(pair => pair.Value.Milliseconds).Take(12))
                _write($"[PERF][GPU_INTERVAL] kind={key.Kind} pipeline={key.Pipeline} count={total.Count} total_ms={total.Milliseconds:F3} max_ms={total.MaximumMilliseconds:F3} max_args={total.MaximumMarker.First},{total.MaximumMarker.Second},{total.MaximumMarker.Third}");
        }
        _totals.Clear();
        _skippedMarkers = 0;
        _unavailableBuffers = 0;
        _reportStart = Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        // The scheduler drains submissions before it releases device resources.
        foreach (var queries in _buffers.Values)
        {
            Collect(queries);
            _vulkan.DestroyQueryPool(_device, queries.Pool, null);
        }
        _buffers.Clear();
        Report();
    }
}
