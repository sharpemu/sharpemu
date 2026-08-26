// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Text;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // This partial initializes Vulkan host infrastructure.

        private readonly SdlHostWindow _window;

        private Vk _vk = null!;
        private KhrSurface _surfaceApi = null!;
        private KhrSwapchain _swapchainApi = null!;
        private delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result> _setDebugUtilsObjectName;
        private delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void> _cmdBeginDebugUtilsLabel;
        private delegate* unmanaged<CommandBuffer, void> _cmdEndDebugUtilsLabel;
        private Instance _instance;
        private SurfaceKHR _surface;
        private DebugUtilsMessengerEXT _debugMessenger;
        private ExtDebugUtils? _debugUtils;
        private PhysicalDevice _physicalDevice;
        private uint _maxComputeWorkGroupCountX;
        private uint _maxComputeWorkGroupCountY;
        private uint _maxComputeWorkGroupCountZ;
        private uint _maxComputeWorkGroupSizeX;
        private uint _maxComputeWorkGroupSizeY;
        private uint _maxComputeWorkGroupSizeZ;
        private uint _maxComputeWorkGroupInvocations;
        private ulong _minStorageBufferOffsetAlignment = 1;
        private bool _supportsIndependentBlend;
        private bool _supportsDepthBiasClamp;
        private uint _maxColorAttachments;
        private Device _device;
        private PipelineCache _pipelineCache;
        private string? _pipelineCachePath;
        private bool _pipelineCacheDirty;
        private long _lastPipelineCacheSaveTick;
        private Queue _queue;
        private Queue _computeQueue;
        private uint _queueFamilyIndex;
        private uint _queueFamilyQueueCount;
        private bool _queueFamilySupportsCompute;
        private bool _useDedicatedComputeQueue;

        private CommandPool _commandPool;
        private CommandBuffer _commandBuffer;
        private CommandBuffer _presentationCommandBuffer;

        private void Initialize()
        {
            WaitForRenderDocAttachIfRequested();
            _vk = Vk.GetApi();
            CreateInstance();
            CreateSurface();
            SelectPhysicalDevice();
            CreateDevice();
            CreatePipelineCache();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            _vulkanReady = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut ready: {_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private static void WaitForRenderDocAttachIfRequested()
        {
            var value = Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_WAIT");
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (string.Equals(value, "enter", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Waiting for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}. Press Enter to continue.");
                _ = Console.ReadLine();
                return;
            }

            var seconds = 15;
            if (int.TryParse(value, out var parsedSeconds))
            {
                seconds = Math.Clamp(parsedSeconds, 1, 300);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Waiting {seconds}s for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        private bool IsInstanceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceExtensionProperties(
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Utf8NullTerminatedEquals(byte* actual, ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    return false;
                }
            }

            return actual[expected.Length] == 0;
        }

        private void LoadDebugUtilsCommands()
        {
            if (!_vulkanDebugUtilsEnabled)
            {
                return;
            }

            var setObjectName = _vk.GetDeviceProcAddr(_device, "vkSetDebugUtilsObjectNameEXT");
            var beginLabel = _vk.GetDeviceProcAddr(_device, "vkCmdBeginDebugUtilsLabelEXT");
            var endLabel = _vk.GetDeviceProcAddr(_device, "vkCmdEndDebugUtilsLabelEXT");
            _setDebugUtilsObjectName =
                (delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result>)
                setObjectName.Handle;
            _cmdBeginDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void>)
                beginLabel.Handle;
            _cmdEndDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, void>)
                endLabel.Handle;

            if (_setDebugUtilsObjectName is not null)
            {
                Console.Error.WriteLine("[LOADER][INFO] Vulkan debug labels enabled.");
            }
        }

        private void SetDebugName(ObjectType objectType, ulong objectHandle, string name)
        {
            if (_setDebugUtilsObjectName is null ||
                _device.Handle == 0 ||
                objectHandle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var info = new DebugUtilsObjectNameInfoEXT
                {
                    SType = StructureType.DebugUtilsObjectNameInfoExt,
                    ObjectType = objectType,
                    ObjectHandle = objectHandle,
                    PObjectName = namePointer,
                };
                _ = _setDebugUtilsObjectName(_device, &info);
            }
        }

        private void BeginDebugLabel(CommandBuffer commandBuffer, string name)
        {
            if (_cmdBeginDebugUtilsLabel is null ||
                commandBuffer.Handle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var label = new DebugUtilsLabelEXT
                {
                    SType = StructureType.DebugUtilsLabelExt,
                    PLabelName = namePointer,
                };
                label.Color[0] = 0.20f;
                label.Color[1] = 0.60f;
                label.Color[2] = 1.00f;
                label.Color[3] = 1.00f;
                _cmdBeginDebugUtilsLabel(commandBuffer, &label);
            }
        }

        private void EndDebugLabel(CommandBuffer commandBuffer)
        {
            if (_cmdEndDebugUtilsLabel is not null &&
                commandBuffer.Handle != 0)
            {
                _cmdEndDebugUtilsLabel(commandBuffer);
            }
        }

        private static byte[] NullTerminatedUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        private void CreateInstance()
        {
            var applicationName = (byte*)SilkMarshal.StringToPtr("SharpEmu");
            byte* validationLayerName = null;

            try
            {
                var applicationInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 1),
                    PEngineName = applicationName,
                    EngineVersion = Vk.MakeVersion(0, 0, 1),
                    ApiVersion = Vk.Version12,
                };

                var extensions = _window.GetRequiredVulkanInstanceExtensions(out var extensionCount);

                byte* debugUtilsExtension = null;
                byte* portabilityExtension = null;
                byte* swapchainColorspaceExtension = null;
                var instanceCreateFlags = InstanceCreateFlags.None;
                var enabledExtensionCount = (int)extensionCount;
                var enabledExtensions = stackalloc byte*[(int)extensionCount + 3];
                for (var index = 0; index < (int)extensionCount; index++)
                {
                    enabledExtensions[index] = extensions[index];
                }

                if (_vulkanDebugUtilsEnabled &&
                    IsInstanceExtensionAvailable(DebugUtilsExtensionName))
                {
                    debugUtilsExtension = (byte*)SilkMarshal.StringToPtr(DebugUtilsExtensionName);
                    enabledExtensions[enabledExtensionCount++] = debugUtilsExtension;
                }

                if (IsInstanceExtensionAvailable(PortabilityEnumerationExtensionName))
                {
                    // MoltenVK is a portability (non-conformant) implementation;
                    // without this flag + extension the loader hides it.
                    portabilityExtension = (byte*)SilkMarshal.StringToPtr(PortabilityEnumerationExtensionName);
                    enabledExtensions[enabledExtensionCount++] = portabilityExtension;
                    instanceCreateFlags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
                }

                if (IsInstanceExtensionAvailable(SwapchainColorspaceExtensionName))
                {
                    swapchainColorspaceExtension =
                        (byte*)SilkMarshal.StringToPtr(SwapchainColorspaceExtensionName);
                    enabledExtensions[enabledExtensionCount++] = swapchainColorspaceExtension;
                }

                if (_vulkanValidationEnabled &&
                    IsInstanceLayerAvailable("VK_LAYER_KHRONOS_validation"))
                {
                    validationLayerName = (byte*)SilkMarshal.StringToPtr("VK_LAYER_KHRONOS_validation");
                }
                else if (_vulkanValidationEnabled)
                {
                    Console.Error.WriteLine("[LOADER][WARN] SHARPEMU_VK_VALIDATION=1 but VK_LAYER_KHRONOS_validation not found (Vulkan SDK installed?).");
                }

                var layers = stackalloc byte*[1];
                if (validationLayerName is not null)
                {
                    layers[0] = validationLayerName;
                }

                var createInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    Flags = instanceCreateFlags,
                    PApplicationInfo = &applicationInfo,
                    EnabledExtensionCount = (uint)enabledExtensionCount,
                    PpEnabledExtensionNames = enabledExtensions,
                    EnabledLayerCount = validationLayerName is not null ? 1u : 0u,
                    PpEnabledLayerNames = validationLayerName is not null ? layers : null,
                };

                try
                {
                    Check(_vk.CreateInstance(&createInfo, null, out _instance), "vkCreateInstance");
                    if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi))
                    {
                        throw new InvalidOperationException("VK_KHR_surface is unavailable.");
                    }

                    if (validationLayerName is not null && _vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
                    {
                        _debugUtils = debugUtils;
                        RegisterDebugMessenger(debugUtils);
                        Console.Error.WriteLine("[LOADER][INFO] Vulkan Validation Layers active (SHARPEMU_VK_VALIDATION=1).");
                    }
                }
                finally
                {
                    if (debugUtilsExtension is not null)
                    {
                        SilkMarshal.Free((nint)debugUtilsExtension);
                    }
                    if (portabilityExtension is not null)
                    {
                        SilkMarshal.Free((nint)portabilityExtension);
                    }
                    if (swapchainColorspaceExtension is not null)
                    {
                        SilkMarshal.Free((nint)swapchainColorspaceExtension);
                    }
                }
            }
            finally
            {
                SilkMarshal.Free((nint)applicationName);
                if (validationLayerName is not null)
                {
                    SilkMarshal.Free((nint)validationLayerName);
                }
            }
        }

        private bool IsDeviceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateDeviceExtensionProperties(
                        _physicalDevice,
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsInstanceLayerAvailable(string layerName)
        {
            uint layerCount = 0;
            if (_vk.EnumerateInstanceLayerProperties(&layerCount, null) != Result.Success || layerCount == 0)
            {
                return false;
            }

            var properties = new LayerProperties[layerCount];
            fixed (LayerProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceLayerProperties(&layerCount, propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(layerName);
                for (var index = 0; index < layerCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].LayerName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        private void RegisterDebugMessenger(ExtDebugUtils debugUtils)
        {
            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                                  | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                              | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                              | DebugUtilsMessageTypeFlagsEXT.GeneralBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback),
            };

            Check(debugUtils.CreateDebugUtilsMessenger(_instance, &messengerInfo, null, out _debugMessenger),
                "vkCreateDebugUtilsMessengerEXT");
        }

        private static unsafe uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* callbackData,
            void* userData)
        {
            var message = SilkMarshal.PtrToString((nint)callbackData->PMessage);
            var prefix = severity switch
            {
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt => "[VULKAN][ERROR]",
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt => "[VULKAN][WARN]",
                _ => "[VULKAN][INFO]",
            };
            Console.Error.WriteLine($"{prefix} {message}");


            if (severity == DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt &&
                message is not null &&
                message.Contains("vkCreateShaderModule", StringComparison.Ordinal))
            {
                var dumpPath = _pendingShaderModuleDumpPath;
                var dumpHint = dumpPath is null
                    ? "Set SHARPEMU_SHADER_SPIRV_DUMP_DIR to a directory to "
                        + "capture every module's .spv bytes for offline "
                        + "spirv-dis/spirv-val analysis."
                    : $"Dumped module for this failure: {dumpPath}";

                Console.Error.WriteLine(
                    "[SHARPEMU][ERROR] A guest shader compiled to invalid SPIR-V."
                    + "The shader module was created without an API-level error. {dumpHint}");
            }

            return Vk.False;
        }
        private void CreateSurface()
        {
            _surface = _window.CreateVulkanSurface(_instance);
        }

        private void SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, null), "vkEnumeratePhysicalDevices");
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device was found.");
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicePointer = devices)
            {
                Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicePointer), "vkEnumeratePhysicalDevices");
            }

            var deviceOverride = Environment.GetEnvironmentVariable("SHARPEMU_VK_DEVICE");
            var bestScore = int.MinValue;
            var found = false;
            foreach (var device in devices)
            {
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* queuePointer = queues)
                {
                    _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, queuePointer);
                }

                for (uint index = 0; index < queueCount; index++)
                {
                    var supportsGraphics = (queues[index].QueueFlags & QueueFlags.GraphicsBit) != 0;
                    _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out var supportsPresent);
                    if (!supportsGraphics || !supportsPresent)
                    {
                        continue;
                    }

                    _vk.GetPhysicalDeviceProperties(device, out var properties);
                    var name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? string.Empty;
                    var score = ScorePhysicalDevice(properties, name, deviceOverride);
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan candidate: {name} ({properties.DeviceType}) score={score}");
                    if (score > bestScore)
                    {
                        bestScore = score;
                        _physicalDevice = device;
                        _queueFamilyIndex = index;
                        _queueFamilyQueueCount = queues[index].QueueCount;
                        _queueFamilySupportsCompute =
                            (queues[index].QueueFlags & QueueFlags.ComputeBit) != 0;
                        found = true;
                    }

                    break;
                }
            }

            if (!found)
            {
                throw new InvalidOperationException("No Vulkan graphics/present queue was found.");
            }

            LoadComputeDeviceLimits();
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var selected);
            _maxColorAttachments = selected.Limits.MaxColorAttachments;
            var selectedName = SilkMarshal.PtrToString((nint)selected.DeviceName) ?? "unknown";
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan device: {selectedName} ({selected.DeviceType})");
            VideoOutExports.SetSelectedGpuName(selectedName);
            if (_window is not null)
            {
                _window.SetTitle(VideoOutExports.GetWindowTitle());
            }
        }

        private void LoadComputeDeviceLimits()
        {
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            var subgroupSizeControl = new PhysicalDeviceSubgroupSizeControlProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupSizeControlProperties,
            };
            var subgroup = new PhysicalDeviceSubgroupProperties
            {
                SType = StructureType.PhysicalDeviceSubgroupProperties,
                PNext = &subgroupSizeControl,
            };
            var properties2 = new PhysicalDeviceProperties2
            {
                SType = StructureType.PhysicalDeviceProperties2,
                PNext = &subgroup,
            };
            _vk.GetPhysicalDeviceProperties2(_physicalDevice, &properties2);
            _maxComputeWorkGroupCountX = properties.Limits.MaxComputeWorkGroupCount[0];
            _maxComputeWorkGroupCountY = properties.Limits.MaxComputeWorkGroupCount[1];
            _maxComputeWorkGroupCountZ = properties.Limits.MaxComputeWorkGroupCount[2];
            _maxComputeWorkGroupSizeX = properties.Limits.MaxComputeWorkGroupSize[0];
            _maxComputeWorkGroupSizeY = properties.Limits.MaxComputeWorkGroupSize[1];
            _maxComputeWorkGroupSizeZ = properties.Limits.MaxComputeWorkGroupSize[2];
            _maxComputeWorkGroupInvocations = properties.Limits.MaxComputeWorkGroupInvocations;
            _minStorageBufferOffsetAlignment = Math.Max(
                properties.Limits.MinStorageBufferOffsetAlignment,
                1UL);
            if (GuestStorageBufferOffsetAlignment %
                _minStorageBufferOffsetAlignment != 0)
            {
                throw new InvalidOperationException(
                    $"Vulkan storage-buffer alignment " +
                    $"{_minStorageBufferOffsetAlignment} is not compatible with " +
                    $"the portable alias alignment " +
                    $"{GuestStorageBufferOffsetAlignment}");
            }
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan compute limits groups=" +
                $"{_maxComputeWorkGroupCountX}x{_maxComputeWorkGroupCountY}x{_maxComputeWorkGroupCountZ} " +
                $"local={_maxComputeWorkGroupSizeX}x{_maxComputeWorkGroupSizeY}x" +
                $"{_maxComputeWorkGroupSizeZ} invocations={_maxComputeWorkGroupInvocations} " +
                $"storage_alignment={_minStorageBufferOffsetAlignment}");
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan subgroup default={subgroup.SubgroupSize} " +
                $"stages={subgroup.SupportedStages} ops={subgroup.SupportedOperations} " +
                $"size_control={subgroupSizeControl.MinSubgroupSize}-" +
                $"{subgroupSizeControl.MaxSubgroupSize} " +
                $"required_stages={subgroupSizeControl.RequiredSubgroupSizeStages} " +
                $"max_compute_subgroups=" +
                $"{subgroupSizeControl.MaxComputeWorkgroupSubgroups}");
        }

        private void CreateDevice()
        {
            _useDedicatedComputeQueue =
                _useDedicatedComputeQueueRequested &&
                _queueFamilySupportsCompute &&
                _queueFamilyQueueCount >= 2;
            var priorities = stackalloc float[2] { 1.0f, 1.0f };
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
                QueueCount = _useDedicatedComputeQueue ? 2u : 1u,
                PQueuePriorities = priorities,
            };
            _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var supportedFeatures);
            _supportsIndependentBlend = supportedFeatures.IndependentBlend;
            _supportsDepthBiasClamp = supportedFeatures.DepthBiasClamp;
            var enabledFeatures = new PhysicalDeviceFeatures
            {
                IndependentBlend = supportedFeatures.IndependentBlend,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                ShaderInt64 = supportedFeatures.ShaderInt64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageExtendedFormats = supportedFeatures.ShaderStorageImageExtendedFormats,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                TextureCompressionBC = supportedFeatures.TextureCompressionBC,
                RobustBufferAccess = supportedFeatures.RobustBufferAccess,
                DepthBiasClamp = supportedFeatures.DepthBiasClamp,
            };

            if (!supportedFeatures.RobustBufferAccess)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support robustBufferAccess " +
                    "translated shaders performing out-of-bounds buffer access may cause device loss.");
            }

            if (!supportedFeatures.ShaderInt64)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderInt64 " +
                    "translated shaders using 64-bit integers will fail.");
            }

            if (!supportedFeatures.VertexPipelineStoresAndAtomics || !supportedFeatures.FragmentStoresAndAtomics)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support vertexPipelineStoresAndAtomics/fragmentStoresAndAtomics " +
                    "translated shaders using storage buffers in vertex/fragment stages may fail.");
            }

            if (!supportedFeatures.ShaderImageGatherExtended)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderImageGatherExtended " +
                    "translated shaders using image gather with offsets/LOD/bias will fail.");
            }

            if (!supportedFeatures.ShaderStorageImageReadWithoutFormat ||
                !supportedFeatures.ShaderStorageImageWriteWithoutFormat)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderStorageImage(Read|Write)WithoutFormat " +
                    "translated shaders using unformatted storage image load/store will fail.");
            }

            if (!supportedFeatures.TextureCompressionBC)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support textureCompressionBC " +
                    "guest BC1-BC7 textures cannot be sampled directly.");
            }

            var maintenance8Features = new PhysicalDeviceMaintenance8FeaturesKHR
            {
                SType = StructureType.PhysicalDeviceMaintenance8FeaturesKhr,
            };
            var robustness2Features = new PhysicalDeviceRobustness2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                PNext = &maintenance8Features,
            };
            var timelineSemaphoreFeatures = new PhysicalDeviceTimelineSemaphoreFeatures
            {
                SType = StructureType.PhysicalDeviceTimelineSemaphoreFeatures,
                PNext = &robustness2Features,
            };
            var featuresQuery = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &timelineSemaphoreFeatures,
            };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &featuresQuery);
            var supportsTimelineSemaphore = timelineSemaphoreFeatures.TimelineSemaphore;
            var supportsMaintenance8 = maintenance8Features.Maintenance8;
            var supportsRobustBufferAccess2 = robustness2Features.RobustBufferAccess2;
            var supportsRobustImageAccess2 = robustness2Features.RobustImageAccess2;
            var supportsNullDescriptor = robustness2Features.NullDescriptor;
            var supportsRobustness2 = supportsRobustImageAccess2 || supportsNullDescriptor;
            if (!supportsMaintenance8)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_KHR_maintenance8 " +
                    "translated shaders using a dynamic texel offset on non-gather image samples will fail.");
            }

            if (!supportsRobustImageAccess2)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_EXT_robustness2 robustImageAccess2 " +
                    "translated shaders performing out-of-bounds image access may cause device loss.");
            }

            var swapchainExtension = (byte*)SilkMarshal.StringToPtr("VK_KHR_swapchain");
            var maintenance8Extension = (byte*)SilkMarshal.StringToPtr("VK_KHR_maintenance8");
            var robustness2Extension = (byte*)SilkMarshal.StringToPtr("VK_EXT_robustness2");
            var portabilitySubsetExtension = (byte*)SilkMarshal.StringToPtr(PortabilitySubsetExtensionName);
            try
            {
                var extensions = stackalloc byte*[4];
                var extensionCount = 0u;
                extensions[extensionCount++] = swapchainExtension;
                if (supportsMaintenance8)
                {
                    extensions[extensionCount++] = maintenance8Extension;
                }

                if (supportsRobustness2)
                {
                    extensions[extensionCount++] = robustness2Extension;
                }

                if (IsDeviceExtensionAvailable(PortabilitySubsetExtensionName))
                {
                    // The spec requires enabling this when the (MoltenVK)
                    // device advertises it.
                    extensions[extensionCount++] = portabilitySubsetExtension;
                }

                maintenance8Features.Maintenance8 = supportsMaintenance8;
                maintenance8Features.PNext = null;
                robustness2Features.RobustBufferAccess2 =
                    supportsRobustBufferAccess2 && supportedFeatures.RobustBufferAccess;
                robustness2Features.RobustImageAccess2 = supportsRobustImageAccess2;
                robustness2Features.NullDescriptor = supportsNullDescriptor;
                robustness2Features.PNext = supportsMaintenance8 ? &maintenance8Features : null;
                _gpuLabelTimelineEnabled =
                    _gpuLabelTimelineRequested && supportsTimelineSemaphore;
                timelineSemaphoreFeatures.TimelineSemaphore = _gpuLabelTimelineEnabled;
                timelineSemaphoreFeatures.PNext = supportsRobustness2
                    ? &robustness2Features
                    : (supportsMaintenance8 ? &maintenance8Features : null);
                var features2 = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = _gpuLabelTimelineEnabled
                        ? &timelineSemaphoreFeatures
                        : supportsRobustness2
                            ? &robustness2Features
                            : (supportsMaintenance8 ? &maintenance8Features : null),
                    Features = enabledFeatures,
                };
                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &features2,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = extensionCount,
                    PpEnabledExtensionNames = extensions,
                };

                Check(_vk.CreateDevice(_physicalDevice, &createInfo, null, out _device), "vkCreateDevice");
            }
            finally
            {
                SilkMarshal.Free((nint)swapchainExtension);
                SilkMarshal.Free((nint)maintenance8Extension);
                SilkMarshal.Free((nint)robustness2Extension);
                SilkMarshal.Free((nint)portabilitySubsetExtension);
            }

            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
            if (_useDedicatedComputeQueue)
            {
                _vk.GetDeviceQueue(_device, _queueFamilyIndex, 1, out _computeQueue);
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan dedicated compute queue enabled " +
                    $"family={_queueFamilyIndex} queues={_queueFamilyQueueCount}.");
            }
            else if (_useDedicatedComputeQueueRequested)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan dedicated compute queue requested but " +
                    $"unavailable family={_queueFamilyIndex} queues={_queueFamilyQueueCount} " +
                    $"compute={_queueFamilySupportsCompute}; using graphics queue.");
            }
            if (_gpuLabelTimelineEnabled)
            {
                CreateGuestTimelineSemaphores();
                Volatile.Write(ref _gpuLabelTimelineAvailable, true);
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan GPU label timelines enabled " +
                    $"dedicated_compute={_useDedicatedComputeQueue}.");
            }
            else if (_gpuLabelTimelineRequested)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan GPU label timelines requested but " +
                    "timeline semaphores are unavailable; using CPU-visible labels.");
            }
            LoadDebugUtilsCommands();
            VulkanDetileSelfTest.RunIfRequested(_vk, _device, _queue, _physicalDevice, _queueFamilyIndex);
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainApi))
            {
                throw new InvalidOperationException("VK_KHR_swapchain is unavailable.");
            }
        }

        private void CreatePipelineCache()
        {
            var cacheMode = Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE");
            // Vulkan cache blobs carry the implementation's compatibility
            // header and are rejected/rebuilt below when the device or driver
            // changes. MoltenVK compilation of a large translated shader can
            // take ten seconds, so discarding a valid cache at every launch is
            // much more harmful than using Vulkan's normal persistence path.
            // Keep an explicit opt-out for diagnostics and read-only systems.
            var persistentCacheEnabled =
                !string.Equals(cacheMode, "0", StringComparison.Ordinal);
            _pipelineCachePath = persistentCacheEnabled ? GetPipelineCachePath() : null;
            byte[] initialData = [];
            try
            {
                if (_pipelineCachePath is not null && File.Exists(_pipelineCachePath))
                {
                    initialData = File.ReadAllBytes(_pipelineCachePath);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache read failed: {exception.Message}");
            }

            var result = TryCreatePipelineCache(initialData, out _pipelineCache);
            if (result != Result.Success && initialData.Length != 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache rejected ({result}); rebuilding it.");
                result = TryCreatePipelineCache([], out _pipelineCache);
            }

            if (result != Result.Success)
            {
                _pipelineCache = default;
                _pipelineCachePath = null;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache unavailable: {result}");
                return;
            }

            SetDebugName(
                ObjectType.PipelineCache,
                _pipelineCache.Handle,
                _pipelineCachePath is null
                    ? "SharpEmu in-memory pipeline cache"
                    : "SharpEmu persistent pipeline cache");
            _lastPipelineCacheSaveTick = Environment.TickCount64;
            if (_pipelineCachePath is null)
            {
                Console.Error.WriteLine(
                    "[LOADER][INFO] Vulkan pipeline cache ready: memory-only " +
                    "(persistence disabled with SHARPEMU_VK_PIPELINE_CACHE=0).");
            }
            else
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan pipeline cache ready: path={_pipelineCachePath} initial={initialData.Length} bytes");
            }
        }

        private Result TryCreatePipelineCache(byte[] initialData, out PipelineCache pipelineCache)
        {
            fixed (byte* initialDataPointer = initialData)
            {
                var createInfo = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = (nuint)initialData.Length,
                    PInitialData = initialData.Length == 0 ? null : initialDataPointer,
                };
                return _vk.CreatePipelineCache(
                    _device,
                    &createInfo,
                    null,
                    out pipelineCache);
            }
        }

        private static string GetPipelineCachePath()
        {
            var configured = Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH");
            var cachePath = VulkanPipelineCacheStorage.ResolvePath(
                VideoOutExports.GetApplicationTitleId(),
                configured);
            if (string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    var legacyPath = VulkanPipelineCacheStorage.GetLegacyPath();
                    if (VulkanPipelineCacheStorage.ImportLegacyCache(legacyPath, cachePath))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][INFO] Imported legacy Vulkan pipeline cache: source={legacyPath} destination={cachePath}");
                    }
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan pipeline cache migration failed: {exception.Message}");
                }
            }

            return cachePath;
        }

        private void MarkPipelineCacheDirty()
        {
            if (_pipelineCache.Handle == 0)
            {
                return;
            }

            _pipelineCacheDirty = true;
            // Exporting MoltenVK's cache can itself serialize the compiler.
            // Gameplay may discover dozens of expensive pipelines in one
            // frame, so saving after every slow creation compounds a warm-up
            // hitch into a multi-minute stall. Coalesce all creations into one
            // periodic snapshot; shutdown still forces a final save.
            if (Environment.TickCount64 - _lastPipelineCacheSaveTick >= 30_000)
            {
                SavePipelineCache(force: false);
            }
        }

        private void SavePipelineCache(bool force)
        {
            if (_pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCachePath))
            {
                return;
            }

            if (!force && !_pipelineCacheDirty)
            {
                return;
            }

            try
            {
                nuint size = 0;
                var result = _vk.GetPipelineCacheData(
                    _device,
                    _pipelineCache,
                    &size,
                    null);
                if (result != Result.Success || size == 0 || size > 256u * 1024u * 1024u)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan pipeline cache query failed: result={result} size={size}");
                    return;
                }

                var data = new byte[checked((int)size)];
                fixed (byte* dataPointer = data)
                {
                    result = _vk.GetPipelineCacheData(
                        _device,
                        _pipelineCache,
                        &size,
                        dataPointer);
                }

                if (result != Result.Success)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan pipeline cache export failed: {result}");
                    return;
                }

                if (size != (nuint)data.Length)
                {
                    Array.Resize(ref data, checked((int)size));
                }

                var directory = Path.GetDirectoryName(_pipelineCachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporaryPath = _pipelineCachePath + $".{Environment.ProcessId}.tmp";
                File.WriteAllBytes(temporaryPath, data);
                File.Move(temporaryPath, _pipelineCachePath, overwrite: true);
                _pipelineCacheDirty = false;
                _lastPipelineCacheSaveTick = Environment.TickCount64;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan pipeline cache saved: path={_pipelineCachePath} bytes={data.Length}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache save failed: {exception.Message}");
            }
        }

        private void CreateCommandResources()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _queueFamilyIndex,
            };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "vkCreateCommandPool");

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = MaxFramesInFlight,
            };
            _frameCommandBuffers = new CommandBuffer[MaxFramesInFlight];
            fixed (CommandBuffer* frameCommandBuffers = _frameCommandBuffers)
            {
                Check(
                    _vk.AllocateCommandBuffers(_device, &allocateInfo, frameCommandBuffers),
                    "vkAllocateCommandBuffers");
            }

            var semaphoreInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            _frameImageAvailable = new VkSemaphore[MaxFramesInFlight];
            _frameFences = new Fence[MaxFramesInFlight];
            _frameFencePending = new bool[MaxFramesInFlight];
            _frameTimelines = new ulong[MaxFramesInFlight];
            _frameTranslatedResources = new TranslatedDrawResources?[MaxFramesInFlight];
            _frameGuestImageVersions = new GuestImageResource?[MaxFramesInFlight];
            for (var slot = 0; slot < MaxFramesInFlight; slot++)
            {
                Check(
                    _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _frameImageAvailable[slot]),
                    "vkCreateSemaphore");
                Check(
                    _vk.CreateFence(_device, &fenceInfo, null, out _frameFences[slot]),
                    "vkCreateFence(frame)");
            }

            _renderFinishedPerImage = new VkSemaphore[_swapchainImages.Length];
            for (var image = 0; image < _renderFinishedPerImage.Length; image++)
            {
                Check(
                    _vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinishedPerImage[image]),
                    "vkCreateSemaphore");
            }

            _currentFrameSlot = 0;
            _commandBuffer = _frameCommandBuffers[0];
            _presentationCommandBuffer = _commandBuffer;

            CreateStagingBuffer((ulong)_extent.Width * _extent.Height * 4);
            CreateFrameUploadBuffers(_stagingSize);
            CreateOverlayResources();
        }
    }
}
