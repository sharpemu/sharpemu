// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut.Upscaling.Fsr;

// Keep the supported features enabled which the SDK can select while building its pipelines.
internal unsafe struct FsrDeviceFeatures
{
    public const int ExtensionCount = 3;

    private PhysicalDeviceVulkan11Features _storage16;
    private PhysicalDeviceComputeShaderDerivativesFeaturesKHR _derivatives;
    private PhysicalDeviceCooperativeMatrixFeaturesKHR _matrix;
    private PhysicalDeviceShaderFloat8FeaturesEXT _float8;

    public static void Enable(
        FsrDeviceFeatures* storage, Vk vk, PhysicalDevice physicalDevice, Func<string, bool> hasExtension,
        ref PhysicalDeviceFeatures features, ref PhysicalDeviceVulkan12Features vulkan12,
        ref PhysicalDeviceVulkan13Features vulkan13, byte** names, ref uint count, List<nint> allocatedNames)
    {
        const string derivatives = "VK_KHR_compute_shader_derivatives";
        const string matrix = "VK_KHR_cooperative_matrix";
        const string float8 = "VK_EXT_shader_float8";
        storage->_derivatives.SType = StructureType.PhysicalDeviceComputeShaderDerivativesFeaturesKhr;
        storage->_matrix.SType = StructureType.PhysicalDeviceCooperativeMatrixFeaturesKhr;
        storage->_float8.SType = StructureType.PhysicalDeviceShaderFloat8FeaturesExt;
        var supported11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features };
        var supported13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, PNext = &supported11 };
        var supported12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &supported13 };
        var supported = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &supported12 };
        var hasDerivatives = hasExtension(derivatives);
        var hasMatrix = hasExtension(matrix);
        var hasFloat8 = hasMatrix && hasExtension(float8);
        if (hasDerivatives)
        {
            storage->_derivatives.PNext = supported.PNext;
            supported.PNext = &storage->_derivatives;
        }
        if (hasMatrix)
        {
            storage->_matrix.PNext = supported.PNext;
            supported.PNext = &storage->_matrix;
        }
        if (hasFloat8)
        {
            storage->_float8.PNext = supported.PNext;
            supported.PNext = &storage->_float8;
        }

        vk.GetPhysicalDeviceFeatures2(physicalDevice, &supported);
        features.ShaderInt16 = supported.Features.ShaderInt16;
        vulkan12.ShaderFloat16 = supported12.ShaderFloat16;
        vulkan12.ShaderInt8 = supported12.ShaderInt8;
        vulkan12.StorageBuffer8BitAccess = supported12.StorageBuffer8BitAccess;
        vulkan12.UniformAndStorageBuffer8BitAccess = supported12.UniformAndStorageBuffer8BitAccess;
        vulkan12.SeparateDepthStencilLayouts = supported12.SeparateDepthStencilLayouts;
        vulkan13.SubgroupSizeControl = supported13.SubgroupSizeControl;
        vulkan13.ComputeFullSubgroups = supported13.ComputeFullSubgroups;
        vulkan13.ShaderIntegerDotProduct = supported13.ShaderIntegerDotProduct;
        vulkan13.ShaderDemoteToHelperInvocation = supported13.ShaderDemoteToHelperInvocation;
        storage->_storage16 = new PhysicalDeviceVulkan11Features
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            PNext = vulkan13.PNext,
            StorageBuffer16BitAccess = supported11.StorageBuffer16BitAccess,
            UniformAndStorageBuffer16BitAccess = supported11.UniformAndStorageBuffer16BitAccess,
            StoragePushConstant16 = supported11.StoragePushConstant16,
            StorageInputOutput16 = supported11.StorageInputOutput16,
        };
        vulkan13.PNext = &storage->_storage16;

        if (hasDerivatives && (storage->_derivatives.ComputeDerivativeGroupLinear || storage->_derivatives.ComputeDerivativeGroupQuads))
        {
            AddName(derivatives, names, ref count, allocatedNames);
            storage->_derivatives.PNext = vulkan13.PNext;
            vulkan13.PNext = &storage->_derivatives;
        }
        if (hasMatrix && storage->_matrix.CooperativeMatrix)
        {
            AddName(matrix, names, ref count, allocatedNames);
            storage->_matrix.PNext = vulkan13.PNext;
            vulkan13.PNext = &storage->_matrix;
            if (hasFloat8 && storage->_float8.ShaderFloat8 && storage->_float8.ShaderFloat8CooperativeMatrix)
            {
                AddName(float8, names, ref count, allocatedNames);
                storage->_float8.PNext = vulkan13.PNext;
                vulkan13.PNext = &storage->_float8;
            }
        }
    }

    private static void AddName(string name, byte** names, ref uint count, List<nint> allocatedNames)
    {
        var pointer = SilkMarshal.StringToPtr(name);
        allocatedNames.Add(pointer);
        names[count++] = (byte*)pointer;
    }
}
