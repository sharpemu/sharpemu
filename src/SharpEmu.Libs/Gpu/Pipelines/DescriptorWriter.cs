// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The native descriptor type and count of every binding kind, shared by layouts and writes.
public static class DescriptorWriter
{
    public static DescriptorType DescriptorType(DescriptorBindingKind kind)
    {
        var imageClass = ImageDescriptorBinding.ResourceClass(kind);
        if (imageClass == ShaderCompiler.Resources.ImageResourceClass.Sampled)
        {
            return Silk.NET.Vulkan.DescriptorType.SampledImage;
        }

        if (imageClass == ShaderCompiler.Resources.ImageResourceClass.Storage)
        {
            return Silk.NET.Vulkan.DescriptorType.StorageImage;
        }

        return kind switch
        {
            DescriptorBindingKind.Samplers => Silk.NET.Vulkan.DescriptorType.Sampler,
            DescriptorBindingKind.Buffers or DescriptorBindingKind.GlobalDataShare or DescriptorBindingKind.DeviceAddressPageTable or
                DescriptorBindingKind.FaultBuffer or DescriptorBindingKind.FlattenedResourceTable or DescriptorBindingKind.ShaderData =>
                Silk.NET.Vulkan.DescriptorType.StorageBuffer,
            _ => throw SubmissionScheduler.Fatal($"The descriptor binding kind is invalid: kind={kind}."),
        };
    }

    public static uint DescriptorCount(DescriptorBinding binding) => binding.Resources.Count == 0 ? 1u : (uint)binding.Resources.Count;

    public static ShaderStageFlags ShaderStageFlag(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => ShaderStageFlags.VertexBit,
        ShaderStage.Pixel => ShaderStageFlags.FragmentBit,
        ShaderStage.Compute => ShaderStageFlags.ComputeBit,
        _ => throw SubmissionScheduler.Fatal($"The shader stage is unknown: stage={stage}."),
    };

    public static PipelineStageFlags PipelineStageFlag(ShaderStageFlags stages)
    {
        var result = PipelineStageFlags.None;
        if ((stages & ShaderStageFlags.VertexBit) != 0)
        {
            result |= PipelineStageFlags.VertexShaderBit;
        }

        if ((stages & ShaderStageFlags.FragmentBit) != 0)
        {
            result |= PipelineStageFlags.FragmentShaderBit;
        }

        if ((stages & ShaderStageFlags.ComputeBit) != 0)
        {
            result |= PipelineStageFlags.ComputeShaderBit;
        }

        if (result == PipelineStageFlags.None)
        {
            throw SubmissionScheduler.Fatal($"The shader stages map to no pipeline stage: stages={stages}.");
        }

        return result;
    }

    // The total descriptor count of the stages of one pipeline decides between push descriptors and a heap set.
    public static uint TotalDescriptorCount(BindingLayout layout)
    {
        var count = 0u;
        foreach (var binding in layout.Descriptors)
        {
            count += DescriptorCount(binding);
        }

        return count;
    }
}
