// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.Gpu.Buffers;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanHostBufferPoolKey(
    BufferUsageFlags Usage,
    ulong Capacity);

internal readonly record struct VulkanHostBufferAllocation(
    VkBuffer Buffer,
    DeviceMemory Memory,
    VulkanHostBufferPoolKey Key,
    nint Mapped,
    GpuResidency.Entry? Residency = null);

internal sealed class VulkanHostBufferPool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<VulkanHostBufferPoolKey, List<VulkanHostBufferAllocation>>
        _available = [];
    private readonly Dictionary<ulong, long> _idleSpills = [];
    internal const long SpillIdleMilliseconds = 5_000;
    private long _nextTrim;
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
            if (!_available.TryGetValue(key, out var available) || available.Count == 0)
            {
                allocation = default;
                return false;
            }

            allocation = available[^1];
            allocation.Residency?.Use(GpuResidency.PresenterBufferRole(key.Usage));
            available.RemoveAt(available.Count - 1);
            _cachedHandles.Remove(allocation.Buffer.Handle);
            _idleSpills.Remove(allocation.Buffer.Handle);
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

    public bool Return(VkBuffer buffer, DeviceMemory memory, long? spillReturnedAt = null)
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

                allocation.Residency?.Use(GpuResidency.Role.IdleBuffer);
                available.Add(allocation);
                if (spillReturnedAt.HasValue) _idleSpills.Add(buffer.Handle, spillReturnedAt.Value);
                CachedBytes += allocation.Key.Capacity;
            }
        }

        // Destroy outside the lock — _destroy calls into Vulkan which may
        // grab device-level locks, and holding _gate while doing so risks
        // a lock-ordering deadlock with a thread that holds the device lock
        // and is waiting on _gate.
        if (toDestroy is { } td) _destroy(td);

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
            _idleSpills.Clear();
            CachedBytes = 0;
        }

        foreach (var allocation in toDestroy)
        {
            _destroy(allocation);
        }
    }

    // Called by the render loop. Only completed or abandoned preparations have returned their spills.
    public void TrimIdleSpills(long now)
    {
        if (now < _nextTrim) return;
        _nextTrim = now + SpillIdleMilliseconds;
        List<VulkanHostBufferAllocation>? expired = null;
        lock (_gate)
        {
            foreach (var (handle, returnedAt) in _idleSpills)
                if (now - returnedAt >= SpillIdleMilliseconds)
                    (expired ??= []).Add(_allocations[handle]);
            if (expired is null) return;
            foreach (var allocation in expired)
            {
                _idleSpills.Remove(allocation.Buffer.Handle);
                _cachedHandles.Remove(allocation.Buffer.Handle);
                _allocations.Remove(allocation.Buffer.Handle);
                CachedBytes -= allocation.Key.Capacity;
            }
            foreach (var key in expired.Select(allocation => allocation.Key).Distinct())
                _available[key].RemoveAll(allocation => !_cachedHandles.Contains(allocation.Buffer.Handle));
        }
        foreach (var allocation in expired)
        {
            _destroy(allocation);
        }
    }
}
