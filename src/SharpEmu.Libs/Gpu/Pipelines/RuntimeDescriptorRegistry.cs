// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Libs.Gpu.Pipelines;

// Up to nine key dwords (the view class and eight image words, or four sampler words) with value equality.
internal readonly record struct RuntimeDescriptorKey(ulong Words01, ulong Words23, ulong Words45, ulong Words67, uint Word8, int Count)
{
    public static RuntimeDescriptorKey From(ReadOnlySpan<uint> words)
    {
        Span<uint> padded = stackalloc uint[9];
        padded.Clear();
        words.CopyTo(padded);
        static ulong Pair(Span<uint> values, int index) => values[index] | ((ulong)values[index + 1] << 32);
        return new RuntimeDescriptorKey(Pair(padded, 0), Pair(padded, 2), Pair(padded, 4), Pair(padded, 6), padded[8], words.Length);
    }
}

// One image a shader reached at run time, as the table names it.
internal sealed class RuntimeImageEntry<TResolved>(uint[] key) where TResolved : class
{
    // The view class followed by the eight image words, as the shader hashes them.
    public uint[] Key { get; } = key;

    public ImageDimension Dimension => RuntimeDescriptorTable.ViewDimension(Key[0]);

    public ImageNumericClass NumericClass => RuntimeDescriptorTable.ViewNumericClass(Key[0]);

    public bool R128 => RuntimeDescriptorTable.ViewR128(Key[0]);

    // The heap slot the entry had at the last draw that committed it; 0 until one has.
    public uint Slot { get; set; }

    // What the words resolved to on the host, kept while it stays valid.
    public TResolved? Resolved { get; set; }
}

internal sealed class RuntimeSamplerEntry(uint[] key, uint slot)
{
    public uint[] Key { get; } = key;

    public uint Slot { get; } = slot;
}

// What one shader has told the host about the descriptors it reads at run time (RuntimeDescriptorTable): the
// misses its completed draws recorded, the images and samplers they became, and the table that names them.
// The host-specific work (is the memory behind a descriptor mapped, which heap slot a sampler takes) is passed
// in, so the bookkeeping stands alone.
internal sealed class RuntimeDescriptorRegistry<TResolved> where TResolved : class
{
    // How many ticks a descriptor that named no usable texture is left alone: its memory may be mapped later.
    public const ulong RejectionTicks = 120;

    private readonly Dictionary<RuntimeDescriptorKey, RuntimeImageEntry<TResolved>> _images = new();
    private readonly Dictionary<RuntimeDescriptorKey, RuntimeSamplerEntry> _samplers = new();
    private readonly Dictionary<RuntimeDescriptorKey, ulong> _rejected = new();

    // Misses read from completed ticks, drained when the next draw of the shader is prepared.
    private readonly ConcurrentQueue<(bool Image, uint[] Key)> _misses = new();

    public Dictionary<RuntimeDescriptorKey, RuntimeImageEntry<TResolved>> Images => _images;

    public int SamplerCount => _samplers.Count;

    public int RejectedCount => _rejected.Count;

    public int PendingMisses => _misses.Count;

    // The table no longer matches the entries and must be built again.
    public bool TableDirty { get; set; } = true;

    // Reads the miss buffer of a completed tick (its dwords, as laid out by RuntimeDescriptorTable). Returns the
    // number of records taken.
    public int ReadMisses(ReadOnlySpan<uint> words)
    {
        var imageCount = Math.Min(words[(int)RuntimeDescriptorTable.ImageMissCountDword], RuntimeDescriptorTable.ImageMissCapacity);
        var samplerCount = Math.Min(words[(int)RuntimeDescriptorTable.SamplerMissCountDword], RuntimeDescriptorTable.SamplerMissCapacity);
        for (uint record = 0; record < imageCount; record++)
        {
            var start = (int)(RuntimeDescriptorTable.ImageMissOffset + record * RuntimeDescriptorTable.ImageMissRecordDwords);
            _misses.Enqueue((true, words.Slice(start, (int)RuntimeDescriptorTable.ImageMissRecordDwords).ToArray()));
        }

        for (uint record = 0; record < samplerCount; record++)
        {
            var start = (int)(RuntimeDescriptorTable.SamplerMissOffset + record * RuntimeDescriptorTable.SamplerMissRecordDwords);
            _misses.Enqueue((false, words.Slice(start, (int)RuntimeDescriptorTable.SamplerMissRecordDwords).ToArray()));
        }

        return (int)(imageCount + samplerCount);
    }

    // Turns the queued misses into entries. isUsableImage is asked about an image key whose dimension the heap
    // supports; samplerSlot makes the host sampler of the (compare-free) sampler words and returns its heap slot.
    public void RegisterMisses(ulong tick, Func<RuntimeImageEntry<TResolved>, bool> isUsableImage, Func<uint[], uint> samplerSlot)
    {
        while (_misses.TryDequeue(out var miss))
        {
            var key = RuntimeDescriptorKey.From(miss.Key);
            if (miss.Image)
            {
                if (_images.ContainsKey(key) || (_rejected.TryGetValue(key, out var until) && tick < until))
                {
                    continue;
                }

                // A descriptor that describes no host texture (a null base, an invalid format, memory that is
                // not mapped) stays out of the table, so the shader keeps reading the null image for it, as the
                // hardware does.
                var entry = new RuntimeImageEntry<TResolved>(miss.Key);
                if (RuntimeDescriptorTable.SupportsRuntimeView(entry.Dimension) && isUsableImage(entry))
                {
                    _rejected.Remove(key);
                    _images.Add(key, entry);
                    TableDirty = true;
                }
                else
                {
                    _rejected[key] = tick + RejectionTicks;
                }

                continue;
            }

            if (!_samplers.ContainsKey(key))
            {
                // The compare bits of S# word 0 only matter to depth-compare samples, which never read the heap;
                // a sampler made with them would compare, and a color image cannot be compared.
                var native = new[] { miss.Key[0] & ~(0x7u << 12), miss.Key[1], miss.Key[2], miss.Key[3] };
                _samplers.Add(key, new RuntimeSamplerEntry(miss.Key, samplerSlot(native)));
                TableDirty = true;
            }
        }
    }

    public void Remove(IEnumerable<RuntimeDescriptorKey> keys)
    {
        foreach (var key in keys)
        {
            _images.Remove(key);
        }

        TableDirty = true;
    }

    // The table words of the entries that have a heap slot.
    public uint[] BuildTable()
    {
        var images = _images.Values.Where(entry => entry.Slot != 0).Select(entry => (entry.Key, entry.Slot)).ToList();
        var samplers = _samplers.Values.Select(entry => (entry.Key, entry.Slot)).ToList();
        TableDirty = false;
        return RuntimeDescriptorTable.Build(images, samplers);
    }

    public static ReadOnlySpan<uint> AsWords(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, uint>(bytes);
}
