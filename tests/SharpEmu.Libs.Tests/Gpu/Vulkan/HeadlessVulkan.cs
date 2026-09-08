// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Xunit;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A window-less Vulkan 1.2 device with timeline semaphores and buffer device addresses; null when the host has none.
internal sealed unsafe class HeadlessVulkan : IDisposable
{
    // Set SHARPEMU_TEST_VK_VALIDATION=1 to load the validation layer and print its messages.
    private const string ValidationVariable = "SHARPEMU_TEST_VK_VALIDATION";
    private static readonly PfnDebugUtilsMessengerCallbackEXT DebugCallbackPointer = new(DebugCallback);
    private static readonly List<string> ValidationMessages = new();

    private GpuDeviceInfo? _deviceInfo;
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;

    private HeadlessVulkan(Vk vk, Instance instance, PhysicalDevice physical, Device device, Queue queue, uint queueFamily, uint apiVersion, in PhysicalDeviceFeatures features)
    {
        Vk = vk;
        Instance = instance;
        Physical = physical;
        Device = device;
        Queue = queue;
        QueueFamily = queueFamily;
        ApiVersion = apiVersion;
        SampleRateShading = features.SampleRateShading;
        SamplerAnisotropy = features.SamplerAnisotropy;
    }

    public Vk Vk { get; }

    public Instance Instance { get; }

    public PhysicalDevice Physical { get; }

    public Device Device { get; }

    public Queue Queue { get; }

    public uint QueueFamily { get; }

    public object QueueGate { get; } = new();

    // The device API version: 1.3 when the loader and the device support it, else 1.2.
    public uint ApiVersion { get; }

    // SPIR-V 1.6 modules (the compiled reference blobs) need a Vulkan 1.3 device.
    public bool SupportsSpirv16 => ApiVersion >= Vk.Version13;

    // True when the device was created with the sample-rate-shading feature the blit needs.
    public bool SampleRateShading { get; }

    public bool SamplerAnisotropy { get; }

    public bool ValidationEnabled => _debugUtils is not null;

    // The validation messages collected since the last call; empty when the layer is off.
    public string[] TakeValidationMessages()
    {
        lock (ValidationMessages)
        {
            var messages = ValidationMessages.ToArray();
            ValidationMessages.Clear();
            return messages;
        }
    }

    public void AssertNoValidationMessages()
    {
        var messages = TakeValidationMessages();
        Assert.True(messages.Length == 0, "Validation layer messages:" + Environment.NewLine + string.Join(Environment.NewLine, messages));
    }

    // One shared instance so the live allocation counter spans every buffer and image of a test.
    public GpuDeviceInfo DeviceInfo => _deviceInfo ??= new GpuDeviceInfo(Vk, Physical, Device);

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
        if (_debugUtils is { } debugUtils)
        {
            debugUtils.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);
        }

        Vk.DestroyInstance(Instance, null);
    }

    private static uint DebugCallback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type, DebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        var message = $"[VULKAN][{severity}] {SilkMarshal.PtrToString((nint)data->PMessage)}";
        Console.Error.WriteLine(message);
        lock (ValidationMessages)
        {
            ValidationMessages.Add(message);
        }

        return 0;
    }

    private void RegisterDebugMessenger()
    {
        if (!Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
        {
            return;
        }

        var info = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | DebugUtilsMessageTypeFlagsEXT.GeneralBitExt,
            PfnUserCallback = DebugCallbackPointer,
        };
        if (debugUtils.CreateDebugUtilsMessenger(Instance, &info, null, out _debugMessenger) == Result.Success)
        {
            _debugUtils = debugUtils;
        }
    }

    private static HeadlessVulkan? Create()
    {
        var vk = Vk.GetApi();
        uint instanceVersion = Vk.Version10;
        vk.EnumerateInstanceVersion(ref instanceVersion);
        var apiVersion = instanceVersion >= Vk.Version13 ? Vk.Version13 : Vk.Version12;
        var appInfo = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = apiVersion };
        var validation = Environment.GetEnvironmentVariable(ValidationVariable) == "1";
        var layers = validation ? SilkMarshal.StringArrayToPtr(new[] { "VK_LAYER_KHRONOS_validation" }) : 0;
        var extensions = validation ? SilkMarshal.StringArrayToPtr(new[] { ExtDebugUtils.ExtensionName }) : 0;
        var instanceInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledLayerCount = validation ? 1u : 0u,
            PpEnabledLayerNames = (byte**)layers,
            EnabledExtensionCount = validation ? 1u : 0u,
            PpEnabledExtensionNames = (byte**)extensions,
        };
        var created = vk.CreateInstance(&instanceInfo, null, out var instance);
        if (validation)
        {
            SilkMarshal.Free(layers);
            SilkMarshal.Free(extensions);
        }

        if (created != Result.Success)
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

        vk.GetPhysicalDeviceProperties(physical, out var physicalProperties);
        if (physicalProperties.ApiVersion < apiVersion)
        {
            apiVersion = Vk.Version12;
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

        vk.GetPhysicalDeviceFeatures(physical, out var baseFeatures);
        var enabledFeatures = new PhysicalDeviceFeatures
        {
            SampleRateShading = baseFeatures.SampleRateShading,
            SamplerAnisotropy = baseFeatures.SamplerAnisotropy,
        };
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
            PEnabledFeatures = &enabledFeatures,
        };
        if (vk.CreateDevice(physical, &deviceInfo, null, out var device) != Result.Success)
        {
            vk.DestroyInstance(instance, null);
            return null;
        }

        vk.GetDeviceQueue(device, family, 0, out var queue);
        var result = new HeadlessVulkan(vk, instance, physical, device, queue, family, apiVersion, enabledFeatures);
        if (validation)
        {
            result.RegisterDebugMessenger();
        }

        return result;
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
