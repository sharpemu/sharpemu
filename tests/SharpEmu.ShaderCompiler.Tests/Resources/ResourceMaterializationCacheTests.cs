// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ResourceMaterializationCacheTests
{
    private const ulong HeapBase = 0x1000;

    // A mask word, a two-entry index table and six 32-byte image records.
    private sealed class Heap
    {
        public readonly Dictionary<ulong, uint> Words = new();
        public int Reads;
        public bool GpuOwned;

        public Heap()
        {
            Words[HeapBase + 0x80] = (1u << 1) | (1u << 4);
            Words[HeapBase + 0x40 + 0x90] = 2;
            Words[HeapBase + 0x40 + 4 * 0x90] = 5;
            foreach (var record in new uint[] { 2, 5 })
            {
                var entry = HeapBase + 0x100 + record * 32;
                for (uint dword = 0; dword < 8; dword++)
                    Words[entry + dword * 4] = dword switch
                    {
                        0 => (record & 1) == 0 ? 0x2000u : 0x1000u,
                        1 => 20u << 20,
                        3 => 0xFACu | (9u << 28),
                        _ => 0,
                    };
            }
        }

        public bool Read(ulong address, out uint word)
        {
            Reads++;
            return Words.TryGetValue(address, out word);
        }

        public bool ReadResident(ulong address, Span<byte> destination, bool clean)
        {
            if (GpuOwned)
                return false;
            for (var offset = 0; offset < destination.Length; offset += 4)
            {
                if (!Words.TryGetValue(address + (ulong)offset, out var word))
                    return false;
                BitConverter.TryWriteBytes(destination[offset..], word);
            }

            return true;
        }
    }

    private static ShaderResourcePlan Plan() =>
        ShaderResourcePlan.Extract(DirectImageTableTests.CreateWaveIndexedDescriptorProgram(), ShaderStage.Compute, Hash, 0, 2);

    private static bool Run(ResourceMaterializationCache cache, ShaderResourcePlan plan, Heap heap, uint[] userData,
        out ResourceSnapshot snapshot, out ResourceSpecialization specialization, ulong shaderBase = 0)
    {
        snapshot = new ResourceSnapshot();
        specialization = new ResourceSpecialization();
        return cache.Materialize(plan, Inputs(userData, readCleanMemory: heap.Read, shaderBase: shaderBase), heap.ReadResident,
            ref snapshot, ref specialization, out _);
    }

    [Fact]
    public void UnchangedMemoryReusesTheMaterialization()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out var firstSpecialization));
        var readsAfterFirst = heap.Reads;
        Assert.True(readsAfterFirst > 0);

        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out var secondSpecialization));
        Assert.Equal(readsAfterFirst, heap.Reads);
        Assert.Same(first, second);
        Assert.Same(firstSpecialization, secondSpecialization);
        Assert.Equal((1, 1), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AChangedDescriptorWordMaterializesAgain()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));

        heap.Words[HeapBase + 0x100 + 5 * 32] = 0x3000;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _));
        Assert.NotSame(first, second);
        Assert.Equal((0, 2), (cache.Hits, cache.Misses));

        // An uncached full walk gives the same result as the re-materialization.
        var reference = new ResourceSnapshot();
        var referenceSpecialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs([0x1000, 0], readCleanMemory: heap.Read),
            ref reference, ref referenceSpecialization));
        Assert.Equal(reference.Images.Select(image => image.ToArray()), second.Images.Select(image => image.ToArray()));
    }

    [Fact]
    public void AChangedMaskMaterializesAgain()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        heap.Words[HeapBase + 0x80] = 1u << 1;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void GpuOwnedMemoryIsNeverTrusted()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        heap.GpuOwned = true;
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void ADifferentDrawKeyIsADifferentEntry()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache();
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var first, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var second, out _, shaderBase: 0x100));
        Assert.NotSame(first, second);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out var third, out _));
        Assert.Same(first, third);
        Assert.Equal((1, 2), (cache.Hits, cache.Misses));
    }

    [Fact]
    public void AFailedReadIsNotCached()
    {
        var plan = Plan();
        var heap = new Heap();
        heap.Words.Remove(HeapBase + 0x100 + 5 * 32 + 4);
        var cache = new ResourceMaterializationCache();
        var ok = Run(cache, plan, heap, [0x1000, 0], out _, out _);
        var reads = heap.Reads;
        Assert.Equal(ok, Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.True(heap.Reads > reads);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void AFullGenerationKeepsRecentEntries()
    {
        var plan = Plan();
        var heap = new Heap();
        var cache = new ResourceMaterializationCache(generationCapacity: 1);
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _, shaderBase: 0x100));
        // The first entry moved to the old generation and is still found.
        Assert.True(Run(cache, plan, heap, [0x1000, 0], out _, out _));
        Assert.Equal(1, cache.Hits);
    }
}
