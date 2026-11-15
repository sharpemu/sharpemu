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

    public void Return(DescriptorPool pool)
    {
        lock (_all)
        {
            if (_disposed)
            {
                return;
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
        }

        while (_free.TryTake(out _))
        {
        }
    }
}

public sealed class SliceDescriptorHeap(GpuDeviceInfo device, DescriptorPoolStock stock)
{
    private sealed class SetBatchState
    {
        public readonly DescriptorSet[] Sets = new DescriptorSet[DescriptorHeap.SetBatch];
        public uint Size;
        public uint Allocation = DescriptorHeap.SetBatch;
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
        batch.Allocation = DescriptorHeap.SetBatch;
        if (!Allocate(layout, in demand, batch))
        {
            throw SubmissionScheduler.Fatal(
                $"The slice descriptor heap cannot allocate a set from a fresh pool: buffers={demand.StorageBuffers} sampled={demand.SampledImages} storage={demand.StorageImages} samplers={demand.Samplers}.");
        }

        return batch.Sets[--batch.Size];
    }

    public List<DescriptorPool> TakePools()
    {
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
            batch.Allocation = DescriptorHeap.SetBatch;
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
        return true;
    }
}
