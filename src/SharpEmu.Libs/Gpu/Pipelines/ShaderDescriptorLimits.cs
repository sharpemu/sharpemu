// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class ShaderDescriptorLimits
{
    public static void Validate(IReadOnlyList<DescriptorSetLayoutBinding> bindings, PhysicalDeviceLimits limits)
    {
        ValidateScope(bindings, 0, limits, false);
        foreach (var stage in new[] { ShaderStageFlags.VertexBit, ShaderStageFlags.FragmentBit,
            ShaderStageFlags.ComputeBit, ShaderStageFlags.MeshBitExt })
            ValidateScope(bindings, stage, limits, true);
    }

    private static void ValidateScope(IReadOnlyList<DescriptorSetLayoutBinding> bindings,
        ShaderStageFlags stage, PhysicalDeviceLimits limits, bool perStage)
    {
        ulong sampled = 0, storage = 0, samplers = 0, buffers = 0, uniforms = 0;
        foreach (var binding in bindings)
        {
            if (perStage && (binding.StageFlags & stage) == 0) continue;
            switch (binding.DescriptorType)
            {
                case DescriptorType.SampledImage: sampled += binding.DescriptorCount; break;
                case DescriptorType.StorageImage: storage += binding.DescriptorCount; break;
                case DescriptorType.Sampler: samplers += binding.DescriptorCount; break;
                case DescriptorType.StorageBuffer: buffers += binding.DescriptorCount; break;
                case DescriptorType.UniformBuffer: uniforms += binding.DescriptorCount; break;
                default: throw new InvalidOperationException($"The shader descriptor type is not supported: {binding.DescriptorType}.");
            }
        }

        void Check(string kind, ulong count, uint limit)
        {
            if (count > limit)
                throw new InvalidOperationException($"The shader descriptor count exceeds the device limit: scope={(perStage ? stage.ToString() : "pipeline")} kind={kind} count={count} limit={limit}.");
        }

        Check("sampled images", sampled, perStage ? limits.MaxPerStageDescriptorSampledImages : limits.MaxDescriptorSetSampledImages);
        Check("storage images", storage, perStage ? limits.MaxPerStageDescriptorStorageImages : limits.MaxDescriptorSetStorageImages);
        Check("samplers", samplers, perStage ? limits.MaxPerStageDescriptorSamplers : limits.MaxDescriptorSetSamplers);
        Check("storage buffers", buffers, perStage ? limits.MaxPerStageDescriptorStorageBuffers : limits.MaxDescriptorSetStorageBuffers);
        Check("uniform buffers", uniforms, perStage ? limits.MaxPerStageDescriptorUniformBuffers : limits.MaxDescriptorSetUniformBuffers);
        if (perStage) Check("resources", sampled + storage + buffers + uniforms, limits.MaxPerStageResources);
    }
}
