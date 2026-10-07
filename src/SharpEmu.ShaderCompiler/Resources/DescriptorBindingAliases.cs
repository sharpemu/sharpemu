// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

internal static class DescriptorBindingAliases
{
    private sealed class WordComparer : IEqualityComparer<uint[]>
    {
        public bool Equals(uint[]? left, uint[]? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(uint[] words)
        {
            var hash = new HashCode();
            foreach (var word in words) hash.Add(word);
            return hash.ToHashCode();
        }
    }

    public static List<uint> Group(IReadOnlyList<uint[]> descriptors)
    {
        if (descriptors.Count < 2) return [];
        var groups = new Dictionary<uint[], uint>(new WordComparer());
        var result = new List<uint>(descriptors.Count);
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (!groups.TryGetValue(descriptors[index], out var group))
            {
                group = (uint)index;
                groups.Add(descriptors[index], group);
            }
            result.Add(group);
        }
        return groups.Count == descriptors.Count ? [] : result;
    }

    public static void Apply(ShaderResourceInfo info, ShaderResourceInfo source, ResourceSpecialization specialization)
    {
        if (specialization.ImageDescriptorGroups.Count != 0)
        {
            if (specialization.ImageDescriptorGroups.Count != info.Images.Count)
                throw new ResourcePlanException("The image descriptor groups do not match the resource table.");
            info.ImageBindings = new uint[info.Images.Count];
            var bindings = new Dictionary<(uint Group, ImageSpecialization View, ImageResourceClass Class,
                ImageMipMode Mip, bool Read, bool Written, bool Atomic, bool Depth, bool R128), uint>();
            for (var index = 0; index < info.Images.Count; index++)
            {
                var image = info.Images[index];
                var key = (specialization.ImageDescriptorGroups[index], specialization.Images[index],
                    image.ResourceClass, image.MipMode, image.Read, image.Written, image.Atomic, image.DepthCompare, image.R128);
                // Indirect tables retain their candidate order and contiguous mip elements.
                if (image.IndirectRoot != DescriptorConstants.NoIndex || !bindings.TryGetValue(key, out var binding))
                {
                    binding = (uint)index;
                    if (image.IndirectRoot == DescriptorConstants.NoIndex) bindings.Add(key, binding);
                }
                info.ImageBindings[index] = binding;
            }
        }

        if (specialization.SamplerDescriptorGroups.Count != 0)
        {
            if (specialization.SamplerDescriptorGroups.Count != source.Samplers.Count)
                throw new ResourcePlanException("The sampler descriptor groups do not match the resource table.");
            var sourceGroups = new Dictionary<uint, uint>();
            for (var index = 0; index < source.Samplers.Count; index++)
                sourceGroups.Add(source.Samplers[index].Source, specialization.SamplerDescriptorGroups[index]);
            info.SamplerBindings = new uint[info.Samplers.Count];
            var bindings = new Dictionary<(uint Group, bool Point, bool Depth), uint>();
            for (var index = 0; index < info.Samplers.Count; index++)
            {
                var sampler = info.Samplers[index];
                var key = (sourceGroups[sampler.Source], sampler.ForcePointFiltering, sampler.DepthCompare);
                if (!bindings.TryGetValue(key, out var binding))
                {
                    binding = (uint)index;
                    bindings.Add(key, binding);
                }
                info.SamplerBindings[index] = binding;
            }
        }
    }
}
