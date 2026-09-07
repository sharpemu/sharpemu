// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class FlexibleBackingPoolTests
{
    private const ulong Page = 0x4000;

    [Fact]
    public void QuotaRejectsBeforeAnyHostWork()
    {
        var pool = new FlexibleBackingPool(0, Page);
        var space = new RecordingSpace();
        Assert.False(pool.TryMap(space, Page, Page * 2, GuestPageProtection.Read, out _));
        Assert.Empty(space.Calls);
        Assert.Equal(Page, pool.Available);
    }

    [Fact]
    public void FragmentedFreeRangesProduceSeparateZeroedViews()
    {
        var pool = new FlexibleBackingPool(0, Page * 3);
        var space = new RecordingSpace();
        for (ulong index = 1; index <= 3; index++)
            Assert.True(pool.TryMap(space, index * Page, Page, GuestPageProtection.Write, out _));
        pool.Release(Page, Page);
        pool.Release(3 * Page, Page);
        space.Calls.Clear();
        Assert.True(pool.TryMap(space, 4 * Page, 2 * Page, GuestPageProtection.Write, out var blocks));
        Assert.Equal(new[] { 0UL, 2 * Page }, blocks.Select(block => block.Offset));
        Assert.Equal(new[] { "zero", "map", "zero", "map" }, space.Calls);
        Assert.Equal(3 * Page, pool.Used);
        pool.Release(4 * Page, 2 * Page);
        Assert.Equal(Page, pool.Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSecondBlockRestoresTheFirstAndKeepsFreeRanges(bool failZero)
    {
        var pool = new FlexibleBackingPool(0, 3 * Page);
        var space = new RecordingSpace();
        for (ulong index = 1; index <= 3; index++)
            Assert.True(pool.TryMap(space, index * Page, Page, GuestPageProtection.Write, out _));
        pool.Release(Page, Page);
        pool.Release(3 * Page, Page);
        space.Calls.Clear();
        space.FailMapAfter = failZero ? -1 : 1;
        space.FailZeroAfter = failZero ? 1 : -1;
        Assert.False(pool.TryMap(space, 4 * Page, 2 * Page, GuestPageProtection.Write, out _));
        Assert.Equal("unmap", space.Calls[^1]);
        Assert.Equal(Page, pool.Used);
        Assert.True(pool.TryMap(space, 4 * Page, 2 * Page, GuestPageProtection.Write, out var blocks));
        Assert.Equal(new[] { 0UL, 2 * Page }, blocks.Select(block => block.Offset));
    }

    private sealed class RecordingSpace : IGuestBackedSpace
    {
        public List<string> Calls { get; } = new();
        public int FailMapAfter = -1;
        public int FailZeroAfter = -1;
        public bool TryHoldRange(ulong address, ulong size) => true;
        public bool TryHoldRangeAtOrAbove(ulong start, ulong size, ulong alignment, out ulong address)
        {
            address = start;
            return true;
        }
        public bool TryMapBacked(ulong address, ulong size, ulong offset, GuestPageProtection protection,
            out HostViewFailure failure)
        {
            Calls.Add("map");
            failure = HostViewFailure.None;
            return FailMapAfter-- != 0;
        }
        public bool TryUnmapBacked(ulong address, ulong size)
        {
            Calls.Add("unmap");
            return true;
        }
        public bool IsBackedRange(ulong address, ulong size) => false;
        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => false;
        public bool TryReadBacking(ulong address, Span<byte> data) => false;
        public bool TryClearBacking(ulong offset, ulong size)
        {
            Calls.Add("zero");
            return FailZeroAfter-- != 0;
        }
        public bool IsBackedView(ulong address) => true;
    }
}
