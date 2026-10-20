// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Pipelines;
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

    private HeadlessVulkan(Vk vk, Instance instance, PhysicalDevice physical, Device device, Queue queue, uint queueFamily, uint apiVersion, in PhysicalDeviceFeatures features, bool dynamicRendering)
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
        StorageImageExtendedFormats = features.ShaderStorageImageExtendedFormats;
        ShaderInt64 = features.ShaderInt64;
        SupportsClipDistance = features.ShaderClipDistance;
        SupportsDynamicRendering = dynamicRendering;
    }

    // Dynamic rendering support required by the presenter's render host.
    public bool SupportsDynamicRendering { get; }
    public bool SupportsClipDistance { get; }
    public bool SupportsFragmentShaderBarycentric { get; private init; }
    public bool SupportsTessellationShader { get; private init; }
    public bool SupportsFillRectangle { get; private init; }
    public bool SupportsMeshShaders { get; private init; }

    // shaderFloat16 is enabled and the device rounds f16 to nearest even and preserves denormals.
    public bool ExactFloat16Conversion { get; private init; }
    public bool CanRequireComputeSubgroup32 { get; private init; }
    public uint SubgroupSize
    {
        get
        {
            var subgroup = new PhysicalDeviceSubgroupProperties { SType = StructureType.PhysicalDeviceSubgroupProperties };
            var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &subgroup };
            Vk.GetPhysicalDeviceProperties2(Physical, &properties);
            return subgroup.SubgroupSize;
        }
    }

    // The device was created with the descriptor indexing and null descriptor features the runtime
    // descriptor heap needs (see RuntimeDescriptorHeap).
    public bool SupportsRuntimeDescriptors { get; private init; }

    public RuntimeDescriptorHeap.Limits RuntimeDescriptorLimits
    {
        get
        {
            var indexing = new PhysicalDeviceDescriptorIndexingProperties { SType = StructureType.PhysicalDeviceDescriptorIndexingProperties };
            var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &indexing };
            Vk.GetPhysicalDeviceProperties2(Physical, &properties2);
            var limits = properties2.Properties.Limits;
            return new RuntimeDescriptorHeap.Limits(
                limits.MaxPerStageDescriptorSampledImages,
                limits.MaxPerStageDescriptorSamplers,
                indexing.MaxPerStageDescriptorUpdateAfterBindSampledImages,
                indexing.MaxPerStageDescriptorUpdateAfterBindSamplers,
                indexing.MaxDescriptorSetUpdateAfterBindSampledImages,
                indexing.MaxDescriptorSetUpdateAfterBindSamplers,
                indexing.MaxUpdateAfterBindDescriptorsInAllPools);
        }
    }

    private static readonly string[] RenderingExtensionNames =
    [
        "VK_KHR_push_descriptor",
    ];

    // The push descriptor limit the render host may use on this device (none on MoltenVK).
    public uint MaxPushDescriptors
    {
        get
        {
            var pushDescriptors = new PhysicalDevicePushDescriptorPropertiesKHR { SType = StructureType.PhysicalDevicePushDescriptorPropertiesKhr };
            var properties = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &pushDescriptors };
            Vk.GetPhysicalDeviceProperties2(Physical, &properties);
            return SharpEmu.Libs.Gpu.Vulkan.VulkanPushDescriptorPolicy.UsableCount(Vk, Physical, pushDescriptors.MaxPushDescriptors);
        }
    }

    public Vk Vk { get; }

    public Instance Instance { get; }

    public PhysicalDevice Physical { get; }

    public Device Device { get; }

    public Queue Queue { get; }

    public uint QueueFamily { get; }

    public object QueueGate { get; } = new();

    // The device API version. The production renderer requires Vulkan 1.3.
    public uint ApiVersion { get; }

    // SPIR-V 1.6 modules (the compiled reference blobs) need a Vulkan 1.3 device.
    public bool SupportsSpirv16 => ApiVersion >= Vk.Version13;

    // True when the device was created with the sample-rate-shading feature the blit needs.
    public bool SampleRateShading { get; }

    public bool SamplerAnisotropy { get; }

    public bool StorageImageExtendedFormats { get; }

    public bool ShaderInt64 { get; }

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
        _deviceInfo?.Slabs.Destroy();
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
        if (instanceVersion < Vk.Version13)
        {
            return null;
        }
        var apiVersion = Vk.Version13;
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
        if (physicalProperties.ApiVersion < Vk.Version13)
        {
            vk.DestroyInstance(instance, null);
            return null;
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

        const string barycentricExtension = "VK_KHR_fragment_shader_barycentric";
        var barycentricFeatures = new PhysicalDeviceFragmentShaderBarycentricFeaturesKHR
        {
            SType = StructureType.PhysicalDeviceFragmentShaderBarycentricFeaturesKhr,
        };
        if (HasDeviceExtensions(vk, physical, [barycentricExtension]))
        {
            var query = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &barycentricFeatures,
            };
            vk.GetPhysicalDeviceFeatures2(physical, &query);
        }
        var barycentric = (bool)barycentricFeatures.FragmentShaderBarycentric;
        const string meshExtension = "VK_EXT_mesh_shader";
        var meshFeatures = new PhysicalDeviceMeshShaderFeaturesEXT { SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt };
        if (HasDeviceExtensions(vk, physical, [meshExtension]))
        {
            var query = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &meshFeatures };
            vk.GetPhysicalDeviceFeatures2(physical, &query);
        }

        const string robustness2Extension = "VK_EXT_robustness2";
        var indexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures { SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures };
        var robustness2Features = new PhysicalDeviceRobustness2FeaturesEXT
        {
            SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
            PNext = &indexingFeatures,
        };
        var hasRobustness2 = HasDeviceExtensions(vk, physical, [robustness2Extension]);
        var indexingQuery = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = hasRobustness2 ? &robustness2Features : &indexingFeatures,
        };
        vk.GetPhysicalDeviceFeatures2(physical, &indexingQuery);
        var runtimeDescriptors = hasRobustness2 && robustness2Features.NullDescriptor && indexingFeatures.RuntimeDescriptorArray &&
            indexingFeatures.ShaderSampledImageArrayNonUniformIndexing && indexingFeatures.DescriptorBindingSampledImageUpdateAfterBind &&
            indexingFeatures.DescriptorBindingUpdateUnusedWhilePending && indexingFeatures.DescriptorBindingPartiallyBound;

        var vulkan13Features = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
        };
        var addressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            PNext = &vulkan13Features,
        };
        var timelineFeatures = new PhysicalDeviceTimelineSemaphoreFeatures
        {
            SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
            PNext = &addressFeatures,
        };
        var features = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &timelineFeatures };
        vk.GetPhysicalDeviceFeatures2(physical, &features);
        var dynamicRendering = vulkan13Features.DynamicRendering && vulkan13Features.Synchronization2 &&
            HasDeviceExtensions(vk, physical, RenderingExtensionNames);
        if (family == uint.MaxValue || !timelineFeatures.TimelineSemaphore || !addressFeatures.BufferDeviceAddress ||
            !vulkan13Features.Synchronization2)
        {
            vk.DestroyInstance(instance, null);
            return null;
        }

        vk.GetPhysicalDeviceFeatures(physical, out var baseFeatures);
        var meshShaders = meshFeatures.MeshShader && vulkan13Features.ComputeFullSubgroups && dynamicRendering;
        var sizeControlProperties = new PhysicalDeviceSubgroupSizeControlProperties
        {
            SType = StructureType.PhysicalDeviceSubgroupSizeControlProperties,
        };
        var sizeControlQuery = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &sizeControlProperties };
        vk.GetPhysicalDeviceProperties2(physical, &sizeControlQuery);
        var computeSubgroup32 = vulkan13Features.SubgroupSizeControl &&
            (sizeControlProperties.RequiredSubgroupSizeStages & ShaderStageFlags.ComputeBit) != 0 &&
            sizeControlProperties.MinSubgroupSize <= 32 && sizeControlProperties.MaxSubgroupSize >= 32;
        var enabledFeatures = new PhysicalDeviceFeatures
        {
            GeometryShader = baseFeatures.GeometryShader,
            OcclusionQueryPrecise = baseFeatures.OcclusionQueryPrecise,
            SampleRateShading = baseFeatures.SampleRateShading,
            SamplerAnisotropy = baseFeatures.SamplerAnisotropy,
            ShaderStorageImageExtendedFormats = baseFeatures.ShaderStorageImageExtendedFormats,
            ShaderInt64 = baseFeatures.ShaderInt64,
            ShaderClipDistance = baseFeatures.ShaderClipDistance,
            ShaderCullDistance = baseFeatures.ShaderCullDistance,
            TessellationShader = baseFeatures.TessellationShader,
            RobustBufferAccess = baseFeatures.RobustBufferAccess,
            VertexPipelineStoresAndAtomics = baseFeatures.VertexPipelineStoresAndAtomics,
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
        vulkan13Features = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = dynamicRendering,
            Synchronization2 = true,
            ComputeFullSubgroups = meshShaders,
            SubgroupSizeControl = computeSubgroup32,
        };
        addressFeatures = new PhysicalDeviceBufferDeviceAddressFeatures
        {
            SType = StructureType.PhysicalDeviceBufferDeviceAddressFeatures,
            BufferDeviceAddress = true,
            PNext = &vulkan13Features,
        };
        timelineFeatures.PNext = &addressFeatures;
        if (barycentric)
        {
            barycentricFeatures.PNext = vulkan13Features.PNext;
            vulkan13Features.PNext = &barycentricFeatures;
        }
        if (runtimeDescriptors)
        {
            indexingFeatures = new PhysicalDeviceDescriptorIndexingFeatures
            {
                SType = StructureType.PhysicalDeviceDescriptorIndexingFeatures,
                RuntimeDescriptorArray = true,
                ShaderSampledImageArrayNonUniformIndexing = true,
                DescriptorBindingSampledImageUpdateAfterBind = true,
                DescriptorBindingUpdateUnusedWhilePending = true,
                DescriptorBindingPartiallyBound = true,
                PNext = timelineFeatures.PNext,
            };
            robustness2Features = new PhysicalDeviceRobustness2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                NullDescriptor = true,
                PNext = &indexingFeatures,
            };
            timelineFeatures.PNext = &robustness2Features;
        }

        // Native f16 conversion needs shaderFloat16 and per-width RTE/denorm-preserve control (the
        // presenter's rule for ExactFloat16ConversionSupported).
        var float16Features = new PhysicalDeviceShaderFloat16Int8Features
        {
            SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
        };
        var float16Query = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &float16Features };
        vk.GetPhysicalDeviceFeatures2(physical, &float16Query);
        var floatControls = new PhysicalDeviceFloatControlsProperties { SType = StructureType.PhysicalDeviceFloatControlsProperties };
        var floatControlsQuery = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &floatControls };
        vk.GetPhysicalDeviceProperties2(physical, &floatControlsQuery);
        var exactFloat16 = float16Features.ShaderFloat16 && floatControls.ShaderRoundingModeRtefloat16 &&
            floatControls.ShaderDenormPreserveFloat16 &&
            floatControls.RoundingModeIndependence == ShaderFloatControlsIndependence.IndependenceAll &&
            floatControls.DenormBehaviorIndependence == ShaderFloatControlsIndependence.IndependenceAll;
        if (exactFloat16)
        {
            float16Features = new PhysicalDeviceShaderFloat16Int8Features
            {
                SType = StructureType.PhysicalDeviceShaderFloat16Int8Features,
                ShaderFloat16 = true,
                PNext = timelineFeatures.PNext,
            };
            timelineFeatures.PNext = &float16Features;
        }

        var extensionNames = new List<string>();
        if (runtimeDescriptors) extensionNames.Add(robustness2Extension);
        if (meshShaders)
        {
            meshFeatures = new PhysicalDeviceMeshShaderFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceMeshShaderFeaturesExt,
                MeshShader = true,
                PNext = vulkan13Features.PNext,
            };
            vulkan13Features.PNext = &meshFeatures;
            extensionNames.Add(meshExtension);
        }
        if (dynamicRendering) extensionNames.AddRange(RenderingExtensionNames);
        if (barycentric) extensionNames.Add(barycentricExtension);
        const string fillRectangleExtension = "VK_NV_fill_rectangle";
        var fillRectangle = HasDeviceExtensions(vk, physical, [fillRectangleExtension]);
        if (fillRectangle) extensionNames.Add(fillRectangleExtension);
        var deviceExtensions = extensionNames.Count > 0 ? SilkMarshal.StringArrayToPtr(extensionNames.ToArray()) : 0;
        var deviceInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &timelineFeatures,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            PEnabledFeatures = &enabledFeatures,
            EnabledExtensionCount = (uint)extensionNames.Count,
            PpEnabledExtensionNames = (byte**)deviceExtensions,
        };
        var deviceCreated = vk.CreateDevice(physical, &deviceInfo, null, out var device);
        if (deviceExtensions != 0)
        {
            SilkMarshal.Free(deviceExtensions);
        }

        if (deviceCreated != Result.Success)
        {
            vk.DestroyInstance(instance, null);
            return null;
        }

        vk.GetDeviceQueue(device, family, 0, out var queue);
        var result = new HeadlessVulkan(vk, instance, physical, device, queue, family, apiVersion, enabledFeatures, dynamicRendering)
        {
            SupportsFragmentShaderBarycentric = barycentric,
            SupportsTessellationShader = enabledFeatures.TessellationShader,
            SupportsFillRectangle = fillRectangle,
            SupportsRuntimeDescriptors = runtimeDescriptors,
            SupportsMeshShaders = meshShaders,
            ExactFloat16Conversion = exactFloat16,
            CanRequireComputeSubgroup32 = computeSubgroup32,
        };
        if (validation)
        {
            result.RegisterDebugMessenger();
        }

        return result;
    }

    private static bool HasDeviceExtensions(Vk vk, PhysicalDevice physical, IReadOnlyList<string> names)
    {
        uint count = 0;
        if (vk.EnumerateDeviceExtensionProperties(physical, (byte*)null, &count, null) != Result.Success || count == 0)
        {
            return false;
        }

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* pointer = properties)
        {
            if (vk.EnumerateDeviceExtensionProperties(physical, (byte*)null, &count, pointer) != Result.Success)
            {
                return false;
            }
        }

        var available = new HashSet<string>();
        for (var index = 0; index < count; index++)
        {
            var property = properties[index];
            available.Add(SilkMarshal.PtrToString((nint)property.ExtensionName) ?? string.Empty);
        }

        return names.All(available.Contains);
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
