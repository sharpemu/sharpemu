// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class PendingStorePagesTests
{
    private const ulong Page = 0x1000;

    // A store kept as one range answers as the page index would: every page from its first byte to its last, and no other.
    [Fact]
    public void ALargeStoreCoversThePagesOfItsFirstAndLastBytesAndNoOthers()
    {
        var pages = new PendingStorePages();
        var address = 0x40000UL + 0x10;
        var length = (PendingStorePages.LargePages + 1) * Page;
        Assert.True(PendingStorePages.IsLarge(address, length));
        Assert.False(PendingStorePages.IsLarge(0x40000, PendingStorePages.LargePages * Page));
        pages.Add(address, length, large: true);
        var firstPage = address & ~(Page - 1);
        var lastByte = address + length - 1;
        var lastPage = lastByte & ~(Page - 1);

        Assert.True(pages.Overlaps(address, 1));
        Assert.True(pages.Overlaps(firstPage, 1));
        Assert.True(pages.Overlaps(lastByte, 1));
        Assert.True(pages.Overlaps(lastPage + Page - 1, 1));
        Assert.False(pages.Overlaps(firstPage - 1, 1));
        Assert.False(pages.Overlaps(lastPage + Page, 1));

        Assert.True(pages.OverlapsLarge(lastByte, lastByte + 1));
        Assert.False(pages.OverlapsLarge(lastByte + 1, lastByte + 2));
        Assert.False(pages.OverlapsLarge(address - 1, address));
    }

    // Small stores before and after a large one on its first page stay in the page index; once all of them leave in order,
    // nothing is left.
    [Fact]
    public void SmallStoresOnALargeStoresPageStayAfterItLeaves()
    {
        var pages = new PendingStorePages();
        var page = 0x80000UL;
        pages.Add(page + 0x100, 4, large: false);
        pages.Add(page, 20 * Page, large: true);
        pages.Add(page + 0x200, 8, large: false);
        Assert.True(pages.HasLarge);

        pages.Remove(page + 0x100, 4, large: false);
        pages.Remove(page, 20 * Page, large: true);
        Assert.False(pages.HasLarge);
        Assert.True(pages.Overlaps(page + 0x200, 8));
        Assert.True(pages.Overlaps(page + 0x100, 4));
        Assert.False(pages.Overlaps(page + 5 * Page, 4));

        pages.Remove(page + 0x200, 8, large: false);
        Assert.True(pages.IsEmpty);
        Assert.False(pages.Overlaps(page, Page));
    }

    // The bucket counts never hide a page the dictionary holds.
    [Fact]
    public void TheBucketCountsAgreeWithTheDictionary()
    {
        var pages = new PendingStorePages();
        var random = new System.Random(22);
        var queued = new Queue<(ulong Address, ulong Length)>();
        for (var step = 0; step < 4000; step++)
        {
            if (queued.Count < 64 && random.Next(3) != 0)
            {
                var store = ((ulong)random.Next(0, 1 << 20) * 4, (ulong)random.Next(1, 3 * (int)Page));
                pages.Add(store.Item1, store.Item2, large: false);
                queued.Enqueue(store);
            }
            else if (queued.Count != 0)
            {
                var store = queued.Dequeue();
                pages.Remove(store.Address, store.Length, large: false);
            }

            var address = (ulong)random.Next(0, 1 << 22);
            var size = (ulong)random.Next(1, 2 * (int)Page);
            Assert.Equal(pages.Overlaps(address, size, prefilter: false), pages.Overlaps(address, size, prefilter: true));
        }

        while (queued.Count != 0)
        {
            var store = queued.Dequeue();
            pages.Remove(store.Address, store.Length, large: false);
        }

        Assert.True(pages.IsEmpty);
    }
}
