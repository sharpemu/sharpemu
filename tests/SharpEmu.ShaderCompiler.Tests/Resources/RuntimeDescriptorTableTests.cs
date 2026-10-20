// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class RuntimeDescriptorTableTests
{
    // The lookup a shader performs: FNV-1a of the key, then up to ProbeCount consecutive entries.
    private static bool TryFind(uint[] table, bool image, uint[] key, out uint slot)
    {
        slot = 0;
        var capacity = table[image ? RuntimeDescriptorTable.ImageCapacityDword : RuntimeDescriptorTable.SamplerCapacityDword];
        if (capacity == 0)
        {
            return false;
        }

        var entries = table[image ? RuntimeDescriptorTable.ImageEntriesDword : RuntimeDescriptorTable.SamplerEntriesDword];
        var entryDwords = image ? RuntimeDescriptorTable.ImageEntryDwords : RuntimeDescriptorTable.SamplerEntryDwords;
        var hash = RuntimeDescriptorTable.Hash(key);
        for (uint probe = 0; probe < RuntimeDescriptorTable.ProbeCount; probe++)
        {
            var entry = entries + ((hash + probe) & (capacity - 1)) * entryDwords;
            if (table[entry] != 0 && table.AsSpan((int)entry + 1, key.Length).SequenceEqual(key))
            {
                slot = table[entry] - 1;
                return true;
            }
        }

        return false;
    }

    private static uint[] ImageKey(uint seed)
    {
        var key = new uint[1 + RuntimeDescriptorTable.ImageWordCount];
        key[0] = RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2D);
        for (var word = 1; word < key.Length; word++)
        {
            key[word] = unchecked(seed * 0x9E3779B9u + (uint)word * 0x85EBCA6Bu);
        }

        return key;
    }

    [Fact]
    public void EveryRegisteredKeyIsFoundAndAnUnknownKeyIsNot()
    {
        var images = Enumerable.Range(0, 3000).Select(index => (ImageKey((uint)index), (uint)index + 1)).ToList();
        var samplers = Enumerable.Range(0, 200).Select(index => (new uint[] { (uint)index, 1, 2, 3 }, (uint)index + 1)).ToList();

        var table = RuntimeDescriptorTable.Build(images, samplers);

        foreach (var (key, slot) in images)
        {
            Assert.True(TryFind(table, image: true, key, out var found));
            Assert.Equal(slot, found);
        }

        foreach (var (key, slot) in samplers)
        {
            Assert.True(TryFind(table, image: false, key, out var found));
            Assert.Equal(slot, found);
        }

        Assert.False(TryFind(table, image: true, ImageKey(99999), out _));
        Assert.False(TryFind(table, image: false, [9999, 1, 2, 3], out _));
    }

    [Fact]
    public void EmptyTableHasNoCapacity()
    {
        var table = RuntimeDescriptorTable.Build([], []);

        Assert.Equal(0u, table[(int)RuntimeDescriptorTable.ImageCapacityDword]);
        Assert.Equal(0u, table[(int)RuntimeDescriptorTable.SamplerCapacityDword]);
        Assert.False(TryFind(table, image: true, ImageKey(1), out _));
    }

    [Fact]
    public void RelatedKeysStayWithinTheProbeWindow()
    {
        // Keys that differ by a power-of-two multiple of their seed cluster in a small table, so the
        // builder has to find a capacity that places each within the probe window.
        var images = Enumerable.Range(0, 64).Select(index => (ImageKey((uint)index * 16), (uint)index)).ToList();
        var table = RuntimeDescriptorTable.Build(images, []);

        foreach (var (key, slot) in images)
        {
            Assert.True(TryFind(table, image: true, key, out var found));
            Assert.Equal(slot, found);
        }
    }

    [Fact]
    public void ViewClassCarriesTheDimensionAndTheNumericClass()
    {
        Assert.Equal((uint)ImageDimension.Dim3D, RuntimeDescriptorTable.ViewClass(ImageDimension.Dim3D));
        var sint = RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2DArray, ImageNumericClass.Sint);
        Assert.Equal(ImageDimension.Dim2DArray, RuntimeDescriptorTable.ViewDimension(sint));
        Assert.Equal(ImageNumericClass.Sint, RuntimeDescriptorTable.ViewNumericClass(sint));
        Assert.Equal(ImageNumericClass.Float, RuntimeDescriptorTable.ViewNumericClass(RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2D)));
    }

    [Fact]
    public void ViewClassKeepsA128BitResourceApartFromTheEightWordForm()
    {
        var wide = RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2D, ImageNumericClass.Uint, r128: true);

        Assert.NotEqual(RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2D, ImageNumericClass.Uint), wide);
        Assert.True(RuntimeDescriptorTable.ViewR128(wide));
        Assert.False(RuntimeDescriptorTable.ViewR128(RuntimeDescriptorTable.ViewClass(ImageDimension.Dim2D)));
        Assert.Equal(ImageDimension.Dim2D, RuntimeDescriptorTable.ViewDimension(wide));
        Assert.Equal(ImageNumericClass.Uint, RuntimeDescriptorTable.ViewNumericClass(wide));
    }

    [Theory]
    [InlineData(ImageDimension.Dim2D, true)]
    [InlineData(ImageDimension.Dim3D, true)]
    [InlineData(ImageDimension.Dim1DArray, true)]
    [InlineData(ImageDimension.Dim2DMsaa, false)]
    [InlineData(ImageDimension.Unknown, false)]
    public void OnlyPlainDimensionsHaveAHeapView(ImageDimension dimension, bool supported) =>
        Assert.Equal(supported, RuntimeDescriptorTable.SupportsRuntimeView(dimension));
}
