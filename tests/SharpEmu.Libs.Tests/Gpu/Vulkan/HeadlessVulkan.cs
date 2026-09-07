// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A window-less Vulkan 1.2 device with timeline semaphores and buffer device addresses; null when the host has none.
internal sealed unsafe class HeadlessVulkan : IDisposable
{
    private HeadlessVulkan(Vk vk, Instance instance, PhysicalDevice physical, Device device, Queue queue, uint queueFamily)
    {
        Vk = vk;
        Instance = instance;
        Physical = physical;
        Device = device;
        Queue = queue;
        QueueFamily = queueFamily;
    }

    public Vk Vk { get; }

    public Instance Instance { get; }

    public PhysicalDevice Physical { get; }

    public Device Device { get; }

    public Queue Queue { get; }

    public uint QueueFamily { get; }

    public object QueueGate { get; } = new();

    public GpuDeviceInfo DeviceInfo => new(Vk, Physical, Device);

    public string DeviceName
    {
        get
        {
            Vk.GetPhysicalDeviceProperties(Physical, out var properties);
            return Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown";
        }
    }

    public static HeadlessVulkan? TryCreate()
    {
        try
        {
            return Create();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public VulkanTickDevice NewTickDevice() => new(Vk, Device, Queue, QueueFamily, QueueGate);

    public ulong CreateTimelineSemaphore()
    {
        var typeInfo = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var createInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &typeInfo };
        Require(Vk.CreateSemaphore(Device, &createInfo, null, out VkSemaphore semaphore), "vkCreateSemaphore");
        return semaphore.Handle;
    }

    public ulong ReadSemaphore(ulong handle)
    {
        ulong value;
        Require(Vk.GetSemaphoreCounterValue(Device, new VkSemaphore(handle), &value), "vkGetSemaphoreCounterValue");
        return value;
    }

    public void DestroySemaphore(ulong handle) => Vk.DestroySemaphore(Device, new VkSemaphore(handle), null);

    public void Dispose()
    {
        Vk.DeviceWaitIdle(Device);
        Vk.DestroyDevice(Device, null);
        Vk.DestroyInstance(Instance, null);
    }

    private static HeadlessVulkan? Create()
    {
        var vk = Vk.GetApi();
        var appInfo = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version12 };
        var instanceInfo = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &appInfo };
        if (vk.CreateInstance(&instanceInfo, null, out var instance) != Result.Success)
        {
            return null;
        }

        uint deviceCount = 0;
        vk.EnumeratePhysicalDevices(instance, &deviceCount, null);
        if (deviceCount == 0)
        {
            vk.DestroyInstance(instance, null);
            return null;
        }

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pointer = devices)
        {
            vk.EnumeratePhysicalDevices(instance, &deviceCount, pointer);
        }

        var physical = devices[0];
        foreach (var candidate in devices)
        {
            vk.GetPhysicalDeviceProperties(candidate, out var properties);
            if (properties.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                physical = candidate;
                break;
            }
        }

        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, null);
        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pointer = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, pointer);
        }

        var family = uint.MaxValue;
        for (uint index = 0; index < familyCount; index++)
        {
            if ((families[index].QueueFlags & (QueueFlags.GraphicsBit | QueueFlags.ComputeBit)) ==
                (QueueFlags.GraphicsBit | QueueFlags.ComputeBit))
            {
                family = index;
                break;
            }
        }

        var addressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
        };
        var timelineFeatures = new PhysicalDeviceTimelineSemaphoreFeatures
        {
            SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
            PNext = &addressFeatures,
        };
        var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &timelineFeatures };
        vk.GetPhysicalDeviceFeatures2(physical, &features);
        if (family == uint.MaxValue || !timelineFeatures.TimelineSemaphore || !addressFeatures.BufferDeviceAddress)
        {
            vk.DestroyInstance(instance, null);
            return null;
        }

        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = family,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        timelineFeatures.TimelineSemaphore = true;
        addressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            BufferDeviceAddress = true,
        };
        timelineFeatures.PNext = &addressFeatures;
        var deviceInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &timelineFeatures,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
        };
        if (vk.CreateDevice(physical, &deviceInfo, null, out var device) != Result.Success)
        {
            vk.DestroyInstance(instance, null);
            return null;
        }

        vk.GetDeviceQueue(device, family, 0, out var queue);
        return new HeadlessVulkan(vk, instance, physical, device, queue, family);
    }

    private static void Require(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed with {result}");
        }
    }
}

public sealed class HeadlessVulkanFixture : IDisposable
{
    internal HeadlessVulkan? Vulkan { get; } = HeadlessVulkan.TryCreate();

    public void Dispose() => Vulkan?.Dispose();
}

// Records the scheduler's device calls around a real device so tests can see waits and submits.
internal sealed class LoggingTickDevice : IGpuTickDevice
{
    private readonly IGpuTickDevice _inner;
    private readonly List<string> _log = new();

    public LoggingTickDevice(IGpuTickDevice inner) => _inner = inner;

    public object QueueGate => _inner.QueueGate;

    public ulong TimelineHandle => _inner.TimelineHandle;

    public string[] Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToArray();
            }
        }
    }

    public ulong ReadTimeline() => _inner.ReadTimeline();

    public bool TryWaitTimeline(ulong tick, out string failure)
    {
        Note($"wait {tick}");
        return _inner.TryWaitTimeline(tick, out failure);
    }

    public nint[] AllocateBuffers(int count) => _inner.AllocateBuffers(count);

    public void BeginBuffer(nint buffer) => _inner.BeginBuffer(buffer);

    public void EndBuffer(nint buffer) => _inner.EndBuffer(buffer);

    public bool TrySubmit(nint buffer, SubmitBundle bundle, out string failure)
    {
        for (var i = 0; i < bundle.SignalCount; i++)
        {
            if (bundle.SignalSemaphores[i] == TimelineHandle)
            {
                Note($"submit {bundle.SignalTicks[i]}");
            }
        }

        return _inner.TrySubmit(buffer, bundle, out failure);
    }

    public void Dispose() => _inner.Dispose();

    private void Note(string entry)
    {
        lock (_log)
        {
            _log.Add(entry);
        }
    }
}
