// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// What the host learns from the miss buffers of a shader that reads its descriptors at run time.
public sealed class RuntimeDescriptorRegistryTests
{
    private static uint[] MissBuffer(uint[][] images, uint[][] samplers, uint? reportedImages = null, uint? reportedSamplers = null)
    {
        var words = new uint[RuntimeDescriptorTable.MissBufferDwords];
        words[RuntimeDescriptorTable.ImageMissCountDword] = reportedImages ?? (uint)images.Length;
        words[RuntimeDescriptorTable.SamplerMissCountDword] = reportedSamplers ?? (uint)samplers.Length;
        for (var record = 0; record < images.Length; record++)
        {
            images[record].CopyTo(words, (int)RuntimeDescriptorTable.ImageMissOffset + record * (int)RuntimeDescriptorTable.ImageMissRecordDwords);
        }

        for (var record = 0; record < samplers.Length; record++)
        {
            samplers[record].CopyTo(words, (int)RuntimeDescriptorTable.SamplerMissOffset + record * (int)RuntimeDescriptorTable.SamplerMissRecordDwords);
        }

        return words;
    }

    private static uint[] ImageKey(uint address, ImageDimension dimension = ImageDimension.Dim2D) =>
        [RuntimeDescriptorTable.ViewClass(dimension), address, 22u << 20, 0, 9u << 28, 0, 0, 0, 0];

    [Fact]
    public void MissRecordsBecomeEntriesAndTheTableNamesThemOnceTheyHaveSlots()
    {
        var registry = new RuntimeDescriptorRegistry<object>();

        var taken = registry.ReadMisses(MissBuffer([ImageKey(0x4000), ImageKey(0x4010)], [[1, 2, 3, 4]]));
        registry.RegisterMisses(10, _ => true, _ => 5);

        Assert.Equal(3, taken);
        Assert.Equal(2, registry.Images.Count);
        Assert.Equal(1, registry.SamplerCount);
        Assert.Equal(0, registry.PendingMisses);
        foreach (var (entry, slot) in registry.Images.Values.Zip([7u, 8u]))
        {
            entry.Slot = slot;
        }

        var table = registry.BuildTable();
        Assert.False(registry.TableDirty);
        Assert.Equal(16u, table[(int)RuntimeDescriptorTable.ImageCapacityDword]);
        var slotsInTable = Enumerable.Range(0, 16)
            .Select(index => table[(int)(RuntimeDescriptorTable.HeaderDwords + index * RuntimeDescriptorTable.ImageEntryDwords)])
            .Where(stored => stored != 0)
            .Order()
            .ToArray();
        Assert.Equal([8u, 9u], slotsInTable);
    }

    [Fact]
    public void AnEntryWithoutAHeapSlotStaysOutOfTheTable()
    {
        var registry = new RuntimeDescriptorRegistry<object>();
        registry.ReadMisses(MissBuffer([ImageKey(0x4000)], []));
        registry.RegisterMisses(1, _ => true, _ => 0);

        var table = registry.BuildTable();

        Assert.Equal(0u, table[(int)RuntimeDescriptorTable.ImageCapacityDword]);
    }

    [Fact]
    public void RecordsPastTheBufferCapacityAreNotRead()
    {
        var registry = new RuntimeDescriptorRegistry<object>();

        var taken = registry.ReadMisses(MissBuffer([ImageKey(0x4000)], [], reportedImages: 500, reportedSamplers: 0));

        // The counter says 500, the buffer holds 64 records and only the written ones carry data.
        Assert.Equal((int)RuntimeDescriptorTable.ImageMissCapacity, taken);
    }

    [Fact]
    public void ADescriptorThatNamesNoUsableTextureIsLeftAloneForAWhile()
    {
        var registry = new RuntimeDescriptorRegistry<object>();
        var asked = 0;
        bool Usable(RuntimeImageEntry<object> entry)
        {
            asked++;
            return entry.Key[1] != 0x4000 || asked > 1;
        }

        registry.ReadMisses(MissBuffer([ImageKey(0x4000)], []));
        registry.RegisterMisses(100, Usable, _ => 1);
        Assert.Empty(registry.Images);
        Assert.Equal(1, registry.RejectedCount);

        // Missed again during the rejection window: not asked about again.
        registry.ReadMisses(MissBuffer([ImageKey(0x4000)], []));
        registry.RegisterMisses(100 + RuntimeDescriptorRegistry<object>.RejectionTicks - 1, Usable, _ => 1);
        Assert.Equal(1, asked);
        Assert.Empty(registry.Images);

        // Past it, the memory may have been mapped meanwhile.
        registry.ReadMisses(MissBuffer([ImageKey(0x4000)], []));
        registry.RegisterMisses(100 + RuntimeDescriptorRegistry<object>.RejectionTicks, Usable, _ => 1);
        Assert.Single(registry.Images);
        Assert.Equal(0, registry.RejectedCount);
    }

    [Theory]
    [InlineData(ImageDimension.Dim2DMsaa)]
    [InlineData(ImageDimension.Unknown)]
    public void ADimensionTheHeapDoesNotHoldIsNeverOfferedToTheHost(ImageDimension dimension)
    {
        var registry = new RuntimeDescriptorRegistry<object>();
        registry.ReadMisses(MissBuffer([ImageKey(0x4000, dimension)], []));

        registry.RegisterMisses(1, _ => throw new InvalidOperationException("asked"), _ => 1);

        Assert.Empty(registry.Images);
    }

    [Fact]
    public void ASamplerIsMadeWithoutItsCompareBitsAndOnlyOncePerKey()
    {
        var registry = new RuntimeDescriptorRegistry<object>();
        var made = new List<uint[]>();
        uint Make(uint[] words)
        {
            made.Add(words);
            return 3;
        }

        registry.ReadMisses(MissBuffer([], [[0x7u << 12 | 0x5, 2, 3, 4]]));
        registry.RegisterMisses(1, _ => true, Make);
        registry.ReadMisses(MissBuffer([], [[0x7u << 12 | 0x5, 2, 3, 4]]));
        registry.RegisterMisses(2, _ => true, Make);

        Assert.Equal([0x5u, 2, 3, 4], Assert.Single(made));
        Assert.Equal(1, registry.SamplerCount);
    }

    [Fact]
    public void RemovedEntriesLeaveTheTable()
    {
        var registry = new RuntimeDescriptorRegistry<object>();
        registry.ReadMisses(MissBuffer([ImageKey(0x4000)], []));
        registry.RegisterMisses(1, _ => true, _ => 1);
        var (key, entry) = Assert.Single(registry.Images);
        entry.Slot = 4;
        Assert.NotEqual(0u, registry.BuildTable()[(int)RuntimeDescriptorTable.ImageCapacityDword]);

        registry.Remove([key]);

        Assert.True(registry.TableDirty);
        Assert.Equal(0u, registry.BuildTable()[(int)RuntimeDescriptorTable.ImageCapacityDword]);
    }
}
