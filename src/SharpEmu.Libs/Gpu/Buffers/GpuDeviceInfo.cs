// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The device facts the stores need: handles, memory types, limits, format support
// and the count of live device-memory allocations made through this object.
public sealed unsafe class GpuDeviceInfo : IImageFormatSupport
{
    private PhysicalDeviceMemoryProperties _memoryProperties;
    // Queried for every draw target from the render thread and from other threads; a hit takes no lock.
    // Two threads may query the same missing key once each; the driver answers identically.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Format, FormatProperties> _formatProperties = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Format, ImageType, ImageTiling, ImageUsageFlags, ImageCreateFlags), (Result Result, ImageFormatProperties Properties)> _imageFormatProperties = new();
    private int _liveAllocations;
    private int _peakAllocations;

    // VK_EXT_image_view_min_lod is enabled, so a view can clamp to a texture descriptor's MIN_LOD.
    public bool ImageViewMinLodSupported { get; init; }

    public GpuDeviceInfo(Vk vk, PhysicalDevice physicalDevice, Device device)
    {
        Vk = vk;
        PhysicalDevice = physicalDevice;
        Device = device;
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out _memoryProperties);
        vk.GetPhysicalDeviceProperties(physicalDevice, out var properties);
        MinUniformBufferOffsetAlignment = Math.Max(properties.Limits.MinUniformBufferOffsetAlignment, 1);
        MinStorageBufferOffsetAlignment = Math.Max(properties.Limits.MinStorageBufferOffsetAlignment, 1);
        NonCoherentAtomSize = Math.Max(properties.Limits.NonCoherentAtomSize, 1);
        MaxStorageBufferRange = properties.Limits.MaxStorageBufferRange;
        MaxMemoryAllocationCount = properties.Limits.MaxMemoryAllocationCount;
        MaxComputeWorkGroupCount = (properties.Limits.MaxComputeWorkGroupCount[0], properties.Limits.MaxComputeWorkGroupCount[1], properties.Limits.MaxComputeWorkGroupCount[2]);
        Slabs = new GpuMemorySlabs(this);
        MappedDeviceLocalBudget = PlanMappedDeviceLocalBudget();
    }

    private const ulong MiB = 1024 * 1024;
    private const ulong ResizableBarHeapBytes = 2048 * MiB;
    private const ulong MaxMappedDeviceLocalBudget = 1024 * MiB;
    private const ulong SmallBarReserve = 32 * MiB;
    private static readonly SharpEmu.Logging.SharpEmuLogger Log = SharpEmu.Logging.SharpEmuLog.For("GPU");
    private static int _loggedMappedDeviceLocalBudget;

    public ulong MappedDeviceLocalBudget { get; }

    public bool ResizableBar { get; private set; }

    public uint MemoryHeapCount => _memoryProperties.MemoryHeapCount;

    public uint GetMemoryHeapIndex(uint memoryType)
    {
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            return (&properties->MemoryTypes.Element0)[memoryType].HeapIndex;
        }
    }

    public ulong GetMemoryHeapSize(uint heap)
    {
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            return (&properties->MemoryHeaps.Element0)[heap].Size;
        }
    }

    public bool IsDeviceLocalHeap(uint heap)
    {
        fixed (PhysicalDeviceMemoryProperties* properties = &_memoryProperties)
        {
            return ((&properties->MemoryHeaps.Element0)[heap].Flags & MemoryHeapFlags.DeviceLocalBit) != 0;
        }
    }

    private ulong PlanMappedDeviceLocalBudget()
    {
        var mappedTypes = GpuBuffer.RankMemoryTypes(this, uint.MaxValue, GpuBufferUsage.MappedDeviceLocal).ToArray();
        var largestDeviceLocalHeap = 0UL;
        for (uint heap = 0; heap < MemoryHeapCount; heap++)
        {
            if (IsDeviceLocalHeap(heap))
            {
                largestDeviceLocalHeap = Math.Max(largestDeviceLocalHeap, GetMemoryHeapSize(heap));
            }
        }

        if (mappedTypes.Length == 0)
        {
            NoteMappedDeviceLocalBudget(FormattableString.Invariant($"Mapped device-local buffers are unavailable: no memory type is both device-local and host-visible (largest device-local heap {largestDeviceLocalHeap / MiB} MiB)"));
            return 0;
        }

        var memoryType = mappedTypes[0];
        var mappedHeap = GetMemoryHeapIndex(memoryType);
        var heapSize = GetMemoryHeapSize(mappedHeap);
        ResizableBar = heapSize >= largestDeviceLocalHeap / 2 || heapSize >= ResizableBarHeapBytes;
        var budget = ResizableBar
            ? Math.Min(heapSize / 4, MaxMappedDeviceLocalBudget)
            : heapSize / 4 > SmallBarReserve ? heapSize / 4 - SmallBarReserve : 0;
        NoteMappedDeviceLocalBudget(FormattableString.Invariant($"Mapped device-local buffers use memory type {memoryType} ({GetMemoryTypeFlags(memoryType)}) on heap {mappedHeap} of {heapSize / MiB} MiB (largest device-local heap {largestDeviceLocalHeap / MiB} MiB, resizable BAR {ResizableBar}): budget {budget / MiB} MiB"));
        return budget;
    }

    private static void NoteMappedDeviceLocalBudget(string message)
    {
        if (Interlocked.Exchange(ref _loggedMappedDeviceLocalBudget, 1) == 0)
        {
            Log.Info(message);
        }
    }

    // Shared chunks the small buffers are carved from; freed at device teardown.
    internal GpuMemorySlabs Slabs { get; }

    public Vk Vk { get; }

    // Queue families that may access every buffer; set before the first buffer is
    // created when a second queue family (the async readback queue) reads them.
    public uint[]? SharedQueueFamilies { get; set; }

    public PhysicalDevice PhysicalDevice { get; }

    public Device Device { get; }

    public ulong MinUniformBufferOffsetAlignment { get; }

    public ulong MinStorageBufferOffsetAlignment { get; }

    public ulong NonCoherentAtomSize { get; }

    public uint MaxStorageBufferRange { get; }

    public uint MaxMemoryAllocationCount { get; }

    public (uint X, uint Y, uint Z) MaxComputeWorkGroupCount { get; }

    public uint MemoryTypeCount => _memoryProperties.MemoryTypeCount;

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
