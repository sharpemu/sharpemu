// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(AllocationMeasurementCollection.Name)]
public sealed class MetadataWriteRangesTests
{
    [Fact]
    public void CoveragePreservesGapsAndMergesGrowth()
    {
        var ranges = new MetadataWriteRanges();
        ranges.Include(100, 10);
        ranges.Include(200, 10);
        Assert.False(ranges.Overlaps(110, 90));
        Assert.False(ranges.Overlaps(99, 1));
        Assert.True(ranges.Overlaps(99, 2));
        Assert.True(ranges.Overlaps(109, 1));
        Assert.True(ranges.Overlaps(150, 51));
        Assert.False(ranges.Overlaps(210, 1));
        ranges.Include(105, 3);
        Assert.False(ranges.Overlaps(110, 90));
        ranges.Include(110, 90);
        Assert.True(ranges.Overlaps(150, 1));
        Assert.True(ranges.Overlaps(209, 1));
        Assert.False(ranges.Overlaps(210, 1));
    }

    [Fact]
    public void CoverageHandlesZeroSizeAndAddressLimit()
    {
        var ranges = new MetadataWriteRanges();
        ranges.Include(0, 0);
        ranges.Include(ulong.MaxValue - 2, 10);
        Assert.True(ranges.Overlaps(0, 1));
        Assert.False(ranges.Overlaps(1, 1));
        Assert.False(ranges.Overlaps(0, 0));
        Assert.True(ranges.Overlaps(ulong.MaxValue, 1));
        Assert.True(ranges.Overlaps(ulong.MaxValue - 3, 2));
        Assert.False(ranges.Overlaps(ulong.MaxValue - 3, 1));
    }

    [Fact]
    public void RandomInsertionsMatchByteCoverage()
    {
        var ranges = new MetadataWriteRanges();
        var covered = new bool[512];
        var random = new System.Random(7341);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var address = random.Next(covered.Length);
            var size = random.Next(0, Math.Min(20, covered.Length - address) + 1);
            ranges.Include((ulong)address, (ulong)size);
            Array.Fill(covered, true, address, Math.Max(1, size));
            for (var query = 0; query < covered.Length; query++)
                Assert.Equal(covered[query], ranges.Overlaps((ulong)query, 1));
        }
    }

    [Fact]
    public void ReadsAndContainedRegistrationsDoNotAllocate()
    {
        var ranges = new MetadataWriteRanges();
        ranges.Include(100, 10);
        ranges.Include(200, 10);
        var hits = 0;
        void Read()
        {
            for (var iteration = 0; iteration < 1000; iteration++)
            {
                ranges.Include(101, 2);
                if (ranges.Overlaps(100, 1)) hits++;
                if (ranges.Overlaps(110, 90)) hits++;
            }
        }
        Read();
        hits = 0;
        Assert.Equal(0, AllocationMeasurementCollection.Measure(Read));
        Assert.Equal(1000, hits);
    }

    [Fact]
    public async Task PublishedCoverageRemainsVisibleDuringGrowth()
    {
        var ranges = new MetadataWriteRanges();
        ranges.Include(1, 1);
        using var started = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            started.Wait();
            for (ulong address = 4; address < 4000; address += 4) ranges.Include(address, 1);
        });
        started.Set();
        do
        {
            Assert.True(ranges.Overlaps(1, 1));
            Assert.False(ranges.Overlaps(2, 1));
        } while (!writer.IsCompleted);
        await writer;
        for (ulong address = 4; address < 4000; address += 4)
        {
            Assert.True(ranges.Overlaps(address, 1));
            Assert.False(ranges.Overlaps(address + 1, 1));
        }
    }
}
