// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class RenderScratchPoolTests
{
    [Fact]
    public void InFlightLeasesAreDistinctAndReturnedStorageIsZeroed()
    {
        var pool = new RenderScratchPool<uint>();
        var first = pool.Rent(17);
        var second = pool.Rent(17);
        first.AsSpan().Fill(0xFFFFFFFF);
        second.AsSpan().Fill(42);
        Assert.NotSame(first, second);
        pool.Return(first);
        var reused = pool.Rent(17);
        Assert.Same(first, reused);
        Assert.All(reused, value => Assert.Equal(0u, value));
        Assert.All(second, value => Assert.Equal(42u, value));
        Assert.Equal(17, reused.Length);
        Assert.Equal(18, pool.Rent(18).Length);
    }

    [Fact]
    public void ReturnReleasesResourceReferences()
    {
        var pool = new RenderScratchPool<object>();
        var array = pool.Rent(2);
        array[0] = new object();
        array[1] = new object();
        pool.Return(array);
        Assert.All(array, Assert.Null);
        Assert.Same(array, pool.Rent(2));
    }

    [Fact]
    public void WarmLeaseCycleAllocatesNothing()
    {
        var pool = new RenderScratchPool<uint>();
        for (var i = 0; i < 100; i++) pool.Return(pool.Rent(31));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) pool.Return(pool.Rent(31));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PoolBoundsRetainedMemoryAndShapeCount()
    {
        var pool = new RenderScratchPool<byte>(100);
        var arrays = Enumerable.Range(0, 8).Select(_ => pool.Rent(20)).ToArray();
        foreach (var array in arrays) pool.Return(array);
        Assert.Equal(80, pool.RetainedBytes); // At most four per shape.
        pool.Return(new byte[101]);
        Assert.Equal(80, pool.RetainedBytes);
        pool.Return(new byte[21]);
        Assert.Equal(80, pool.RetainedBytes);
        var shapes = new RenderScratchPool<byte>(100_000);
        for (var length = 1; length <= 200; length++) shapes.Return(shapes.Rent(length));
        Assert.Equal(128 * 129 / 2, shapes.RetainedBytes);
    }
}
