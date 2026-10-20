// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

// The contract between a shader that reads its image and sampler descriptors at run time
// (MemoryAccessInfo.RuntimeDescriptor) and the host that resolves them.
//
// The hardware reads a T# and an S# from memory into scalar registers and the sampler unit
// consumes those words directly. A host image view cannot be made from registers, so the
// shader hashes the words it holds and looks them up in a table the host fills: an entry maps
// the image or sampler words to a slot of the persistent descriptor heap (a partially bound,
// update-after-bind array of sampled images and one of samplers). The shader then indexes the
// heap with that slot.
//
// Latency: the host can only learn which descriptors exist from the shader itself. A
// descriptor the table does not hold yet reads slot 0 (the null image, the default sampler),
// so the access returns black for that draw, and is appended to the miss buffer. The host reads
// the miss buffer when the tick completes, creates the views and samplers, and rebuilds the
// table; draws recorded after that find the descriptor. A descriptor therefore shows its real
// texture from the first draw recorded after the tick that missed it completed.
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
//   from ImageSeenOffset / SamplerSeenOffset: a bitmap of SeenBits bits per table
// Every invocation of a draw that holds a missing descriptor would append the same record, filling the
// buffer with duplicates before a second descriptor is seen. A shader therefore sets the bit its key
// hashes to (atomic or) and records the miss only when it was the one that set the bit: each distinct
// key is recorded once per buffer. Two keys sharing a bit are recorded in different ticks. A miss beyond
// the record capacity is counted but not stored; the descriptor misses again in a later draw.
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
    public const uint SeenBits = 2048;
    public const uint SeenDwords = SeenBits / 32;
    public const uint ImageSeenOffset = SamplerMissOffset + SamplerMissCapacity * SamplerMissRecordDwords;
    public const uint SamplerSeenOffset = ImageSeenOffset + SeenDwords;
    public const uint MissBufferDwords = SamplerSeenOffset + SeenDwords;

    // The descriptor set that holds the heap, and its bindings: sampled images, then samplers. Slot 0
    // of both is the null image and the default sampler.
    public const uint HeapDescriptorSet = 1;
    public const uint HeapSampledImageBinding = 0;
    public const uint HeapSamplerBinding = 1;

    private const uint MaxSectionEntries = 1u << 20;

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

    // The view a runtime-descriptor access declares. Sampled accesses always use the float
    // view (the dimension alone); an image load uses the numeric class its descriptor format
    // implies, because a Vulkan image load needs a view of the matching numeric type.
    public const uint ViewClassNumericShift = 8;
    private const uint ViewClassDimensionMask = (1u << (int)ViewClassNumericShift) - 1;

    // A 128-bit resource carries four descriptor words, with the fields a view needs in other places than
    // the eight-word form, so the host must read the key as the shape the instruction declared.
    public const uint ViewClassR128Bit = 1u << 16;

    public static uint ViewClass(ImageDimension dimension) => ViewClass(dimension, ImageNumericClass.Float);

    public static uint ViewClass(ImageDimension dimension, ImageNumericClass numericClass, bool r128 = false) =>
        (uint)dimension | (NumericClassCode(numericClass) << (int)ViewClassNumericShift) | (r128 ? ViewClassR128Bit : 0);

    public static bool ViewR128(uint viewClass) => (viewClass & ViewClassR128Bit) != 0;

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

    // The dimensions the heap holds. Cube and multisampled views are not part of it.
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

        var capacity = Math.Max(16u, System.Numerics.BitOperations.RoundUpToPowerOf2((uint)entries.Count * 2));
        for (; capacity <= MaxSectionEntries; capacity *= 2)
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

        throw new InvalidOperationException($"{entries.Count} runtime descriptor keys cannot be placed within {ProbeCount} probes.");
    }
}
