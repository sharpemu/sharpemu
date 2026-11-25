// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut.Upscaling.Streamline;

internal static class StreamlineDeviceFeatures
{
    public static void Enable(ref PhysicalDeviceVulkan12Features enabled, in PhysicalDeviceVulkan12Features supported, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            switch (name)
            {
                case "samplerMirrorClampToEdge": Set(ref enabled.SamplerMirrorClampToEdge, supported.SamplerMirrorClampToEdge, name); break;
                case "drawIndirectCount": Set(ref enabled.DrawIndirectCount, supported.DrawIndirectCount, name); break;
                case "storageBuffer8BitAccess": Set(ref enabled.StorageBuffer8BitAccess, supported.StorageBuffer8BitAccess, name); break;
                case "uniformAndStorageBuffer8BitAccess": Set(ref enabled.UniformAndStorageBuffer8BitAccess, supported.UniformAndStorageBuffer8BitAccess, name); break;
                case "storagePushConstant8": Set(ref enabled.StoragePushConstant8, supported.StoragePushConstant8, name); break;
                case "shaderBufferInt64Atomics": Set(ref enabled.ShaderBufferInt64Atomics, supported.ShaderBufferInt64Atomics, name); break;
                case "shaderSharedInt64Atomics": Set(ref enabled.ShaderSharedInt64Atomics, supported.ShaderSharedInt64Atomics, name); break;
                case "shaderFloat16": Set(ref enabled.ShaderFloat16, supported.ShaderFloat16, name); break;
                case "shaderInt8": Set(ref enabled.ShaderInt8, supported.ShaderInt8, name); break;
                case "descriptorIndexing": Set(ref enabled.DescriptorIndexing, supported.DescriptorIndexing, name); break;
                case "shaderInputAttachmentArrayDynamicIndexing": Set(ref enabled.ShaderInputAttachmentArrayDynamicIndexing, supported.ShaderInputAttachmentArrayDynamicIndexing, name); break;
                case "shaderUniformTexelBufferArrayDynamicIndexing": Set(ref enabled.ShaderUniformTexelBufferArrayDynamicIndexing, supported.ShaderUniformTexelBufferArrayDynamicIndexing, name); break;
                case "shaderStorageTexelBufferArrayDynamicIndexing": Set(ref enabled.ShaderStorageTexelBufferArrayDynamicIndexing, supported.ShaderStorageTexelBufferArrayDynamicIndexing, name); break;
                case "shaderUniformBufferArrayNonUniformIndexing": Set(ref enabled.ShaderUniformBufferArrayNonUniformIndexing, supported.ShaderUniformBufferArrayNonUniformIndexing, name); break;
                case "shaderSampledImageArrayNonUniformIndexing": Set(ref enabled.ShaderSampledImageArrayNonUniformIndexing, supported.ShaderSampledImageArrayNonUniformIndexing, name); break;
                case "shaderStorageBufferArrayNonUniformIndexing": Set(ref enabled.ShaderStorageBufferArrayNonUniformIndexing, supported.ShaderStorageBufferArrayNonUniformIndexing, name); break;
                case "shaderStorageImageArrayNonUniformIndexing": Set(ref enabled.ShaderStorageImageArrayNonUniformIndexing, supported.ShaderStorageImageArrayNonUniformIndexing, name); break;
                case "shaderInputAttachmentArrayNonUniformIndexing": Set(ref enabled.ShaderInputAttachmentArrayNonUniformIndexing, supported.ShaderInputAttachmentArrayNonUniformIndexing, name); break;
                case "shaderUniformTexelBufferArrayNonUniformIndexing": Set(ref enabled.ShaderUniformTexelBufferArrayNonUniformIndexing, supported.ShaderUniformTexelBufferArrayNonUniformIndexing, name); break;
                case "shaderStorageTexelBufferArrayNonUniformIndexing": Set(ref enabled.ShaderStorageTexelBufferArrayNonUniformIndexing, supported.ShaderStorageTexelBufferArrayNonUniformIndexing, name); break;
                case "descriptorBindingUniformBufferUpdateAfterBind": Set(ref enabled.DescriptorBindingUniformBufferUpdateAfterBind, supported.DescriptorBindingUniformBufferUpdateAfterBind, name); break;
                case "descriptorBindingSampledImageUpdateAfterBind": Set(ref enabled.DescriptorBindingSampledImageUpdateAfterBind, supported.DescriptorBindingSampledImageUpdateAfterBind, name); break;
                case "descriptorBindingStorageImageUpdateAfterBind": Set(ref enabled.DescriptorBindingStorageImageUpdateAfterBind, supported.DescriptorBindingStorageImageUpdateAfterBind, name); break;
                case "descriptorBindingStorageBufferUpdateAfterBind": Set(ref enabled.DescriptorBindingStorageBufferUpdateAfterBind, supported.DescriptorBindingStorageBufferUpdateAfterBind, name); break;
                case "descriptorBindingUniformTexelBufferUpdateAfterBind": Set(ref enabled.DescriptorBindingUniformTexelBufferUpdateAfterBind, supported.DescriptorBindingUniformTexelBufferUpdateAfterBind, name); break;
                case "descriptorBindingStorageTexelBufferUpdateAfterBind": Set(ref enabled.DescriptorBindingStorageTexelBufferUpdateAfterBind, supported.DescriptorBindingStorageTexelBufferUpdateAfterBind, name); break;
                case "descriptorBindingUpdateUnusedWhilePending": Set(ref enabled.DescriptorBindingUpdateUnusedWhilePending, supported.DescriptorBindingUpdateUnusedWhilePending, name); break;
                case "descriptorBindingPartiallyBound": Set(ref enabled.DescriptorBindingPartiallyBound, supported.DescriptorBindingPartiallyBound, name); break;
                case "descriptorBindingVariableDescriptorCount": Set(ref enabled.DescriptorBindingVariableDescriptorCount, supported.DescriptorBindingVariableDescriptorCount, name); break;
                case "runtimeDescriptorArray": Set(ref enabled.RuntimeDescriptorArray, supported.RuntimeDescriptorArray, name); break;
                case "samplerFilterMinmax": Set(ref enabled.SamplerFilterMinmax, supported.SamplerFilterMinmax, name); break;
                case "scalarBlockLayout": Set(ref enabled.ScalarBlockLayout, supported.ScalarBlockLayout, name); break;
                case "imagelessFramebuffer": Set(ref enabled.ImagelessFramebuffer, supported.ImagelessFramebuffer, name); break;
                case "uniformBufferStandardLayout": Set(ref enabled.UniformBufferStandardLayout, supported.UniformBufferStandardLayout, name); break;
                case "shaderSubgroupExtendedTypes": Set(ref enabled.ShaderSubgroupExtendedTypes, supported.ShaderSubgroupExtendedTypes, name); break;
                case "separateDepthStencilLayouts": Set(ref enabled.SeparateDepthStencilLayouts, supported.SeparateDepthStencilLayouts, name); break;
                case "hostQueryReset": Set(ref enabled.HostQueryReset, supported.HostQueryReset, name); break;
                case "timelineSemaphore": Set(ref enabled.TimelineSemaphore, supported.TimelineSemaphore, name); break;
                case "bufferDeviceAddress": Set(ref enabled.BufferDeviceAddress, supported.BufferDeviceAddress, name); break;
                case "bufferDeviceAddressCaptureReplay": Set(ref enabled.BufferDeviceAddressCaptureReplay, supported.BufferDeviceAddressCaptureReplay, name); break;
                case "bufferDeviceAddressMultiDevice": Set(ref enabled.BufferDeviceAddressMultiDevice, supported.BufferDeviceAddressMultiDevice, name); break;
                case "vulkanMemoryModel": Set(ref enabled.VulkanMemoryModel, supported.VulkanMemoryModel, name); break;
                case "vulkanMemoryModelDeviceScope": Set(ref enabled.VulkanMemoryModelDeviceScope, supported.VulkanMemoryModelDeviceScope, name); break;
                case "vulkanMemoryModelAvailabilityVisibilityChains": Set(ref enabled.VulkanMemoryModelAvailabilityVisibilityChains, supported.VulkanMemoryModelAvailabilityVisibilityChains, name); break;
                case "shaderOutputViewportIndex": Set(ref enabled.ShaderOutputViewportIndex, supported.ShaderOutputViewportIndex, name); break;
                case "shaderOutputLayer": Set(ref enabled.ShaderOutputLayer, supported.ShaderOutputLayer, name); break;
                case "subgroupBroadcastDynamicId": Set(ref enabled.SubgroupBroadcastDynamicId, supported.SubgroupBroadcastDynamicId, name); break;
                default: Console.Error.WriteLine($"[DLSS][WARN] DLSS asked for an unknown Vulkan 1.2 feature: {name}."); break;
            }
        }
    }

    public static void Enable(ref PhysicalDeviceVulkan13Features enabled, in PhysicalDeviceVulkan13Features supported, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            switch (name)
            {
                case "robustImageAccess": Set(ref enabled.RobustImageAccess, supported.RobustImageAccess, name); break;
                case "inlineUniformBlock": Set(ref enabled.InlineUniformBlock, supported.InlineUniformBlock, name); break;
                case "descriptorBindingInlineUniformBlockUpdateAfterBind": Set(ref enabled.DescriptorBindingInlineUniformBlockUpdateAfterBind, supported.DescriptorBindingInlineUniformBlockUpdateAfterBind, name); break;
                case "pipelineCreationCacheControl": Set(ref enabled.PipelineCreationCacheControl, supported.PipelineCreationCacheControl, name); break;
                case "privateData": Set(ref enabled.PrivateData, supported.PrivateData, name); break;
                case "shaderDemoteToHelperInvocation": Set(ref enabled.ShaderDemoteToHelperInvocation, supported.ShaderDemoteToHelperInvocation, name); break;
                case "shaderTerminateInvocation": Set(ref enabled.ShaderTerminateInvocation, supported.ShaderTerminateInvocation, name); break;
                case "subgroupSizeControl": Set(ref enabled.SubgroupSizeControl, supported.SubgroupSizeControl, name); break;
                case "computeFullSubgroups": Set(ref enabled.ComputeFullSubgroups, supported.ComputeFullSubgroups, name); break;
                case "synchronization2": Set(ref enabled.Synchronization2, supported.Synchronization2, name); break;
                case "textureCompressionASTC_HDR": Set(ref enabled.TextureCompressionAstcHdr, supported.TextureCompressionAstcHdr, name); break;
                case "shaderZeroInitializeWorkgroupMemory": Set(ref enabled.ShaderZeroInitializeWorkgroupMemory, supported.ShaderZeroInitializeWorkgroupMemory, name); break;
                case "dynamicRendering": Set(ref enabled.DynamicRendering, supported.DynamicRendering, name); break;
                case "shaderIntegerDotProduct": Set(ref enabled.ShaderIntegerDotProduct, supported.ShaderIntegerDotProduct, name); break;
                case "maintenance4": Set(ref enabled.Maintenance4, supported.Maintenance4, name); break;
                default: Console.Error.WriteLine($"[DLSS][WARN] DLSS asked for an unknown Vulkan 1.3 feature: {name}."); break;
            }
        }
    }

    private static void Set(ref Bool32 enabled, Bool32 supported, string name)
    {
        if (supported)
        {
            enabled = true;
        }
        else
        {
            Console.Error.WriteLine($"[DLSS][WARN] The GPU lacks the Vulkan feature {name}, which DLSS asked for.");
        }
    }
}
