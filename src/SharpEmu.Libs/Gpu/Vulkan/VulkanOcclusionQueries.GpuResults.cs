// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// Sums the same query results on the GPU in stream order, so GPU readers of a dump need no CPU round trip.
internal sealed unsafe partial class VulkanOcclusionQueries
{
    private const uint DumpSlots = 4096;
    internal const ulong ReadySlotOffset = DumpSlots * sizeof(ulong);

    private GpuDeviceInfo? _gpuDevice;
    private SubmissionScheduler? _gpuScheduler;
    private readonly Dictionary<int, GpuBuffer> _gpuCounters = new();
    private GpuBuffer? _queryResults;
    private GpuBuffer? _dumpValues;
    private OcclusionSumPipeline? _sumPipeline;
    private uint _nextDumpSlot;

    public bool HasGpuResults => _gpuDevice is not null;

    // Slot values for pending dumps, followed by one slot that holds only the ready bit.
    public GpuBuffer DumpValues => _dumpValues ?? throw SubmissionScheduler.Fatal("Occlusion GPU results are not enabled.");

    public void EnableGpuResults(GpuDeviceInfo device, SubmissionScheduler scheduler)
    {
        if (_gpuDevice is not null) return;
        _gpuDevice = device;
        _gpuScheduler = scheduler;
        _sumPipeline = new OcclusionSumPipeline(device);
        _queryResults = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, Capacity * sizeof(ulong));
        _dumpValues = new GpuBuffer(device, scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, ReadySlotOffset + sizeof(ulong));
    }

    private GpuBuffer CounterBuffer(int queueId)
    {
        if (_gpuCounters.TryGetValue(queueId, out var existing)) return existing;
        var buffer = new GpuBuffer(_gpuDevice!, _gpuScheduler!, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, sizeof(ulong));
        try { _gpuCounters.Add(queueId, buffer); }
        catch { buffer.Dispose(); throw; }
        buffer.Fill(0, sizeof(ulong), 0);
        return buffer;
    }

    // Each recorded query adds to the counter of the group that was current when it began.
    private void ReduceRecordedQueries()
    {
        if (_gpuDevice is null) return;
        if (_active) throw SubmissionScheduler.Fatal("An occlusion query must end before reduction.");
        foreach (var block in _recorded)
        {
            if (block.ReducedCount == block.Count) continue;
            var first = block.ReducedCount;
            var command = new CommandBuffer(_gpuScheduler!.Current.Handle);
            vulkan.CmdCopyQueryPoolResults(command, block.Pool, first, block.Count - first, _queryResults!.Handle,
                first * sizeof(ulong), sizeof(ulong), QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
            while (first < block.Count)
            {
                var end = first + 1;
                while (end < block.Count && block.Queues[end] == block.Queues[first]) end++;
                _sumPipeline!.Record(_gpuScheduler, _queryResults, first * 2, end - first,
                    CounterBuffer(block.Queues[first]), 0, accumulate: true);
                first = end;
            }
            block.ReducedCount = block.Count;
        }
    }

    private void ResetGpuCounter(int queueId)
    {
        if (_gpuDevice is null) return;
        ReduceRecordedQueries();
        CounterBuffer(queueId).Fill(0, sizeof(ulong), 0);
    }

    // Writes the ready bit and the group sum to a slot and returns its offset in DumpValues.
    public ulong RecordGpuDump(int queueId)
    {
        if (_gpuDevice is null) throw SubmissionScheduler.Fatal("Occlusion GPU results are not enabled.");
        ReduceRecordedQueries();
        if (_nextDumpSlot == 0)
        {
            _dumpValues!.Fill(ReadySlotOffset, sizeof(uint), 0);
            _dumpValues.Fill(ReadySlotOffset + sizeof(uint), sizeof(uint), 0x8000_0000u);
        }
        var slot = (ulong)(_nextDumpSlot++ % DumpSlots) * sizeof(ulong);
        _sumPipeline!.Record(_gpuScheduler!, CounterBuffer(queueId), 0, 1, _dumpValues!, slot, accumulate: false, ready: true);
        return slot;
    }

    private void DisposeGpuResults()
    {
        foreach (var buffer in _gpuCounters.Values) buffer.Dispose();
        _gpuCounters.Clear();
        _queryResults?.Dispose();
        _dumpValues?.Dispose();
        _sumPipeline?.Dispose();
        _queryResults = null;
        _dumpValues = null;
        _sumPipeline = null;
        _gpuDevice = null;
    }
}
