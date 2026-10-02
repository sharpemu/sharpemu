// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The device facts the stores need: handles, memory types, limits, format support
// and the count of live device-memory allocations made through this object.
public sealed unsafe class GpuDeviceInfo : IImageFormatSupport, IDeviceMemoryAllocator
{
    private PhysicalDeviceMemoryProperties _memoryProperties;
    // Queried for every draw target from the render thread and from other threads; a hit takes no lock.
    // Two threads may query the same missing key once each; the driver answers identically.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Format, FormatProperties> _formatProperties = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Format, ImageType, ImageTiling, ImageUsageFlags, ImageCreateFlags), (Result Result, ImageFormatProperties Properties)> _imageFormatProperties = new();
    private int _liveAllocations;
    private int _peakAllocations;
    private long _lastFailedAllocationBytes;

    public ulong LastFailedAllocationBytes => (ulong)Interlocked.Read(ref _lastFailedAllocationBytes);

    // VK_EXT_image_view_min_lod is enabled, so a view can clamp to a texture descriptor's MIN_LOD.
    public bool ImageViewMinLodSupported { get; init; }

    public GpuDeviceInfo(Vk vk, PhysicalDevice physicalDevice, Device device, bool memoryBudgetEnabled = false)
    {
        Vk = vk;
        PhysicalDevice = physicalDevice;
        Device = device;
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out _memoryProperties);
        DeviceLocalHeapBytes = CalculateDeviceLocalHeapBytes();
        var queriedMemory = memoryBudgetEnabled
            ? QueryDeviceLocalMemory()
            : (Budget: 0UL, Usage: 0UL, Available: 0UL);
        if (queriedMemory.Budget != 0)
        {
            DeviceLocalBudgetBytes = queriedMemory.Budget;
            DeviceLocalUsageBytes = queriedMemory.Usage;
            DeviceLocalAvailableBytes = queriedMemory.Available;
            HasMemoryBudget = true;
        }
        else
        {
            DeviceLocalBudgetBytes = DeviceLocalHeapBytes;
            DeviceLocalUsageBytes = 0;
            DeviceLocalAvailableBytes = DeviceLocalHeapBytes;
            HasMemoryBudget = false;
        }
        vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
        MinUniformBufferOffsetAlignment = Math.Max(properties.Limits.MinUniformBufferOffsetAlignment, 1);
        MinStorageBufferOffsetAlignment = Math.Max(properties.Limits.MinStorageBufferOffsetAlignment, 1);
        NonCoherentAtomSize = Math.Max(properties.Limits.NonCoherentAtomSize, 1);
        MaxStorageBufferRange = properties.Limits.MaxStorageBufferRange;
        MaxMemoryAllocationCount = properties.Limits.MaxMemoryAllocationCount;
        MaxComputeWorkGroupCount = (properties.Limits.MaxComputeWorkGroupCount[0], properties.Limits.MaxComputeWorkGroupCount[1], properties.Limits.MaxComputeWorkGroupCount[2]);
        Slabs = new GpuMemorySlabs(this);
    }

    // Shared chunks for small buffers; idle chunks can be returned under pressure.
    internal GpuMemorySlabs Slabs { get; }

    public Vk Vk { get; }

    // Queue families that may access every buffer; set before the first buffer is
    // created when a second queue family (the async readback queue) reads them.
    public uint[]? SharedQueueFamilies { get; set; }

    public PhysicalDevice PhysicalDevice { get; }

    public Device Device { get; }

    public ulong DeviceLocalHeapBytes { get; }

    public ulong DeviceLocalBudgetBytes { get; private set; }

    public ulong DeviceLocalUsageBytes { get; private set; }

    public ulong DeviceLocalAvailableBytes { get; private set; }

    public bool HasMemoryBudget { get; }

    public ulong MinUniformBufferOffsetAlignment { get; }

    public ulong MinStorageBufferOffsetAlignment { get; }

    public ulong NonCoherentAtomSize { get; }

    public uint MaxStorageBufferRange { get; }

    public uint MaxMemoryAllocationCount { get; }

    public (uint X, uint Y, uint Z) MaxComputeWorkGroupCount { get; }

    public uint MemoryTypeCount => _memoryProperties.MemoryTypeCount;

    private ulong CalculateDeviceLocalHeapBytes()
    {
        var usedHeaps = new bool[_memoryProperties.MemoryHeapCount];
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            var memoryTypes = &properties->MemoryTypes.Element0;
            for (var index = 0u; index < properties->MemoryTypeCount; index++)
            {
                if ((memoryTypes[index].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                {
                    usedHeaps[(int)memoryTypes[index].HeapIndex] = true;
                }
            }

            var heaps = &properties->MemoryHeaps.Element0;
            ulong total = 0;
            for (var index = 0u; index < properties->MemoryHeapCount; index++)
            {
                if (usedHeaps[(int)index])
                {
                    total += heaps[index].Size;
                }
            }

            return total;
        }
    }

    private (ulong Budget, ulong Usage, ulong Available) QueryDeviceLocalMemory()
    {
        var budget = new PhysicalDeviceMemoryBudgetPropertiesEXT
        {
            SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt,
        };
        var properties = new PhysicalDeviceMemoryProperties2
        {
            SType = StructureType.PhysicalDeviceMemoryProperties2,
            PNext = &budget,
        };
        Vk.GetPhysicalDeviceMemoryProperties2(PhysicalDevice, &properties);

        var usedHeaps = new bool[_memoryProperties.MemoryHeapCount];
        fixed (PhysicalDeviceMemoryProperties* memoryProperties = &_memoryProperties)
        {
            var memoryTypes = &memoryProperties->MemoryTypes.Element0;
            for (var index = 0u; index < memoryProperties->MemoryTypeCount; index++)
            {
                if ((memoryTypes[index].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                {
                    usedHeaps[(int)memoryTypes[index].HeapIndex] = true;
                }
            }
        }

        ulong totalBudget = 0;
        ulong totalUsage = 0;
        ulong totalAvailable = 0;
        for (var index = 0u; index < _memoryProperties.MemoryHeapCount; index++)
        {
            if (usedHeaps[(int)index])
            {
                var heapBudget = budget.HeapBudget[index];
                var heapUsage = budget.HeapUsage[index];
                totalBudget = checked(totalBudget + heapBudget);
                totalUsage = checked(totalUsage + heapUsage);
                totalAvailable = checked(totalAvailable + CalculateAvailableBytes(heapBudget, heapUsage));
            }
        }

        return totalBudget != 0
            ? (totalBudget, totalUsage, totalAvailable)
            : (0, 0, 0);
    }

    internal static ulong CalculateAvailableBytes(ulong budget, ulong usage) =>
        budget > usage ? budget - usage : 0;

    public int LiveAllocations => Volatile.Read(ref _liveAllocations);

    public int PeakAllocations => Volatile.Read(ref _peakAllocations);

    public MemoryPropertyFlags GetMemoryTypeFlags(uint index)
    {
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            return (&properties->MemoryTypes.Element0)[index].PropertyFlags;
        }
    }

    public FormatProperties GetFormatProperties(Format format)
    {
        if (!_formatProperties.TryGetValue(format, out var properties))
        {
            Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out properties);
            _formatProperties[format] = properties;
        }

        return properties;
    }

    public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
    {
        var key = (format, type, tiling, usage, flags);
        if (!_imageFormatProperties.TryGetValue(key, out var entry))
        {
            var result = Vk.GetPhysicalDeviceImageFormatProperties(PhysicalDevice, format, type, tiling, usage, flags, out var found);
            entry = (result, found);
            _imageFormatProperties[key] = entry;
        }

        properties = entry.Properties;
        return entry.Result == Result.Success;
    }

    // Every device-memory allocation goes through here so the live count stays exact.
    public Result AllocateMemory(in MemoryAllocateInfo info, out DeviceMemory memory)
    {
        fixed (MemoryAllocateInfo* pointer = &info)
        {
            var result = Vk.AllocateMemory(Device, pointer, null, out memory);
            if (result != Result.Success)
                Interlocked.Exchange(ref _lastFailedAllocationBytes, checked((long)info.AllocationSize));
            if (result == Result.Success)
            {
                var live = Interlocked.Increment(ref _liveAllocations);
                int peak;
                while ((peak = Volatile.Read(ref _peakAllocations)) < live &&
                       Interlocked.CompareExchange(ref _peakAllocations, live, peak) != peak)
                {
                }
            }

            return result;
        }
    }

    public void FreeMemory(DeviceMemory memory)
    {
        if (memory.Handle == 0)
        {
            return;
        }

        Vk.FreeMemory(Device, memory, null);
        Interlocked.Decrement(ref _liveAllocations);
    }
}
