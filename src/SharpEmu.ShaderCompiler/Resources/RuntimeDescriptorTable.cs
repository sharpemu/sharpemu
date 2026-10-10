// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The contract between a shader that reads its image and sampler descriptors at run time
// (MemoryAccessInfo.RuntimeDescriptor) and the host that resolves them.
//
// A waterfall loop or a per-pixel material pointer gives a descriptor no plan-time source,
// so the shader looks its words up in a global hash table the host fills: the image or
// sampler words map to a slot of the persistent bindless heap. A descriptor the table does
// not hold yet reads slot 0 (the null descriptor) and is appended to the miss buffer; the
// host creates it after the tick and inserts it, so a later draw finds it.
//
// Table buffer (dwords):
//   [0] image capacity (entries, a power of two, 0 when empty)
//   [1] sampler capacity (entries, a power of two, 0 when empty)
//   [2] first dword of the image entries
//   [3] first dword of the sampler entries
//   image entry:   slot + 1 (0 = empty), view class, image words 0..7
//   sampler entry: slot + 1 (0 = empty), sampler words 0..3
// Miss buffer (dwords):
//   [0] image misses written, [1] sampler misses written (atomic counters, may exceed the capacity)
//   from ImageMissOffset:   ImageMissCapacity records of (view class, image words 0..7)
//   from SamplerMissOffset: SamplerMissCapacity records of sampler words 0..3
public static class RuntimeDescriptorTable
{
    public const uint ImageWordCount = 8;
    public const uint SamplerWordCount = 4;

    public const uint ImageCapacityDword = 0;
    public const uint SamplerCapacityDword = 1;
    public const uint ImageEntriesDword = 2;
    public const uint SamplerEntriesDword = 3;
    public const uint HeaderDwords = 4;

    public const uint ImageEntryDwords = 2 + ImageWordCount;
    public const uint SamplerEntryDwords = 1 + SamplerWordCount;

    // Probes a lookup makes before it treats the descriptor as missing. The host never
    // places an entry further than this from its home position.
    public const uint ProbeCount = 8;

    public const uint ImageMissCountDword = 0;
    public const uint SamplerMissCountDword = 1;
    public const uint ImageMissCapacity = 64;
    public const uint SamplerMissCapacity = 64;
    public const uint ImageMissRecordDwords = 1 + ImageWordCount;
    public const uint SamplerMissRecordDwords = SamplerWordCount;
    public const uint ImageMissOffset = 4;
    public const uint SamplerMissOffset = ImageMissOffset + ImageMissCapacity * ImageMissRecordDwords;
    public const uint MissBufferDwords = SamplerMissOffset + SamplerMissCapacity * SamplerMissRecordDwords;

    // FNV-1a over the key dwords; the shader computes the same value.
    public const uint HashSeed = 0x811C9DC5;
    public const uint HashPrime = 0x01000193;

    public static uint Hash(ReadOnlySpan<uint> words)
    {
        var hash = HashSeed;
        foreach (var word in words)
        {
            hash = unchecked((hash ^ word) * HashPrime);
        }

        return hash;
    }

    // The view a runtime-descriptor access declares. Float views deliberately retain the
    // old dimension-only value so existing sampled-descriptor keys stay unchanged; integer
    // views occupy the next two 8-bit ranges.
    public const uint ViewClassNumericShift = 8;
    private const uint ViewClassDimensionMask = (1u << (int)ViewClassNumericShift) - 1;

    public static uint ViewClass(ImageDimension dimension) => ViewClass(dimension, ImageNumericClass.Float);

    public static uint ViewClass(ImageDimension dimension, ImageNumericClass numericClass) =>
        (uint)dimension | (NumericClassCode(numericClass) << (int)ViewClassNumericShift);

    public static ImageDimension ViewDimension(uint viewClass) =>
        (ImageDimension)(viewClass & ViewClassDimensionMask);

    public static ImageNumericClass ViewNumericClass(uint viewClass) =>
        ((viewClass >> (int)ViewClassNumericShift) & 0xff) switch
        {
            1 => ImageNumericClass.Uint,
            2 => ImageNumericClass.Sint,
            _ => ImageNumericClass.Float,
        };

    public static uint NumericClassCode(ImageNumericClass numericClass) => numericClass switch
    {
        ImageNumericClass.Uint => 1,
        ImageNumericClass.Sint => 2,
        _ => 0,
    };

    public static bool SupportsRuntimeView(ImageDimension dimension) =>
        dimension is ImageDimension.Dim1D or ImageDimension.Dim1DArray or ImageDimension.Dim2D or
            ImageDimension.Dim2DArray or ImageDimension.Dim3D;

    // The table words for the entries: every key sits within ProbeCount entries of its hash,
    // and a section grows until all of its keys fit.
    public static uint[] Build(
        IReadOnlyList<(uint[] Key, uint Slot)> images,
        IReadOnlyList<(uint[] Key, uint Slot)> samplers)
    {
        var imageSection = Place(images, ImageEntryDwords);
        var samplerSection = Place(samplers, SamplerEntryDwords);
        var imageCapacity = (uint)(imageSection.Length / ImageEntryDwords);
        var samplerCapacity = (uint)(samplerSection.Length / SamplerEntryDwords);
        var words = new uint[HeaderDwords + imageSection.Length + samplerSection.Length];
        words[ImageCapacityDword] = imageCapacity;
        words[SamplerCapacityDword] = samplerCapacity;
        words[ImageEntriesDword] = HeaderDwords;
        words[SamplerEntriesDword] = HeaderDwords + (uint)imageSection.Length;
        imageSection.CopyTo(words, (int)HeaderDwords);
        samplerSection.CopyTo(words, (int)(HeaderDwords + imageSection.Length));
        return words;
    }

    private static uint[] Place(IReadOnlyList<(uint[] Key, uint Slot)> entries, uint entryDwords)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        for (var capacity = (uint)Math.Max(16, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)entries.Count * 2)); ; capacity *= 2)
        {
            var section = new uint[capacity * entryDwords];
            var placed = true;
            foreach (var (key, slot) in entries)
            {
                var hash = Hash(key);
                var found = false;
                for (uint probe = 0; probe < ProbeCount && !found; probe++)
                {
                    var entry = ((hash + probe) & (capacity - 1)) * entryDwords;
                    if (section[entry] != 0)
                    {
                        continue;
                    }

                    section[entry] = slot + 1;
                    key.CopyTo(section, (int)entry + 1);
                    found = true;
                }

                if (!found)
                {
                    placed = false;
                    break;
                }
            }

            if (placed)
            {
                return section;
            }
        }
    }
}
