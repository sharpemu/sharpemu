// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace SharpEmu.ShaderCompiler.Resources;

// SHARPEMU_RUNTIME_IMAGE_SWIZZLE (on by default; =0 off): the destination select of a storage image (T# word 3, bits 0-11)
// is shader data instead of part of the module. Of a storage image with no conversion format, only a store reads the
// select: it puts each logical component in the physical channel the select names first (the other channels store zero,
// Gen5ShaderTranslator.GetImageStoreSourceIndex); its loads return the channels as stored. So the specialization keeps the
// identity select, the layout carries a store channel map per written image (BindingLayout.StorageImageSwizzlesDword), the
// descriptor path writes the maps from the bound T#s, and a store picks its channels by the map. Dispatches of one program
// over images whose selects differ share one module.
public static class RuntimeImageSwizzle
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("SHARPEMU_RUNTIME_IMAGE_SWIZZLE") != "0";

    // An image whose stores take their channel map from shader data: a storage image bound through its own T#, not an indirect
    // table's, with no conversion format. Heap and call-record images keep the identity select.
    public static bool Serves(ImageResource image) =>
        image.ResourceClass == ImageResourceClass.Storage && image.ConversionFormat == DescriptorConstants.InvalidFormat &&
        image.IndirectRoot == DescriptorConstants.NoIndex;

    // The store channel map of a destination select: per physical channel (3 bits, X first) the logical component whose selector
    // names it first (0-3), or 4 when none does and the channel stores zero. Each code is kept xor its channel, so the identity
    // select's map, and an unwritten slot, is zero.
    public static uint StoreChannelMap(uint destinationSelect)
    {
        var map = 0u;
        for (var channel = 0u; channel < 4; channel++)
        {
            var source = 4u;
            for (var component = 0u; component < 4; component++)
            {
                if (((destinationSelect >> (int)(component * 3)) & 7u) == 4u + channel)
                {
                    source = component;
                    break;
                }
            }

            map |= (source ^ channel) << (int)(channel * 3);
        }

        return map;
    }

    // The slot of an image's store channel map: its rank among the written images whose select is shader data, or -1
    // for any other image.
    public static int Slot(IReadOnlyList<ImageResource> images, int index)
    {
        if ((uint)index >= (uint)images.Count || !TakesMap(images[index]))
        {
            return -1;
        }

        var slot = 0;
        for (var other = 0; other < index; other++)
        {
            slot += TakesMap(images[other]) ? 1 : 0;
        }

        return slot;
    }

    public static uint SlotCount(IReadOnlyList<ImageResource> images)
    {
        var count = 0u;
        foreach (var image in images)
        {
            count += TakesMap(image) ? 1u : 0u;
        }

        return count;
    }

    // Each written image's map in its slot, two to a dword: from its materialized T#, the identity for a null or absent
    // descriptor (the select the module would have kept for it).
    public static void WriteStoreMaps(uint[] shaderData, BindingLayout layout, IReadOnlyList<ImageResource> images,
        IReadOnlyList<uint[]> descriptors)
    {
        if (layout.StorageImageSwizzleCount == 0)
        {
            return;
        }

        var first = (int)layout.StorageImageSwizzlesDword;
        Array.Clear(shaderData, first, (int)(layout.StorageImageSwizzleCount + 1) / 2);
        var slot = 0u;
        for (var index = 0; index < images.Count; index++)
        {
            if (!TakesMap(images[index]))
            {
                continue;
            }

            if (slot < layout.StorageImageSwizzleCount)
            {
                var map = StoreChannelMap(DestinationSelect(index < descriptors.Count ? descriptors[index] : null));
                shaderData[first + (int)(slot / 2)] |= map << (int)(slot % 2 * 16);
            }

            slot++;
        }

        if (slot != layout.StorageImageSwizzleCount)
        {
            throw new InvalidOperationException(
                $"The store channel maps do not match the layout: images={slot} layout={layout.StorageImageSwizzleCount}.");
        }
    }

    private static bool TakesMap(ImageResource image) => image.Written && Serves(image);

    private static uint DestinationSelect(uint[]? words) =>
        words is { Length: >= 8 } && (words[0] != 0 || (words[1] & 0xFF) != 0)
            ? words[3] & 0xFFF
            : DescriptorConstants.IdentityImageSwizzle;

    // A recorded specialization (a prewarm record written with the selects baked in) with the select of every image
    // whose select is shader data set to the identity, as the materializer keeps it; the same instance when nothing
    // changes.
    public static ResourceSpecialization Canonical(ShaderResourcePlan plan, ResourceSpecialization specialization)
    {
        if (!plan.Info.UsesRuntimeImageSwizzles)
        {
            return specialization;
        }

        ResourceSpecialization? canonical = null;
        var count = Math.Min(plan.Info.Images.Count, specialization.Images.Count);
        for (var index = 0; index < count; index++)
        {
            var image = specialization.Images[index];
            if (plan.Info.Images[index].ResourceClass != ImageResourceClass.Storage ||
                image.ConversionFormat != DescriptorConstants.InvalidFormat || image.IndirectRoot != DescriptorConstants.NoIndex ||
                image.ShaderSwizzle == DescriptorConstants.IdentityImageSwizzle)
            {
                continue;
            }

            canonical ??= specialization.Clone();
            canonical.Images[index] = image with { ShaderSwizzle = DescriptorConstants.IdentityImageSwizzle };
        }

        return canonical ?? specialization;
    }
}
