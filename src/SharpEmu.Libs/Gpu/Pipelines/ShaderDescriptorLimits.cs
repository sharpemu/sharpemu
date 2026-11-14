// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

internal static class ShaderDescriptorLimits
{
    // Compute owns its set. A larger layout can use an allocated set instead of push descriptors,
    // but must fit one pool as well as the per-stage and per-set device limits.
    public static StableImageDescriptorLimits ComputeImageLimits(PhysicalDeviceLimits limits)
    {
        var pool = DescriptorHeap.PoolCapacity;
        return new(
            Math.Min(Math.Min(limits.MaxPerStageDescriptorSampledImages, limits.MaxDescriptorSetSampledImages), pool.SampledImages),
            Math.Min(Math.Min(limits.MaxPerStageDescriptorStorageImages, limits.MaxDescriptorSetStorageImages), pool.StorageImages),
            Math.Min(Math.Min(limits.MaxPerStageDescriptorSamplers, limits.MaxDescriptorSetSamplers), pool.Samplers),
            Math.Min(Math.Min(limits.MaxPerStageDescriptorStorageBuffers, limits.MaxDescriptorSetStorageBuffers), pool.StorageBuffers),
            limits.MaxPerStageResources);
    }

    // A graphics stage shares its set and pool with the pipeline's other stage when the pipeline is built whole, so each
    // stage takes at most half the per-set and pool budget (and its per-stage limits): any two stages then fit together.
    public static StableImageDescriptorLimits GraphicsImageLimits(PhysicalDeviceLimits limits)
    {
        var pool = DescriptorHeap.PoolCapacity;
        return new(
            Math.Min(limits.MaxPerStageDescriptorSampledImages, Math.Min(limits.MaxDescriptorSetSampledImages, pool.SampledImages) / 2),
            Math.Min(limits.MaxPerStageDescriptorStorageImages, Math.Min(limits.MaxDescriptorSetStorageImages, pool.StorageImages) / 2),
            Math.Min(limits.MaxPerStageDescriptorSamplers, Math.Min(limits.MaxDescriptorSetSamplers, pool.Samplers) / 2),
            Math.Min(limits.MaxPerStageDescriptorStorageBuffers,
                Math.Min(limits.MaxDescriptorSetStorageBuffers, pool.StorageBuffers) / 2),
            limits.MaxPerStageResources);
    }

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
