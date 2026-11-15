// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

[Collection(SchedulingStateCollection.Name)]
public sealed class PublicationGapTests
{
    private static PendingGuestPublications Create()
    {
        var pending = new PendingGuestPublications { ReusePublicationGaps = true };
        pending.Register(pending.Add(1, 0x1000, 0x10), 1);
        pending.Register(pending.Add(1, 0x2000, 0x10), 2);
        return pending;
    }

    [Fact]
    public void GapServesDifferentRangesAndHonorsItsExactEdges()
    {
        var pending = Create();
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false));
        Assert.False(pending.HasOverlap(0x1010, 0xFF0, includeLabels: false));
        Assert.True(pending.HasOverlap(0x100F, 1, includeLabels: false));
        Assert.Equal(2UL, pending.OverlapTick(0x1FFF, 2, includeLabels: false));
    }

    [Fact]
    public void AddingAndPublishingDumpInvalidateTheSnapshot()
    {
        var pending = Create();
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false));
        var inserted = pending.Add(2, 0x1500, 4);
        pending.Register(inserted, 9);
        Assert.True(pending.HasOverlap(0x1500, 4, includeLabels: false));
        Assert.Equal(9UL, pending.OverlapTick(0x1500, 4, includeLabels: false));
        pending.Publish(inserted, () => { });
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false));
    }

    [Fact]
    public void SkippedGpuSlotAndPublishingEntryCannotProveAGap()
    {
        var pending = Create();
        var entry = pending.Add(1, 0x1500, 4);
        pending.Register(entry, 8);
        entry.GpuSlot = 0;
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false, skipGpuSlots: true));
        entry.GpuSlot = -1; // Same array, mutable eligibility changed.
        Assert.Equal(8UL, pending.OverlapTick(0x1500, 4, includeLabels: false, skipGpuSlots: true));
        entry.Publishing = true;
        Assert.Equal(0UL, pending.OverlapTick(0x1500, 4, includeLabels: false));
        entry.Publishing = false;
        Assert.Equal(8UL, pending.OverlapTick(0x1500, 4, includeLabels: false));
    }

    [Fact]
    public void FailureIsNotHiddenByACachedGap()
    {
        var pending = Create();
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false));
        pending.Fail(new Exception("publication failed"));
        Assert.Throws<InvalidOperationException>(() => pending.HasOverlap(0x1500, 4, includeLabels: false));
        Assert.Throws<InvalidOperationException>(() => pending.OverlapTick(0x1500, 4, includeLabels: false));
    }

    [Fact]
    public void OtherOwnersAndThreadsDoNotBorrowTheGap()
    {
        var pending = Create();
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false));
        var other = Create();
        other.Register(other.Add(2, 0x1500, 4), 7);
        Assert.True(other.HasOverlap(0x1500, 4, includeLabels: false));
        var threadResult = true;
        var thread = new Thread(() => threadResult = pending.HasOverlap(0x2500, 4, includeLabels: false));
        thread.Start();
        thread.Join();
        Assert.False(threadResult);
        Assert.False(pending.HasOverlap(0x1700, 4, includeLabels: false));
        Assert.True(pending.HasOverlap(0x2000, 1, includeLabels: false));
    }

    [Fact]
    public void DisabledModeAndInvalidRangesKeepReferenceBehavior()
    {
        var pending = Create();
        pending.ReusePublicationGaps = false;
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false));
        Assert.False(pending.HasOverlap(0x1500, 4, includeLabels: false));
        Assert.False(pending.HasOverlap(ulong.MaxValue, 4, includeLabels: false));
        Assert.False(pending.HasOverlap(0x1000, 0, includeLabels: false));
        pending.ReusePublicationGaps = true;
        Assert.False(pending.HasOverlap(0x2500, ulong.MaxValue - 0x2500, includeLabels: false));
        Assert.False(pending.HasOverlap(ulong.MaxValue - 1, 1, includeLabels: false));
    }
}
