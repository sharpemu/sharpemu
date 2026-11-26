// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

public sealed unsafe class DescriptorPoolStock : IDisposable
{
    private readonly GpuDeviceInfo _device;
    private readonly ConcurrentBag<DescriptorPool> _free = new();
    private readonly List<DescriptorPool> _all = new();
    private const int MaxRetainedPools = 64;
    private readonly ConcurrentBag<RetainedDescriptorPool> _idle = new();
    private readonly Dictionary<ulong, RetainedDescriptorPool> _retained = new();
    private int _retainedCount;
    private bool _disposed;

    public DescriptorPoolStock(GpuDeviceInfo device)
    {
        _device = device;
    }

    public DescriptorPool Rent()
    {
        if (_free.TryTake(out var pool))
        {
            return pool;
        }

        pool = DescriptorHeap.CreatePool(_device);
        lock (_all)
        {
            _all.Add(pool);
        }

        return pool;
    }

    internal RetainedDescriptorPool? RentRetained()
    {
        if (_idle.TryTake(out var previous)) return previous;
        lock (_all)
        {
            if (_retainedCount == MaxRetainedPools) return null;
            _retainedCount++;
        }

        // Pool creation can block in the driver. Keep it outside the submission retirement lock.
        try
        {
            var retained = new RetainedDescriptorPool(Rent());
            lock (_all) _retained.Add(retained.Pool.Handle, retained);
            return retained;
        }
        catch
        {
            lock (_all) _retainedCount--;
            throw;
        }
    }

    public void Return(DescriptorPool pool)
    {
        lock (_all)
        {
            if (_disposed)
            {
                return;
            }

            if (_retained.TryGetValue(pool.Handle, out var retained))
            {
                if (retained.FinishSlice())
                {
                    _idle.Add(retained);
                    return;
                }

                _retained.Remove(pool.Handle);
                _retainedCount--;
            }

            var reset = _device.Vk.ResetDescriptorPool(_device.Device, pool, 0);
            if (reset != Result.Success)
            {
                throw SubmissionScheduler.Fatal($"vkResetDescriptorPool failed: result={reset}.");
            }
        }

        _free.Add(pool);
    }

    public void Dispose()
    {
        lock (_all)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var pool in _all)
            {
                _device.Vk.DestroyDescriptorPool(_device.Device, pool, null);
            }

            _all.Clear();
            _retained.Clear();
            _idle.Clear();
        }

        while (_free.TryTake(out _))
        {
        }
    }
}

public sealed class SliceDescriptorHeap(GpuDeviceInfo device, DescriptorPoolStock stock)
{
    private const uint InitialBatch = 4;
    private RetainedDescriptorPool? _retained;
    private bool _triedRetained;

    private sealed class SetBatchState
    {
        public readonly DescriptorSet[] Sets = new DescriptorSet[DescriptorHeap.SetBatch];
        public uint Size;
        public uint Allocation = InitialBatch;
    }

    private readonly Dictionary<ulong, SetBatchState> _sets = new();
    private readonly List<DescriptorPool> _pools = new();
    private DescriptorPool _currentPool;
    private DescriptorSetDemand _remaining;
    private uint _remainingSets;

    public DescriptorSet Commit(DescriptorSetLayout layout, in DescriptorSetDemand demand)
    {
        if (layout.Handle == 0)
        {
            throw SubmissionScheduler.Fatal("The slice descriptor heap needs a set layout.");
        }

        if (!_sets.TryGetValue(layout.Handle, out var batch))
        {
            batch = new SetBatchState();
            _sets.Add(layout.Handle, batch);
        }

        if (batch.Size != 0)
        {
            return batch.Sets[--batch.Size];
        }

        if (_currentPool.Handle != 0 && Allocate(layout, in demand, batch))
        {
            return batch.Sets[--batch.Size];
        }

        _currentPool = stock.Rent();
        _pools.Add(_currentPool);
        _remaining = DescriptorHeap.PoolCapacity;
        _remainingSets = DescriptorHeap.SetsPerPool;
        batch.Allocation = InitialBatch;
        if (!Allocate(layout, in demand, batch))
        {
            throw SubmissionScheduler.Fatal(
                $"The slice descriptor heap cannot allocate a set from a fresh pool: buffers={demand.StorageBuffers} sampled={demand.SampledImages} storage={demand.StorageImages} samplers={demand.Samplers}.");
        }

        return batch.Sets[--batch.Size];
    }

    internal DescriptorSet Commit(ulong pipeline, DescriptorSetLayout layout, in DescriptorSetDemand demand,
        ReadOnlySpan<ulong> key, Span<WriteDescriptorSet> writes)
    {
        if (pipeline != 0 && layout.Handle != 0 && key.Length is > 0 and <= RetainedDescriptorPool.MaxKeyWords &&
            RetainedDescriptorPool.CoversLayout(in demand, writes))
        {
            if (!_triedRetained)
            {
                _triedRetained = true;
                _retained = stock.RentRetained();
                if (_retained is not null) _pools.Add(_retained.Pool);
            }

            if (_retained is not null && _retained.TryCommit(device, pipeline, layout, in demand, key, writes, out var written))
                return written;
        }

        var set = Commit(layout, in demand);
        DescriptorHeap.WriteSet(device, set, writes);
        return set;
    }

    public List<DescriptorPool> TakePools()
    {
        _retained = null;
        _triedRetained = false;
        if (_pools.Count == 0)
        {
            return [];
        }

        var taken = new List<DescriptorPool>(_pools);
        _pools.Clear();
        _currentPool = default;
        foreach (var batch in _sets.Values)
        {
            batch.Size = 0;
            batch.Allocation = InitialBatch;
        }

        return taken;
    }

    private bool Allocate(DescriptorSetLayout layout, in DescriptorSetDemand demand, SetBatchState batch)
    {
        if (!DescriptorHeap.TryAllocateBatch(device, _currentPool, layout, in demand, batch.Sets, ref batch.Allocation, ref _remainingSets, ref _remaining))
        {
            return false;
        }

        batch.Size = batch.Allocation;
        batch.Allocation = Math.Min(DescriptorHeap.SetBatch, batch.Allocation * 2);
        return true;
    }
}
