// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanHostBufferPoolTests
{
    [Fact]
    public void OnlyCompletedSpillsThatStayedIdleExpire()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        using var pool = new VulkanHostBufferPool(4096, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 256);
        var regular = Allocation(1, 2, key);
        var idle = Allocation(3, 4, key);
        var rented = Allocation(5, 6, key);
        var pending = Allocation(7, 8, key);
        foreach (var allocation in new[] { regular, idle, rented, pending }) pool.Register(allocation);
        Assert.True(pool.Return(regular.Buffer, regular.Memory));
        Assert.True(pool.Return(idle.Buffer, idle.Memory, 0));
        Assert.True(pool.Return(rented.Buffer, rented.Memory, 0));
        Assert.True(pool.TryRent(key, out var active));
        Assert.Equal(rented, active);
        pool.TrimIdleSpills(0);
        Assert.Empty(destroyed);
        pool.TrimIdleSpills(VulkanHostBufferPool.SpillIdleMilliseconds);

        Assert.Equal(new[] { idle }, destroyed);
        Assert.Equal(256UL, pool.CachedBytes);
        Assert.True(pool.TryRent(key, out var remaining));
        Assert.Equal(regular, remaining);
    }

    [Fact]
    public void ReturnedAllocationCanBeRentedAgain()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        using var pool = new VulkanHostBufferPool(1024, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 256);
        var allocation = Allocation(1, 2, key);

        pool.Register(allocation);
        Assert.True(pool.Return(allocation.Buffer, allocation.Memory));
        Assert.Equal(256UL, pool.CachedBytes);

        Assert.True(pool.TryRent(key, out var rented));
        Assert.Equal(allocation, rented);
        Assert.Equal(0UL, pool.CachedBytes);
        Assert.Empty(destroyed);
    }

    [Fact]
    public void ReturnDestroysAllocationThatWouldExceedBudget()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        using var pool = new VulkanHostBufferPool(256, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.VertexBufferBit, 512);
        var allocation = Allocation(3, 4, key);

        pool.Register(allocation);

        Assert.True(pool.Return(allocation.Buffer, allocation.Memory));
        Assert.Equal(0UL, pool.CachedBytes);
        Assert.Equal([allocation], destroyed);
        Assert.False(pool.TryRent(key, out _));
    }

    [Fact]
    public void UnknownAllocationIsNotClaimedByPool()
    {
        using var pool = new VulkanHostBufferPool(1024, _ => { });

        Assert.False(pool.Return(new VkBuffer(9), new DeviceMemory(10)));
    }

    private static VulkanHostBufferAllocation Allocation(
        ulong buffer,
        ulong memory,
        VulkanHostBufferPoolKey key) =>
        new(new VkBuffer(buffer), new DeviceMemory(memory), key, 0);
}
