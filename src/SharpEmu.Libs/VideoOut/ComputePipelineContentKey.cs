// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

// Everything that goes into a VkComputePipelineCreateInfo: the SPIR-V module, the
// descriptor set layout, the push constant range and the pinned subgroup size. Two
// compute programs with the same key build the same native pipeline, so the second
// one reuses the first instead of compiling the module again.
internal readonly record struct ComputePipelineContentKey(string ModuleIdentity, string LayoutIdentity, uint RequiredSubgroupSize)
{
    public static ComputePipelineContentKey Create(
        string moduleIdentity,
        IReadOnlyList<DescriptorSetLayoutBinding> bindings,
        bool usesPushDescriptors,
        ShaderStageFlags pushConstantStages,
        uint pushConstantBytes,
        uint requiredSubgroupSize)
    {
        // Binding order is part of the layout the way the create info sees it.
        Span<byte> buffer = stackalloc byte[24];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        BitConverter.TryWriteBytes(buffer, usesPushDescriptors ? 1u : 0u);
        BitConverter.TryWriteBytes(buffer[4..], (uint)pushConstantStages);
        BitConverter.TryWriteBytes(buffer[8..], pushConstantBytes);
        BitConverter.TryWriteBytes(buffer[12..], bindings.Count);
        hash.AppendData(buffer[..16]);
        foreach (var binding in bindings)
        {
            BitConverter.TryWriteBytes(buffer, binding.Binding);
            BitConverter.TryWriteBytes(buffer[4..], (uint)binding.DescriptorType);
            BitConverter.TryWriteBytes(buffer[8..], binding.DescriptorCount);
            BitConverter.TryWriteBytes(buffer[12..], (uint)binding.StageFlags);
            hash.AppendData(buffer[..16]);
        }

        return new ComputePipelineContentKey(moduleIdentity, Convert.ToHexString(hash.GetHashAndReset()), requiredSubgroupSize);
    }
}
