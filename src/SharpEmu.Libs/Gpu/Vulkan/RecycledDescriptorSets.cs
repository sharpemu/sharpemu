// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// Storage-buffer descriptor sets of one layout. A set returns to the free list after the GPU
// work that uses it completes, so per-dispatch helpers do not create and destroy pools.
internal sealed unsafe class RecycledDescriptorSets(GpuDeviceInfo device, DescriptorSetLayout layout, uint storageBuffersPerSet) : IDisposable
{
    private const uint SetsPerPool = 64;
    private readonly object _gate = new();
    private readonly List<DescriptorPool> _pools = new();
    private readonly Stack<DescriptorSet> _free = new();

    public DescriptorSet Rent()
    {
        lock (_gate)
        {
            if (_free.Count == 0) AddPool();
            return _free.Pop();
        }
    }

    public void Return(DescriptorSet set)
    {
        lock (_gate) _free.Push(set);
    }

    private void AddPool()
    {
        var size = new DescriptorPoolSize(DescriptorType.StorageBuffer, storageBuffersPerSet * SetsPerPool);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, MaxSets = SetsPerPool, PoolSizeCount = 1, PPoolSizes = &size,
        };
        var result = device.Vk.CreateDescriptorPool(device.Device, &poolInfo, null, out var pool);
        if (result != Result.Success)
            throw SubmissionScheduler.Fatal($"Cannot create a recycled descriptor pool: {result}.");
        _pools.Add(pool);
        var layouts = stackalloc DescriptorSetLayout[(int)SetsPerPool];
        for (var index = 0; index < SetsPerPool; index++) layouts[index] = layout;
        var sets = new DescriptorSet[SetsPerPool];
        var allocation = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = SetsPerPool, PSetLayouts = layouts,
        };
        fixed (DescriptorSet* output = sets)
            result = device.Vk.AllocateDescriptorSets(device.Device, &allocation, output);
        if (result != Result.Success)
            throw SubmissionScheduler.Fatal($"Cannot allocate recycled descriptor sets: {result}.");
        foreach (var set in sets) _free.Push(set);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var pool in _pools) device.Vk.DestroyDescriptorPool(device.Device, pool, null);
            _pools.Clear();
            _free.Clear();
        }
    }
}
