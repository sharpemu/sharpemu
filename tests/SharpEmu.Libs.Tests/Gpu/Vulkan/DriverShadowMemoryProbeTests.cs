// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// TEMP DIAG: does the driver commit host write-combined memory for device-local allocations?
public sealed unsafe partial class DriverShadowMemoryProbeTests(HeadlessVulkanFixture fixture, ITestOutputHelper output) : IClassFixture<HeadlessVulkanFixture>
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation information, nuint length);

    private static (int Count, ulong Bytes) WriteCombinedPrivate()
    {
        var count = 0;
        ulong bytes = 0;
        nint address = 0;
        while (VirtualQuery(address, out var info, (nuint)sizeof(MemoryBasicInformation)) != 0)
        {
            if (info.State == 0x1000 && info.Type == 0x20000 && (info.Protect & 0x400) != 0)
            {
                count++;
                bytes += (ulong)info.RegionSize;
            }

            var next = (ulong)info.BaseAddress + (ulong)info.RegionSize;
            if (next <= (ulong)address || next >= 0x7FFF_FFFF_0000) break;
            address = (nint)next;
        }

        return (count, bytes);
    }

    [Theory]
    [InlineData(32u, true)]
    [InlineData(32u, false)]
    [InlineData(256u, true)]
    public void DeviceLocalAllocationsAndWriteCombinedHostMemory(uint megabytes, bool deviceAddress)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        using var harness = new ImageTestHarness(vulkan);
        var before = WriteCombinedPrivate();
        var buffers = new List<GpuBuffer>();
        var flags = deviceAddress ? GpuBuffer.AllFlags : BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit;
        for (var index = 0; index < 16; index++)
        {
            buffers.Add(new GpuBuffer(harness.Device, harness.Scheduler, GpuBufferUsage.DeviceLocal, 0, flags, (ulong)megabytes << 20));
        }

        var after = WriteCombinedPrivate();
        foreach (var buffer in buffers) buffer.Dispose();
        var freed = WriteCombinedPrivate();
        output.WriteLine($"size={megabytes}MB deviceAddress={deviceAddress}: wc before n={before.Count} {before.Bytes >> 20}MB, " +
            $"after 16 allocations n={after.Count} {after.Bytes >> 20}MB, after free n={freed.Count} {freed.Bytes >> 20}MB");
    }
    // A device of its own with the memory-priority and pageable-device-local extensions, then
    // raw device-local allocations with and without them.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PageableAndPriorityDeviceLocalMemory(bool pageable, bool priority)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var vk = vulkan.Vk;
        var queuePriority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = vulkan.QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority,
        };
        var pageableFeatures = new PhysicalDevicePageableDeviceLocalMemoryFeaturesEXT
        {
            SType = StructureType.PhysicalDevicePageableDeviceLocalMemoryFeaturesExt,
            PageableDeviceLocalMemory = pageable,
        };
        var priorityFeatures = new PhysicalDeviceMemoryPriorityFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceMemoryPriorityFeaturesExt,
            MemoryPriority = true,
            PNext = &pageableFeatures,
        };
        var names = new List<string> { "VK_EXT_memory_priority" };
        if (pageable) names.Add("VK_EXT_pageable_device_local_memory");
        var extensions = Silk.NET.Core.Native.SilkMarshal.StringArrayToPtr(names.ToArray());
        var deviceInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &priorityFeatures,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            EnabledExtensionCount = (uint)names.Count,
            PpEnabledExtensionNames = (byte**)extensions,
        };
        Assert.Equal(Result.Success, vk.CreateDevice(vulkan.Physical, &deviceInfo, null, out var device));
        vk.GetPhysicalDeviceMemoryProperties(vulkan.Physical, out var properties);
        uint deviceLocal = 0;
        for (uint type = 0; type < properties.MemoryTypeCount; type++)
        {
            if (properties.MemoryTypes[(int)type].PropertyFlags == MemoryPropertyFlags.DeviceLocalBit) { deviceLocal = type; break; }
        }

        var workingSetBefore = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        var before = WriteCombinedPrivate();
        var memories = new List<DeviceMemory>();
        for (var index = 0; index < 16; index++)
        {
            var priorityInfo = new MemoryPriorityAllocateInfoEXT { SType = StructureType.MemoryPriorityAllocateInfoExt, Priority = 1f };
            var allocate = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = priority ? &priorityInfo : null,
                AllocationSize = 32UL << 20,
                MemoryTypeIndex = deviceLocal,
            };
            Assert.Equal(Result.Success, vk.AllocateMemory(device, &allocate, null, out var memory));
            memories.Add(memory);
        }

        var after = WriteCombinedPrivate();
        var workingSetAfter = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64;
        foreach (var memory in memories) vk.FreeMemory(device, memory, null);
        vk.DestroyDevice(device, null);
        Silk.NET.Core.Native.SilkMarshal.Free(extensions);
        output.WriteLine($"pageable={pageable} priority={priority} type={deviceLocal}: wc +{(after.Bytes - before.Bytes) >> 20}MB committed, working set +{(workingSetAfter - workingSetBefore) >> 20}MB, for 512MB device-local");
    }
}
