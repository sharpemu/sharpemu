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
