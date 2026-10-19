// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.ShaderCache;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private const string PipelineBinaryExtensionName = "VK_KHR_pipeline_binary";
    private const StructureType PipelineCreateInfoKhrType = (StructureType)1000483007;
    private const PipelineCreateFlags2 PipelineCaptureDataFlag = (PipelineCreateFlags2)0x8000_0000UL;
    private const uint MaxPipelineBinaries = 64;
    private static ReadOnlySpan<byte> NvidiaDictionaryTag => "_NVDICT_"u8;

    [StructLayout(LayoutKind.Sequential)]
    private struct PipelineCreateInfoKhr
    {
        public StructureType SType;
        public void* PNext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PipelineCreateInfoHeader
    {
        public StructureType SType;
        public void* PNext;
        public PipelineCreateFlags Flags;
    }

    private sealed class PipelineBinaryApi
    {
        public required Device Device;
        public required byte[] DriverKey;
        public delegate* unmanaged<Device, PipelineCreateInfoKhr*, PipelineBinaryKeyKHR*, Result> GetPipelineKey;
        public delegate* unmanaged<Device, PipelineBinaryCreateInfoKHR*, AllocationCallbacks*, PipelineBinaryHandlesInfoKHR*, Result> CreatePipelineBinaries;
        public delegate* unmanaged<Device, PipelineBinaryKHR, AllocationCallbacks*, void> DestroyPipelineBinary;
        public delegate* unmanaged<Device, PipelineBinaryDataInfoKHR*, PipelineBinaryKeyKHR*, nuint*, void*, Result> GetPipelineBinaryData;
        public delegate* unmanaged<Device, ReleaseCapturedPipelineDataInfoKHR*, AllocationCallbacks*, Result> ReleaseCapturedPipelineData;
    }

    private sealed partial class Presenter
    {
        private bool _supportsPipelineBinaries;
        private PipelineBinaryApi? _pipelineBinaries;
        private ShaderCacheFile[] _pipelineStoreSources = [];
        private ShaderCacheFile? _pipelineStoreTarget;
        private volatile bool _pipelineBinaryDictionaryReached;

        private bool UsesPipelineStore => _pipelineStoreTarget is not null;

        private void SetPipelineStore(ShaderCacheFile? target, params ShaderCacheFile[] sources)
        {
            _pipelineStoreSources = sources;
            Volatile.Write(ref _pipelineStoreTarget, target);
        }

        private bool HasStoredPipeline(void* createInfo)
        {
            if (_pipelineBinaries is not { } api || !TryGetPipelineStoreKey(api, createInfo, out var key))
            {
                return false;
            }

            foreach (var source in _pipelineStoreSources)
            {
                if (source.HasBinary(api.DriverKey, key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetPipelineStoreKey(PipelineBinaryApi api, void* createInfo, out UInt128 key)
        {
            key = default;
            var info = new PipelineCreateInfoKhr { SType = PipelineCreateInfoKhrType, PNext = createInfo };
            var driverKey = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr };
            if (api.GetPipelineKey(api.Device, &info, &driverKey) != Result.Success || driverKey.KeySize is 0 or > 32)
            {
                return false;
            }

            key = XxHash128.HashToUInt128(new ReadOnlySpan<byte>(driverKey.Key, (int)driverKey.KeySize));
            _itemContentKeys?.Add(key);
            return true;
        }

        private bool QueryPipelineBinarySupport(bool supportsMaintenance5)
        {
            if (!ShaderCacheSettings.Enabled || !supportsMaintenance5 || !IsDeviceExtensionAvailable(PipelineBinaryExtensionName))
            {
                return false;
            }

            var features = new PhysicalDevicePipelineBinaryFeaturesKHR { SType = StructureType.PhysicalDevicePipelineBinaryFeaturesKhr };
            var query = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &features };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &query);
            return features.PipelineBinaries;
        }

        private void InitializePipelineBinaries()
        {
            if (!_supportsPipelineBinaries)
            {
                return;
            }

            nint Load(string name)
            {
                var pointer = (nint)_vk.GetDeviceProcAddr(_device, name);
                return pointer != 0 ? pointer : throw new InvalidOperationException($"{name} is unavailable.");
            }

            try
            {
                var api = new PipelineBinaryApi
                {
                    Device = _device,
                    DriverKey = [],
                    GetPipelineKey = (delegate* unmanaged<Device, PipelineCreateInfoKhr*, PipelineBinaryKeyKHR*, Result>)Load("vkGetPipelineKeyKHR"),
                    CreatePipelineBinaries = (delegate* unmanaged<Device, PipelineBinaryCreateInfoKHR*, AllocationCallbacks*, PipelineBinaryHandlesInfoKHR*, Result>)Load("vkCreatePipelineBinariesKHR"),
                    DestroyPipelineBinary = (delegate* unmanaged<Device, PipelineBinaryKHR, AllocationCallbacks*, void>)Load("vkDestroyPipelineBinaryKHR"),
                    GetPipelineBinaryData = (delegate* unmanaged<Device, PipelineBinaryDataInfoKHR*, PipelineBinaryKeyKHR*, nuint*, void*, Result>)Load("vkGetPipelineBinaryDataKHR"),
                    ReleaseCapturedPipelineData = (delegate* unmanaged<Device, ReleaseCapturedPipelineDataInfoKHR*, AllocationCallbacks*, Result>)Load("vkReleaseCapturedPipelineDataKHR"),
                };
                var globalKey = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr };
                if (api.GetPipelineKey(_device, null, &globalKey) != Result.Success || globalKey.KeySize is 0 or > 32)
                {
                    throw new InvalidOperationException("vkGetPipelineKeyKHR returned no global key.");
                }

                _pipelineBinaries = new PipelineBinaryApi
                {
                    Device = api.Device,
                    DriverKey = new ReadOnlySpan<byte>(globalKey.Key, (int)globalKey.KeySize).ToArray(),
                    GetPipelineKey = api.GetPipelineKey,
                    CreatePipelineBinaries = api.CreatePipelineBinaries,
                    DestroyPipelineBinary = api.DestroyPipelineBinary,
                    GetPipelineBinaryData = api.GetPipelineBinaryData,
                    ReleaseCapturedPipelineData = api.ReleaseCapturedPipelineData,
                };
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine($"[SHADER CACHE][WARN] Vulkan pipeline binaries are unavailable: {exception.Message}");
            }
        }

        private Result CreatePipelineObject(bool graphics, void* createInfo, PipelineCache cache, out Pipeline pipeline) =>
            graphics
                ? _vk.CreateGraphicsPipelines(_device, cache, 1, (GraphicsPipelineCreateInfo*)createInfo, null, out pipeline)
                : _vk.CreateComputePipelines(_device, cache, 1, (ComputePipelineCreateInfo*)createInfo, null, out pipeline);

        private Result CreateCachedPipelineObject(bool graphics, void* createInfo, out Pipeline pipeline, out bool unoptimized, bool fast)
        {
            unoptimized = false;
            var contentKey = default(UInt128);
            if (_pipelineBinaries is not { } api || Volatile.Read(ref _pipelineStoreTarget) is not { } file ||
                !TryGetPipelineStoreKey(api, createInfo, out contentKey))
            {
                if (fast && TryCreateFastPipeline(graphics, createInfo, out pipeline))
                {
                    unoptimized = true;
                    Interlocked.Increment(ref _shaderCacheCompiled);
                    Interlocked.Increment(ref _shaderCacheUnoptimized);
                    return Result.Success;
                }

                Interlocked.Increment(ref _shaderCacheCompiled);
                return CreatePipelineObject(graphics, createInfo, _pipelineCache, out pipeline);
            }

            foreach (var source in _pipelineStoreSources)
            {
                if (TryCreatePipelineFromBinaries(api, source, graphics, createInfo, contentKey, out pipeline))
                {
                    Interlocked.Increment(ref _shaderCacheFromBinaries);
                    return Result.Success;
                }
            }

            if (fast && TryCreateFastPipeline(graphics, createInfo, out pipeline))
            {
                unoptimized = true;
                Interlocked.Increment(ref _shaderCacheCompiled);
                Interlocked.Increment(ref _shaderCacheUnoptimized);
                return Result.Success;
            }

            var header = (PipelineCreateInfoHeader*)createInfo;
            var original = header->PNext;
            var flags = new PipelineCreateFlags2CreateInfoKHR
            {
                SType = StructureType.PipelineCreateFlags2CreateInfoKhr,
                PNext = original,
                Flags = (PipelineCreateFlags2)(ulong)header->Flags | PipelineCaptureDataFlag,
            };
            header->PNext = &flags;
            var result = CreatePipelineObject(graphics, createInfo, default, out pipeline);
            header->PNext = original;
            if (result == Result.Success)
            {
                Interlocked.Increment(ref _shaderCacheCompiled);
                StorePipelineBinaries(api, file, contentKey, pipeline);
                return result;
            }

            return CreatePipelineObject(graphics, createInfo, _pipelineCache, out pipeline);
        }

        private bool TryCreateFastPipeline(bool graphics, void* createInfo, out Pipeline pipeline)
        {
            var header = (PipelineCreateInfoHeader*)createInfo;
            var originalFlags = header->Flags;
            header->Flags |= PipelineCreateFlags.CreateDisableOptimizationBit;
            try
            {
                var result = CreatePipelineObject(graphics, createInfo, default, out pipeline);
                if (result == Result.Success)
                {
                    return true;
                }

                if (pipeline.Handle != 0)
                {
                    _vk.DestroyPipeline(_device, pipeline, null);
                    pipeline = default;
                }
                return false;
            }
            finally
            {
                header->Flags = originalFlags;
            }
        }

        private bool TryCreatePipelineFromBinaries(
            PipelineBinaryApi api,
            ShaderCacheFile file,
            bool graphics,
            void* createInfo,
            UInt128 contentKey,
            out Pipeline pipeline)
        {
            pipeline = default;
            if (!file.TryReadBinary(api.DriverKey, contentKey, out var parts) || parts.Length > MaxPipelineBinaries)
            {
                return false;
            }

            var keys = new PipelineBinaryKeyKHR[parts.Length];
            var data = new PipelineBinaryDataKHR[parts.Length];
            var binaries = new PipelineBinaryKHR[parts.Length];
            var pins = new GCHandle[parts.Length];
            try
            {
                for (var index = 0; index < parts.Length; index++)
                {
                    var part = parts[index];
                    if (part.Key.Length is 0 or > 32 || part.Data.Length == 0)
                    {
                        return false;
                    }

                    keys[index] = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr, KeySize = (uint)part.Key.Length };
                    fixed (byte* destination = keys[index].Key)
                    {
                        part.Key.CopyTo(new Span<byte>(destination, 32));
                    }

                    pins[index] = GCHandle.Alloc(part.Data, GCHandleType.Pinned);
                    data[index] = new PipelineBinaryDataKHR
                    {
                        DataSize = (nuint)part.Data.Length,
                        PData = (void*)pins[index].AddrOfPinnedObject(),
                    };
                }

                fixed (PipelineBinaryKeyKHR* keyPointer = keys)
                fixed (PipelineBinaryDataKHR* dataPointer = data)
                fixed (PipelineBinaryKHR* binaryPointer = binaries)
                {
                    var keysAndData = new PipelineBinaryKeysAndDataKHR
                    {
                        BinaryCount = (uint)parts.Length,
                        PPipelineBinaryKeys = keyPointer,
                        PPipelineBinaryData = dataPointer,
                    };
                    var create = new PipelineBinaryCreateInfoKHR
                    {
                        SType = StructureType.PipelineBinaryCreateInfoKhr,
                        PKeysAndDataInfo = &keysAndData,
                    };
                    var handles = new PipelineBinaryHandlesInfoKHR
                    {
                        SType = StructureType.PipelineBinaryHandlesInfoKhr,
                        PipelineBinaryCount = (uint)parts.Length,
                        PPipelineBinaries = binaryPointer,
                    };
                    if (api.CreatePipelineBinaries(api.Device, &create, null, &handles) != Result.Success)
                    {
                        Interlocked.Increment(ref _shaderCacheBinaryRejected);
                        return false;
                    }

                    var header = (PipelineCreateInfoHeader*)createInfo;
                    var original = header->PNext;
                    var binaryInfo = new PipelineBinaryInfoKHR
                    {
                        SType = StructureType.PipelineBinaryInfoKhr,
                        PNext = original,
                        BinaryCount = (uint)parts.Length,
                        PPipelineBinaries = binaryPointer,
                    };
                    header->PNext = &binaryInfo;
                    var result = CreatePipelineObject(graphics, createInfo, default, out pipeline);
                    header->PNext = original;
                    if (result != Result.Success)
                    {
                        Interlocked.Increment(ref _shaderCacheBinaryRejected);
                        return false;
                    }

                    return true;
                }
            }
            finally
            {
                foreach (var binary in binaries)
                {
                    if (binary.Handle != 0)
                    {
                        api.DestroyPipelineBinary(api.Device, binary, null);
                    }
                }

                foreach (var pin in pins)
                {
                    if (pin.IsAllocated)
                    {
                        pin.Free();
                    }
                }
            }
        }

        private void StorePipelineBinaries(PipelineBinaryApi api, ShaderCacheFile file, UInt128 contentKey, Pipeline pipeline)
        {
            var create = new PipelineBinaryCreateInfoKHR
            {
                SType = StructureType.PipelineBinaryCreateInfoKhr,
                Pipeline = pipeline,
            };
            var handles = new PipelineBinaryHandlesInfoKHR { SType = StructureType.PipelineBinaryHandlesInfoKhr };
            PipelineBinaryKHR[] binaries = [];
            try
            {
                if (api.CreatePipelineBinaries(api.Device, &create, null, &handles) != Result.Success ||
                    handles.PipelineBinaryCount is 0 or > MaxPipelineBinaries)
                {
                    return;
                }

                binaries = new PipelineBinaryKHR[handles.PipelineBinaryCount];
                fixed (PipelineBinaryKHR* binaryPointer = binaries)
                {
                    handles.PPipelineBinaries = binaryPointer;
                    if (api.CreatePipelineBinaries(api.Device, &create, null, &handles) != Result.Success)
                    {
                        return;
                    }
                }

                var parts = new PipelineBinaryPart[binaries.Length];
                for (var index = 0; index < binaries.Length; index++)
                {
                    var info = new PipelineBinaryDataInfoKHR
                    {
                        SType = StructureType.PipelineBinaryDataInfoKhr,
                        PipelineBinary = binaries[index],
                    };
                    var key = new PipelineBinaryKeyKHR { SType = StructureType.PipelineBinaryKeyKhr };
                    nuint size = 0;
                    if (api.GetPipelineBinaryData(api.Device, &info, &key, &size, null) != Result.Success || size == 0 || size > int.MaxValue)
                    {
                        return;
                    }

                    var bytes = new byte[(int)size];
                    fixed (byte* bytePointer = bytes)
                    {
                        if (api.GetPipelineBinaryData(api.Device, &info, &key, &size, bytePointer) != Result.Success)
                        {
                            return;
                        }
                    }

                    parts[index] = new PipelineBinaryPart(new ReadOnlySpan<byte>(key.Key, (int)Math.Min(key.KeySize, 32u)).ToArray(), bytes);
                }

                if (parts.Any(static part => part.Key.AsSpan().IndexOf(NvidiaDictionaryTag) >= 0))
                {
                    _pipelineBinaryDictionaryReached = true;
                    _lastCaptureReachedDictionary = true;
                    return;
                }

                file.AddBinary(api.DriverKey, contentKey, parts);
            }
            finally
            {
                foreach (var binary in binaries)
                {
                    if (binary.Handle != 0)
                    {
                        api.DestroyPipelineBinary(api.Device, binary, null);
                    }
                }

                var release = new ReleaseCapturedPipelineDataInfoKHR
                {
                    SType = StructureType.ReleaseCapturedPipelineDataInfoKhr,
                    Pipeline = pipeline,
                };
                api.ReleaseCapturedPipelineData(api.Device, &release, null);
            }
        }
    }
}
