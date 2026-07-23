// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanHostBufferPoolKey(
    BufferUsageFlags Usage,
    ulong Capacity);

internal readonly record struct VulkanHostBufferAllocation(
    VkBuffer Buffer,
    DeviceMemory Memory,
    VulkanHostBufferPoolKey Key,
    nint Mapped);

internal sealed class VulkanHostBufferPool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<VulkanHostBufferPoolKey, Stack<VulkanHostBufferAllocation>>
        _available = [];
    private readonly Dictionary<ulong, VulkanHostBufferAllocation> _allocations = [];
    private readonly HashSet<ulong> _cachedHandles = [];
    private readonly Action<VulkanHostBufferAllocation> _destroy;

    public VulkanHostBufferPool(
        ulong maximumCachedBytes,
        Action<VulkanHostBufferAllocation> destroy)
    {
        MaximumCachedBytes = maximumCachedBytes;
        _destroy = destroy;
    }

    public ulong MaximumCachedBytes { get; }

    public ulong CachedBytes { get; private set; }

    public bool TryRent(
        VulkanHostBufferPoolKey key,
        out VulkanHostBufferAllocation allocation)
    {
        lock (_gate)
        {
            if (!_available.TryGetValue(key, out var available) ||
                !available.TryPop(out allocation))
            {
                allocation = default;
                return false;
            }

            _cachedHandles.Remove(allocation.Buffer.Handle);
            CachedBytes -= allocation.Key.Capacity;
            return true;
        }
    }

    public void Register(VulkanHostBufferAllocation allocation)
    {
        if (allocation.Buffer.Handle == 0)
        {
            throw new ArgumentException("A pooled buffer must have a valid handle.", nameof(allocation));
        }

        lock (_gate)
        {
            _allocations.Add(allocation.Buffer.Handle, allocation);
        }
    }

    public bool Return(VkBuffer buffer, DeviceMemory memory)
    {
        VulkanHostBufferAllocation? toDestroy = null;
        lock (_gate)
        {
            if (!_allocations.TryGetValue(buffer.Handle, out var allocation) ||
                allocation.Memory.Handle != memory.Handle)
            {
                return false;
            }

            if (!_cachedHandles.Add(buffer.Handle))
            {
                return true;
            }

            if (allocation.Key.Capacity > MaximumCachedBytes - CachedBytes)
            {
                _cachedHandles.Remove(buffer.Handle);
                _allocations.Remove(buffer.Handle);
                toDestroy = allocation;
            }
            else
            {
                if (!_available.TryGetValue(allocation.Key, out var available))
                {
                    available = [];
                    _available.Add(allocation.Key, available);
                }

                available.Push(allocation);
                CachedBytes += allocation.Key.Capacity;
            }
        }

        // Destroy outside the lock — _destroy calls into Vulkan which may
        // grab device-level locks, and holding _gate while doing so risks
        // a lock-ordering deadlock with a thread that holds the device lock
        // and is waiting on _gate.
if (toDestroy is { } td)
{
    _destroy(td);

        }

        return true;
    }

    public void Dispose()
    {
        // Snapshot under the lock, destroy outside — _destroy calls into
        // Vulkan which may grab device-level locks; holding _gate while
        // doing so risks a lock-ordering deadlock with any thread that
        // acquires the device lock first and then waits on _gate.
        List<VulkanHostBufferAllocation> toDestroy;
        lock (_gate)
        {
            toDestroy = new List<VulkanHostBufferAllocation>(_allocations.Values);
            _allocations.Clear();
            _available.Clear();
            _cachedHandles.Clear();
            CachedBytes = 0;
        }

        foreach (var allocation in toDestroy)
        {
            _destroy(allocation);
        }
    }
}
