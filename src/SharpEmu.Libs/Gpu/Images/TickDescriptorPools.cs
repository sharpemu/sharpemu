// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

// Descriptor sets that live for one scheduler tick; each pool is reset after its tick completes.
public sealed unsafe class TickDescriptorPools : IDisposable
{
    private readonly GpuDeviceInfo _device;
    private readonly SubmissionScheduler _scheduler;
    private readonly DescriptorPoolSize[] _descriptorPoolSizes;
    private readonly uint _maximumSetsPerPool;
    private readonly List<DescriptorPool> _availablePools = new();
    private readonly List<(ulong Tick, DescriptorPool Pool, uint Used)> _activePools = new();

    public TickDescriptorPools(GpuDeviceInfo device, SubmissionScheduler scheduler, DescriptorPoolSize[] descriptorPoolSizes, uint maximumSetsPerPool)
    {
        _device = device;
        _scheduler = scheduler;
        _descriptorPoolSizes = descriptorPoolSizes;
        _maximumSetsPerPool = maximumSetsPerPool;
    }

    public DescriptorSet Allocate(DescriptorSetLayout layout)
    {
        var tick = _scheduler.CurrentTick;
        for (var index = 0; index < _activePools.Count; index++)
        {
            var entry = _activePools[index];
            if (entry.Tick == tick && entry.Used < _maximumSetsPerPool)
            {
                _activePools[index] = (tick, entry.Pool, entry.Used + 1);
                return AllocateFrom(entry.Pool, layout);
            }
        }

        var pool = TakePool();
        _activePools.Add((tick, pool, 1));
        _scheduler.QueueCompletionAction(() => Release(pool));
        return AllocateFrom(pool, layout);
    }

    private DescriptorPool TakePool()
    {
        if (_availablePools.Count != 0)
        {
            var reused = _availablePools[^1];
            _availablePools.RemoveAt(_availablePools.Count - 1);
            return reused;
        }

        fixed (DescriptorPoolSize* descriptorPoolSizes = _descriptorPoolSizes)
        {
            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = _maximumSetsPerPool,
                PoolSizeCount = (uint)_descriptorPoolSizes.Length,
                PPoolSizes = descriptorPoolSizes,
            };
            var result = _device.Vk.CreateDescriptorPool(_device.Device, &info, null, out var pool);
            if (result != Result.Success)
            {
                throw SubmissionScheduler.Fatal($"vkCreateDescriptorPool failed: result={result}.");
            }

            return pool;
        }
    }

    private DescriptorSet AllocateFrom(DescriptorPool pool, DescriptorSetLayout layout)
    {
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        var result = _device.Vk.AllocateDescriptorSets(_device.Device, &info, out var set);
        if (result != Result.Success)
        {
            throw SubmissionScheduler.Fatal($"vkAllocateDescriptorSets failed: result={result}.");
        }

        return set;
    }

    private void Release(DescriptorPool pool)
    {
        _activePools.RemoveAll(entry => entry.Pool.Handle == pool.Handle);
        _device.Vk.ResetDescriptorPool(_device.Device, pool, 0);
        _availablePools.Add(pool);
    }

    public void Dispose()
    {
        foreach (var (_, pool, _) in _activePools)
        {
            _device.Vk.DestroyDescriptorPool(_device.Device, pool, null);
        }

        foreach (var pool in _availablePools)
        {
            _device.Vk.DestroyDescriptorPool(_device.Device, pool, null);
        }

        _activePools.Clear();
        _availablePools.Clear();
    }
}
