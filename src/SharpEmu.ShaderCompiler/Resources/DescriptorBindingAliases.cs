// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

internal static class DescriptorBindingAliases
{
    private sealed class WordComparer : IEqualityComparer<(uint[] Words, ImageSpecialization View)>
    {
        public bool Equals((uint[] Words, ImageSpecialization View) left, (uint[] Words, ImageSpecialization View) right) =>
            left.View.Equals(right.View) &&
            (ReferenceEquals(left.Words, right.Words) || left.Words.AsSpan().SequenceEqual(right.Words));

        public int GetHashCode((uint[] Words, ImageSpecialization View) key)
        {
            var hash = new HashCode();
            hash.Add(key.View);
            foreach (var word in key.Words) hash.Add(word);
            return hash.ToHashCode();
        }
    }

    // Images of an indirect table keep their own bindings (see Apply), so they take no part in a group: a table that pads
    // its candidates with null images does not change the specialization of its program.
    public static List<uint> Group(IReadOnlyList<uint[]> descriptors, IReadOnlyList<ImageSpecialization>? images = null)
    {
        if (descriptors.Count < 2) return [];
        var groups = new Dictionary<(uint[] Words, ImageSpecialization View), uint>(new WordComparer());
        var result = new List<uint>(descriptors.Count);
        var ungrouped = 0;
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (images is not null && images[index].IndirectRoot != DescriptorConstants.NoIndex)
            {
                result.Add((uint)index);
                ungrouped++;
                continue;
            }

            var key = (descriptors[index], images is null ? default : images[index]);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (uint)index;
                groups.Add(key, group);
            }
            result.Add(group);
        }
        return groups.Count + ungrouped == descriptors.Count ? [] : result;
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
