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

    public void Register(VulkanHostBufferAllocation allocation)
    {
        if (allocation.Buffer.Handle == 0)
        {
            throw new ArgumentException("A pooled buffer must have a valid handle.", nameof(allocation));
        }

        _allocations.Add(allocation.Buffer.Handle, allocation);
    }

    public bool Return(VkBuffer buffer, DeviceMemory memory)
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
            _destroy(allocation);
            return true;
        }

        if (!_available.TryGetValue(allocation.Key, out var available))
        {
            available = [];
            _available.Add(allocation.Key, available);
        }

        available.Push(allocation);
        CachedBytes += allocation.Key.Capacity;
        return true;
    }

    public void Dispose()
    {
        foreach (var allocation in _allocations.Values)
        {
            _destroy(allocation);
        }

        _allocations.Clear();
        _available.Clear();
        _cachedHandles.Clear();
        CachedBytes = 0;
    }
}
