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

    // The size of the latest device-memory allocation the driver refused, for failure reports.
    public ulong LastFailedAllocationBytes => (ulong)Interlocked.Read(ref _lastFailedAllocationBytes);

    // Tests make an allocation of the given size fail as an exhausted device would.
    internal Func<ulong, bool>? AllocationFailure { get; set; }

    // VK_EXT_image_view_min_lod is enabled, so a view can clamp to a texture descriptor's MIN_LOD.
    public bool ImageViewMinLodSupported { get; init; }
    // VK_EXT_sampler_filter_minmax is enabled, so a sampler can reduce with min or max.
    public bool SamplerFilterMinmaxSupported { get; init; }

    // `memoryBudgetEnabled`: VK_EXT_memory_budget is enabled on the device, so the driver reports
    // how much device-local memory this process can still use.
    public GpuDeviceInfo(Vk vk, PhysicalDevice physicalDevice, Device device, bool memoryBudgetEnabled = false)
    {
        Vk = vk;
        PhysicalDevice = physicalDevice;
        Device = device;
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out _memoryProperties);
        var deviceLocalHeaps = DeviceLocalHeaps();
        DeviceLocalHeapBytes = SumHeapBytes(deviceLocalHeaps);
        var queried = memoryBudgetEnabled ? QueryDeviceLocalMemory(deviceLocalHeaps) : default;
        HasMemoryBudget = queried.Budget != 0;
        DeviceLocalBudgetBytes = HasMemoryBudget ? queried.Budget : DeviceLocalHeapBytes;
        DeviceLocalUsageBytes = HasMemoryBudget ? queried.Usage : 0;
        DeviceLocalAvailableBytes = HasMemoryBudget ? queried.Available : DeviceLocalHeapBytes;
        vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
        MinUniformBufferOffsetAlignment = Math.Max(properties.Limits.MinUniformBufferOffsetAlignment, 1);
        MinStorageBufferOffsetAlignment = Math.Max(properties.Limits.MinStorageBufferOffsetAlignment, 1);
        NonCoherentAtomSize = Math.Max(properties.Limits.NonCoherentAtomSize, 1);
        MaxStorageBufferRange = properties.Limits.MaxStorageBufferRange;
        MaxMemoryAllocationCount = properties.Limits.MaxMemoryAllocationCount;
        MaxComputeWorkGroupCount = (properties.Limits.MaxComputeWorkGroupCount[0], properties.Limits.MaxComputeWorkGroupCount[1], properties.Limits.MaxComputeWorkGroupCount[2]);
        Slabs = new GpuMemorySlabs(this);
        Images.GuestPixelFormats.UseNativeNarrowSrgb(
            SupportsRenderTarget(Format.R8Srgb) && SupportsRenderTarget(Format.R8G8Srgb));
    }

    // Colour attachments that blend, sample with filtering and copy both ways: what a guest
    // render target that is later sampled and uploaded or read back needs.
    private bool SupportsRenderTarget(Format format)
    {
        const FormatFeatureFlags Required =
            FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.ColorAttachmentBlendBit |
            FormatFeatureFlags.SampledImageBit | FormatFeatureFlags.SampledImageFilterLinearBit |
            FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit;
        return (GetFormatProperties(format).OptimalTilingFeatures & Required) == Required;
    }

    // Shared chunks the small buffers are carved from; freed at device teardown.
    internal GpuMemorySlabs Slabs { get; }

    public Vk Vk { get; }

    // Queue families that may access every buffer; set before the first buffer is
    // created when a second queue family (the async readback queue) reads them.
    public uint[]? SharedQueueFamilies { get; set; }

    public PhysicalDevice PhysicalDevice { get; }

    public Device Device { get; }

    // The device-local heaps' total size, and what the driver reports of this process's share
    // of them at device creation (VK_EXT_memory_budget). Without the extension the budget is
    // the heap size, which says nothing about memory other processes already hold.
    public ulong DeviceLocalHeapBytes { get; }

    public ulong DeviceLocalBudgetBytes { get; }

    public ulong DeviceLocalUsageBytes { get; }

    public ulong DeviceLocalAvailableBytes { get; }

    public bool HasMemoryBudget { get; }

    public ulong MinUniformBufferOffsetAlignment { get; }

    public ulong MinStorageBufferOffsetAlignment { get; }

    public ulong NonCoherentAtomSize { get; }

    public uint MaxStorageBufferRange { get; }

    public uint MaxMemoryAllocationCount { get; }

    public (uint X, uint Y, uint Z) MaxComputeWorkGroupCount { get; }

    public uint MemoryTypeCount => _memoryProperties.MemoryTypeCount;

    private bool[] DeviceLocalHeaps()
    {
        var heaps = new bool[_memoryProperties.MemoryHeapCount];
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            var memoryTypes = &properties->MemoryTypes.Element0;
            for (var index = 0u; index < properties->MemoryTypeCount; index++)
            {
                if ((memoryTypes[index].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                {
                    heaps[(int)memoryTypes[index].HeapIndex] = true;
                }
            }
        }

        return heaps;
    }

    private ulong SumHeapBytes(bool[] heaps)
    {
        ulong total = 0;
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            var memoryHeaps = &properties->MemoryHeaps.Element0;
            for (var index = 0; index < heaps.Length; index++)
            {
                if (heaps[index])
                {
                    total += memoryHeaps[index].Size;
                }
            }
        }

        return total;
    }

    private (ulong Budget, ulong Usage, ulong Available) QueryDeviceLocalMemory(bool[] heaps)
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
        ulong totalBudget = 0, totalUsage = 0, totalAvailable = 0;
        for (var index = 0; index < heaps.Length; index++)
        {
            if (heaps[index])
            {
                totalBudget += budget.HeapBudget[index];
                totalUsage += budget.HeapUsage[index];
                totalAvailable += CalculateAvailableBytes(budget.HeapBudget[index], budget.HeapUsage[index]);
            }
        }

        return (totalBudget, totalUsage, totalAvailable);
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
            memory = default;
            var result = AllocationFailure?.Invoke(info.AllocationSize) == true
                ? Result.ErrorOutOfDeviceMemory
                : Vk.AllocateMemory(Device, pointer, null, out memory);
            if (result != Result.Success)
            {
                Interlocked.Exchange(ref _lastFailedAllocationBytes, checked((long)info.AllocationSize));
            }
            else
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
