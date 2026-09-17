// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class ImageQueryResultsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(65)]
    public void ResultsKeepOrderAndSupportRepeatedEnumeration(int count)
    {
        var results = Create(count);
        Assert.Equal(count, results.Count);
        for (var pass = 0; pass < 2; pass++)
        {
            var index = 0;
            foreach (var identifier in results)
                Assert.Equal(new ResourceSlotIdentifier((uint)index++, 7), identifier);
            Assert.Equal(count, index);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => results[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => results[count]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void InlineQueriesDoNotAllocate(int count)
    {
        for (var warmup = 0; warmup < 100; warmup++) Sum(count);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0UL;
        for (var iteration = 0; iteration < 1000; iteration++) sum += Sum(count);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal((ulong)(count * (count - 1) / 2) * 1000, sum);
    }

    private static ImageQueryResults Create(int count)
    {
        var results = new ImageQueryResults();
        for (var index = 0; index < count; index++) results.Add(new ResourceSlotIdentifier((uint)index, 7));
        return results;
    }

    private static ulong Sum(int count)
    {
        ulong sum = 0;
        foreach (var identifier in Create(count)) sum += identifier.Index;
        return sum;
    }
}
