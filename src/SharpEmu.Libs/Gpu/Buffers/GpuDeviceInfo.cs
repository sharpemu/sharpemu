// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Buffers;

// The device facts the stores need: handles, memory types, limits, format support
// and the count of live device-memory allocations made through this object.
public sealed unsafe class GpuDeviceInfo
{
    private PhysicalDeviceMemoryProperties _memoryProperties;
    private readonly Dictionary<Format, FormatProperties> _formatProperties = new();
    private readonly Dictionary<(Format, ImageType, ImageTiling, ImageUsageFlags, ImageCreateFlags), (Result Result, ImageFormatProperties Properties)> _imageFormatProperties = new();
    private readonly object _gate = new();
    private int _liveAllocations;
    private int _peakAllocations;

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
    }

    public Vk Vk { get; }

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
        lock (_gate)
        {
            if (!_formatProperties.TryGetValue(format, out var properties))
            {
                Vk.GetPhysicalDeviceFormatProperties(PhysicalDevice, format, out properties);
                _formatProperties[format] = properties;
            }

            return properties;
        }
    }

    public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
    {
        lock (_gate)
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
