// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Numerics;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial manages pooled host-visible Vulkan buffers.

    private sealed partial class Presenter
    {
        private readonly VulkanHostBufferPool _hostBufferPool;
        private VkBuffer CreateHostBuffer(
            ReadOnlySpan<byte> data,
            BufferUsageFlags usage,
            out DeviceMemory memory,
            out nint mapped)
        {
            var size = (ulong)Math.Max(data.Length, sizeof(uint));
            var capacity = BitOperations.RoundUpToPowerOf2(size);
            var key = new VulkanHostBufferPoolKey(usage, capacity);

            VulkanHostBufferAllocation allocation;
            if (_hostBufferPool.TryRent(key, out var pooled))
            {
                allocation = pooled;
            }
            else
            {
                var buffer = CreateBuffer(
                    capacity,
                    usage,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out var allocatedMemory);
                // Persistently mapped: map/unmap per draw was a measurable
                // share of the per-draw fixed cost, and HOST_COHERENT memory
                // may legally stay mapped for its lifetime.
                void* persistentMapping;
                Check(
                    _vk.MapMemory(_device, allocatedMemory, 0, capacity, 0, &persistentMapping),
                    "vkMapMemory(host persistent)");
                allocation = new VulkanHostBufferAllocation(
                    buffer,
                    allocatedMemory,
                    key,
                    (nint)persistentMapping);
                _hostBufferPool.Register(allocation);
            }

            memory = allocation.Memory;
            mapped = allocation.Mapped;
            fixed (byte* source = data)
            {
                System.Buffer.MemoryCopy(
                    source,
                    (void*)allocation.Mapped,
                    checked((long)allocation.Key.Capacity),
                    data.Length);
            }

            return allocation.Buffer;
        }

        private void RecycleHostBuffer(VkBuffer buffer, DeviceMemory memory)
        {
            if (buffer.Handle == 0)
            {
                return;
            }

            if (_hostBufferPool.Return(buffer, memory))
            {
                return;
            }

            _vk.DestroyBuffer(_device, buffer, null);
            if (memory.Handle != 0)
            {
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private void DestroyHostBufferAllocation(VulkanHostBufferAllocation allocation)
        {
            _vk.UnmapMemory(_device, allocation.Memory);
            _vk.DestroyBuffer(_device, allocation.Buffer, null);
            _vk.FreeMemory(_device, allocation.Memory, null);
        }

    }
}
