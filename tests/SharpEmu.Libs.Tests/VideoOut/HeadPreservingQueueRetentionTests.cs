// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class HeadPreservingQueueRetentionTests
{
    [Fact]
    public void LeavesQueueUnchangedWhenWithinLimit()
    {
        var pending = new LinkedList<int>([1, 2, 3, 4]);

        var coalesced = HeadPreservingQueueRetention.CoalesceIntermediateItems(
            pending,
            maximumCount: 4);

        Assert.Equal(0, coalesced);
        Assert.Equal([1, 2, 3, 4], pending);
    }

    [Fact]
    public void RetainsHeadAndNewestTailWhenOverloaded()
    {
        var pending = new LinkedList<int>([1, 2, 3, 4, 5, 6, 7, 8]);

        var coalesced = HeadPreservingQueueRetention.CoalesceIntermediateItems(
            pending,
            maximumCount: 4);

        Assert.Equal(4, coalesced);
        Assert.Equal([1, 6, 7, 8], pending);
    }

    [Fact]
    public void RepeatedOverloadNeverEvictsUnconsumedHead()
    {
        var pending = new LinkedList<int>([1, 2, 3, 4]);

        pending.AddLast(5);
        Assert.Equal(
            1,
            HeadPreservingQueueRetention.CoalesceIntermediateItems(pending, 4));
        Assert.Equal([1, 3, 4, 5], pending);

        pending.AddLast(6);
        Assert.Equal(
            1,
            HeadPreservingQueueRetention.CoalesceIntermediateItems(pending, 4));
        Assert.Equal([1, 4, 5, 6], pending);
    }

    [Fact]
    public void ReportsExactIntermediateItemsThatWereCoalesced()
    {
        var pending = new LinkedList<int>([1, 2, 3, 4, 5, 6]);
        List<int> removed = [];

        var coalesced = HeadPreservingQueueRetention.CoalesceIntermediateItems(
            pending,
            maximumCount: 4,
            onCoalesced: removed.Add);

        Assert.Equal(2, coalesced);
        Assert.Equal([2, 3], removed);
        Assert.Equal([1, 4, 5, 6], pending);
    }

    [Fact]
    public void OrderedFlipYieldsToPresenter()
    {
        var flip = new VulkanOrderedGuestFlip(
            Version: 1,
            VideoOutHandle: 1,
            DisplayBufferIndex: 0,
            Address: 0x1000,
            Width: 1920,
            Height: 1080,
            PitchInPixel: 1920);

        Assert.True(GuestPresentationScheduling.ShouldYieldToPresenter(flip));
    }

    [Fact]
    public void NonFlipGuestWorkContinuesDraining()
    {
        var wait = new VulkanOrderedGuestFlipWait(
            Version: 1,
            VideoOutHandle: 1,
            DisplayBufferIndex: 0);

        Assert.False(GuestPresentationScheduling.ShouldYieldToPresenter(wait));
        Assert.False(GuestPresentationScheduling.ShouldYieldToPresenter(
            new VulkanOrderedGuestAction(() => { }, "test")));
    }

    [Fact]
    public void YieldAfterEveryFlipExposesEveryGenerationInOrder()
    {
        var pending = new LinkedList<int>();
        List<int> presented = [];

        for (var version = 1; version <= 12; version++)
        {
            pending.AddLast(version);
            HeadPreservingQueueRetention.CoalesceIntermediateItems(pending, 4);

            var flip = new VulkanOrderedGuestFlip(
                Version: version,
                VideoOutHandle: 1,
                DisplayBufferIndex: 0,
                Address: 0x1000,
                Width: 1920,
                Height: 1080,
                PitchInPixel: 1920);
            if (GuestPresentationScheduling.ShouldYieldToPresenter(flip))
            {
                presented.Add(pending.First!.Value);
                pending.RemoveFirst();
            }
        }

        Assert.Equal(Enumerable.Range(1, 12), presented);
        Assert.Empty(pending);
    }
}
