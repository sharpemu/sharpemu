// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The descriptors one set of a layout takes from a pool, per pool size.
public readonly record struct DescriptorSetDemand(uint StorageBuffers = 0, uint SampledImages = 0, uint StorageImages = 0, uint Samplers = 0)
{
    public static DescriptorSetDemand Of(DescriptorType type, uint count) => type switch
    {
        DescriptorType.StorageBuffer => new DescriptorSetDemand(StorageBuffers: count),
        DescriptorType.SampledImage => new DescriptorSetDemand(SampledImages: count),
        DescriptorType.StorageImage => new DescriptorSetDemand(StorageImages: count),
        DescriptorType.Sampler => new DescriptorSetDemand(Samplers: count),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "The descriptor type has no pool size."),
    };

    public DescriptorSetDemand Add(in DescriptorSetDemand other) => new(
        StorageBuffers + other.StorageBuffers, SampledImages + other.SampledImages, StorageImages + other.StorageImages, Samplers + other.Samplers);

    public DescriptorSetDemand Scale(uint sets) => new(StorageBuffers * sets, SampledImages * sets, StorageImages * sets, Samplers * sets);

    public bool Fits(in DescriptorSetDemand remaining) =>
        StorageBuffers <= remaining.StorageBuffers && SampledImages <= remaining.SampledImages &&
        StorageImages <= remaining.StorageImages && Samplers <= remaining.Samplers;

    public DescriptorSetDemand Subtract(in DescriptorSetDemand taken) => new(
        StorageBuffers - taken.StorageBuffers, SampledImages - taken.SampledImages, StorageImages - taken.StorageImages, Samplers - taken.Samplers);
}

// Descriptor sets in batches per layout from one pool; a full pool retires with the current tick.
// The heap counts what each pool has left, so a batch is halved before it could exceed the pool.
public sealed unsafe class DescriptorHeap : IDisposable
{
    public const uint SetsPerPool = 1024;
    public const uint SetBatch = 32;

    private static readonly DescriptorPoolSize[] PoolSizes =
    [
        new(DescriptorType.StorageBuffer, 8192),
        new(DescriptorType.SampledImage, 8192),
        new(DescriptorType.StorageImage, 1024),
        new(DescriptorType.Sampler, 1024),
    ];

    private sealed class SetBatchState
    {
        public readonly DescriptorSet[] Sets = new DescriptorSet[SetBatch];
        public uint Size;
        public uint Allocation = SetBatch;
    }

    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private static readonly DescriptorSetDemand PoolCapacity = new(8192, 8192, 1024, 1024);

    private readonly Queue<(DescriptorPool Pool, ulong Tick)> _pendingPools = new();
    private readonly Dictionary<ulong, SetBatchState> _sets = new();
    private DescriptorPool _currentPool;
    private DescriptorSetDemand _remaining = PoolCapacity;
    private uint _remainingSets = SetsPerPool;

    public DescriptorHeap(GpuDeviceInfo device, SubmissionScheduler scheduler)
    {
        _device = device;
        _scheduler = scheduler;
        _currentPool = CreatePool();
    }

    public int PendingPoolCount => _pendingPools.Count;

    public DescriptorPool CurrentPool => _currentPool;

    public DescriptorSet Commit(DescriptorSetLayout layout, in DescriptorSetDemand demand)
    {
        if (layout.Handle == 0)
        {
            throw SubmissionScheduler.Fatal("The descriptor heap needs a set layout.");
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

        if (Allocate(layout, in demand, batch))
        {
            return batch.Sets[--batch.Size];
        }

        _pendingPools.Enqueue((_currentPool, _scheduler.CurrentTick));
        var (oldest, tick) = _pendingPools.Peek();
        _scheduler.Timeline.RefreshCompletedTick();
        if (_scheduler.Timeline.IsTickComplete(tick))
        {
            _pendingPools.Dequeue();
            _currentPool = oldest;
            var reset = _device.Vk.ResetDescriptorPool(_device.Device, _currentPool, 0);
            if (reset != Result.Success)
            {
                throw SubmissionScheduler.Fatal($"vkResetDescriptorPool failed: result={reset}.");
            }

            _remaining = PoolCapacity;
            _remainingSets = SetsPerPool;
        }
        else
        {
            _currentPool = CreatePool();
        }

        _sets.Clear();
        var freshBatch = new SetBatchState();
        _sets.Add(layout.Handle, freshBatch);
        if (!Allocate(layout, in demand, freshBatch))
        {
            throw SubmissionScheduler.Fatal(
                $"The descriptor heap cannot allocate a set from a fresh pool: buffers={demand.StorageBuffers} sampled={demand.SampledImages} storage={demand.StorageImages} samplers={demand.Samplers}.");
        }

        return freshBatch.Sets[--freshBatch.Size];
    }

    // Halves the batch until the pool has room for it; false when one set does not fit.
    private bool Allocate(DescriptorSetLayout layout, in DescriptorSetDemand demand, SetBatchState batch)
    {
        var layouts = stackalloc DescriptorSetLayout[(int)SetBatch];
        for (var index = 0; index < SetBatch; index++)
        {
            layouts[index] = layout;
        }

        for (;;)
        {
            if (batch.Allocation > _remainingSets || !demand.Scale(batch.Allocation).Fits(in _remaining))
            {
                if (batch.Allocation == 1)
                {
                    return false;
                }

                batch.Allocation /= 2;
                continue;
            }

            var allocate = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _currentPool,
                DescriptorSetCount = batch.Allocation,
                PSetLayouts = layouts,
            };
            Result result;
            fixed (DescriptorSet* sets = batch.Sets)
            {
                result = _device.Vk.AllocateDescriptorSets(_device.Device, &allocate, sets);
            }

            if (result == Result.Success)
            {
                batch.Size = batch.Allocation;
                _remaining = _remaining.Subtract(demand.Scale(batch.Allocation));
                _remainingSets -= batch.Allocation;
                return true;
            }

            if (result != Result.ErrorOutOfPoolMemory && result != Result.ErrorFragmentedPool)
            {
                throw SubmissionScheduler.Fatal($"vkAllocateDescriptorSets failed: result={result}.");
            }

            if (batch.Allocation == 1)
            {
                return false;
            }

            batch.Allocation /= 2;
        }
    }

    private DescriptorPool CreatePool()
    {
        fixed (DescriptorPoolSize* poolSizes = PoolSizes)
        {
            var create = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = SetsPerPool,
                PoolSizeCount = (uint)PoolSizes.Length,
                PPoolSizes = poolSizes,
            };
            var result = _device.Vk.CreateDescriptorPool(_device.Device, &create, null, out var pool);
            if (result != Result.Success)
            {
                throw SubmissionScheduler.Fatal($"vkCreateDescriptorPool failed: result={result}.");
            }

            _remaining = PoolCapacity;
            _remainingSets = SetsPerPool;
            return pool;
        }
    }

    public void Dispose()
    {
        _device.Vk.DestroyDescriptorPool(_device.Device, _currentPool, null);
        while (_pendingPools.Count != 0)
        {
            var (pool, tick) = _pendingPools.Dequeue();
            _scheduler.Timeline.Wait(tick);
            _device.Vk.DestroyDescriptorPool(_device.Device, pool, null);
        }

        _sets.Clear();
    }
}
