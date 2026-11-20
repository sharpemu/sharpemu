// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// The runs of set-1 elements the heap views of the draws hold. A game can read several tables in one frame (terrain
// tile records, material records), each through its own program: every view needs elements no other view holds.
public sealed class BindlessHeapViewTableTests
{
    private static readonly Action<BindlessHeapViewTable.Region> Unreleased = region =>
        Assert.Fail($"A view gave its elements up: first={region.View.First}.");

    private static BindlessHeapDescriptor Table(ulong address, uint records, uint stride = 440, uint immediate = 0) =>
        new(false, address, records, stride, immediate);

    [Fact]
    public void ViewsOfDifferentHeapsAndDescriptorsHoldDifferentElements()
    {
        var table = new BindlessHeapViewTable(1000);

        var terrain = table.Acquire(Table(0x1000, 49), tick: 1, completedTick: 0, Unreleased, out _)!;
        var masks = table.Acquire(Table(0x1000, 49, immediate: 64), tick: 1, completedTick: 0, Unreleased, out _)!;
        var materials = table.Acquire(Table(0x9000, 200, stride: 136, immediate: 48), tick: 1, completedTick: 0, Unreleased, out _)!;

        Assert.Equal(new HeapView(0, 440, 0, 49), terrain.View);
        Assert.Equal(new HeapView(49, 440, 64, 49), masks.View);
        Assert.Equal(new HeapView(98, 136, 48, 200), materials.View);
        Assert.Same(terrain, table.Find(0));
        Assert.Same(terrain, table.Find(48));
        Assert.Same(masks, table.Find(49));
        Assert.Same(materials, table.Find(297));
        Assert.Null(table.Find(298));
        Assert.True(masks.Holds(97));
        Assert.False(masks.Holds(98));
        Assert.False(masks.Holds(48));
    }

    // The same view of a later draw reads the same elements: the records the shaders marked stay resolved.
    // A packed heap is one view whatever immediate a shader reads it at: the whole offset picks the descriptor. A raw
    // V# (stride 0) counts bytes, so its view has a record per descriptor. A record view at the same field one record
    // further on is the same view.
    [Fact]
    public void ReadsOfOneHeapAtSeveralImmediatesShareOneView()
    {
        var table = new BindlessHeapViewTable(1000);

        var packed = table.Acquire(Table(0x1000, 100, stride: 32), tick: 1, completedTick: 0, Unreleased, out _)!;
        Assert.Same(packed, table.Acquire(Table(0x1000, 100, stride: 32, immediate: 32), tick: 1, completedTick: 0, Unreleased, out _));
        Assert.Same(packed, table.Acquire(Table(0x1000, 100, stride: 32, immediate: 64), tick: 1, completedTick: 0, Unreleased, out _));
        Assert.Equal(new HeapView(0, 32, 0, 100), packed.View);

        var raw = table.Acquire(Table(0x8000, 3200, stride: 0, immediate: 96), tick: 1, completedTick: 0, Unreleased, out _)!;
        Assert.Same(raw, table.Acquire(Table(0x8000, 3200, stride: 0), tick: 1, completedTick: 0, Unreleased, out _));
        Assert.Equal(new HeapView(100, 32, 0, 100), raw.View);

        var records = table.Acquire(Table(0x9000, 49, immediate: 64), tick: 1, completedTick: 0, Unreleased, out _)!;
        Assert.Same(records, table.Acquire(Table(0x9000, 49, immediate: 440 + 64), tick: 1, completedTick: 0, Unreleased, out _));
        Assert.Equal(new HeapView(200, 440, 64, 49), records.View);
    }

    [Fact]
    public void AViewReadAgainKeepsItsElements()
    {
        var table = new BindlessHeapViewTable(1000);
        var first = table.Acquire(Table(0x1000, 49), tick: 1, completedTick: 0, Unreleased, out _)!;

        var again = table.Acquire(Table(0x1000, 49), tick: 7, completedTick: 5, Unreleased, out _);

        Assert.Same(first, again);
        Assert.Equal(7ul, again!.LastUsedTick);
        Assert.Single(table.Regions);
    }

    // A table the guest allocates for each frame is the same records at another address: the moved table takes over the run
    // of the old one once no pending command reads it, so its elements stay resolved (the caller compares them with the
    // records at the new address). While a command still reads the old one, the moved table gets free elements.
    [Fact]
    public void AMovedTableTakesOverTheRunOfTheOldOne()
    {
        var table = new BindlessHeapViewTable(1000);
        var old = table.Acquire(Table(0x1000, 23), tick: 1, completedTick: 0, Unreleased, out var moved)!;
        Assert.False(moved);
        old.Slots = 0b101;

        var pending = table.Acquire(Table(0x5000, 23), tick: 2, completedTick: 0, Unreleased, out moved)!;
        Assert.False(moved);
        Assert.NotSame(old, pending);
        Assert.Equal(23u, pending.View.First);
        // It is the same table: the arrays its records are read through are known before its first read.
        Assert.Equal(0b101u, pending.Slots);

        var next = table.Acquire(Table(0x9000, 23), tick: 3, completedTick: 1, Unreleased, out moved)!;
        Assert.True(moved);
        Assert.Same(old, next);
        Assert.Equal((0x9000ul, 0u, 3ul), (next.Heap.Base, next.View.First, next.LastUsedTick));
        Assert.Equal(2, table.Regions.Count);
        // The old address is a new view when a draw comes back to it.
        var back = table.Acquire(Table(0x1000, 23), tick: 3, completedTick: 1, Unreleased, out moved)!;
        Assert.False(moved);
        Assert.Equal(46u, back.View.First);
    }

    // A terrain's tile list the guest allocates for each frame has a few records more or less while the camera moves (Ghost
    // of Yōtei: 57, then 58). The frame's table is still the same records at another address: it takes over the run of the
    // old one, or the HiZ textures its culling pass reads are null for that frame and every tile is culled.
    [Fact]
    public void AMovedTableWithFewerRecordsTakesOverTheRunOfTheOldOne()
    {
        var table = new BindlessHeapViewTable(1000);
        var old = table.Acquire(Table(0x1000, 58), tick: 1, completedTick: 0, Unreleased, out _)!;

        var next = table.Acquire(Table(0x5000, 57), tick: 3, completedTick: 1, Unreleased, out var moved)!;

        Assert.True(moved);
        Assert.Same(old, next);
        Assert.Equal(new HeapView(0, 440, 0, 57), next.View);
        Assert.Single(table.Regions);
        // The record past the new count reads the null element, but the run keeps it for a longer table.
        Assert.False(next.Holds(57));
        Assert.Same(next, table.Find(57));
        Assert.Same(next, table.Acquire(Table(0x9000, 58), tick: 5, completedTick: 3, Unreleased, out moved));
        Assert.True(moved);
        Assert.Equal(new HeapView(0, 440, 0, 58), next.View);
    }

    // A table longer than every run of its shape gets a run with room to grow, so the counts of the frames that follow
    // fit in it and keep their elements; the run it outgrew stays for a shorter table.
    [Fact]
    public void AGrowingTableGetsARunTheNextCountsFitIn()
    {
        var table = new BindlessHeapViewTable(1000);
        var old = table.Acquire(Table(0x1000, 57), tick: 1, completedTick: 0, Unreleased, out _)!;
        Assert.Equal(0u, old.Slots);
        old.Slots = 0b10;

        var grown = table.Acquire(Table(0x5000, 58), tick: 3, completedTick: 1, Unreleased, out var moved)!;

        Assert.False(moved);
        Assert.NotSame(old, grown);
        Assert.Equal(new HeapView(57, 440, 0, 58), grown.View);
        Assert.Equal(96u, grown.Length);
        // The run it outgrew names the arrays its records are read through; a first view of a shape knows none.
        Assert.Equal(0b10u, grown.Slots);
        Assert.Same(grown, table.Find(57 + 95));
        Assert.Null(table.Find(57 + 96));

        var next = table.Acquire(Table(0x9000, 61), tick: 5, completedTick: 3, Unreleased, out moved)!;
        Assert.True(moved);
        Assert.Same(grown, next);
        Assert.Equal(new HeapView(57, 440, 0, 61), next.View);
        // The next view after it starts past the elements the run keeps.
        var other = table.Acquire(Table(0x1000, 10, immediate: 64), tick: 5, completedTick: 3, Unreleased, out _)!;
        Assert.Equal(57u + 96u, other.View.First);
    }

    // A view says whether a command not yet completed read its run before this use: such a run's elements must not change.
    [Fact]
    public void AViewKnowsWhetherAPendingCommandReadsItsRun()
    {
        var table = new BindlessHeapViewTable(1000);
        var view = table.Acquire(Table(0x1000, 58), tick: 1, completedTick: 0, Unreleased, out _)!;
        Assert.False(view.ReadByPending);

        Assert.True(table.Acquire(Table(0x1000, 58), tick: 2, completedTick: 0, Unreleased, out _)!.ReadByPending);
        Assert.False(table.Acquire(Table(0x1000, 58), tick: 4, completedTick: 2, Unreleased, out _)!.ReadByPending);
        // A second read in the same tick: the first one is in the commands of this tick, not completed.
        Assert.True(table.Acquire(Table(0x1000, 58), tick: 4, completedTick: 2, Unreleased, out _)!.ReadByPending);
    }

    // The guest rewrote a table at the same address while commands still read its run: the view gets another run, and
    // the old one keeps its elements for those commands. Once idle, the old run is taken over by the next fork.
    [Fact]
    public void ARewrittenViewReadByPendingCommandsForksToAnotherRun()
    {
        var table = new BindlessHeapViewTable(1000);
        var old = table.Acquire(Table(0x1000, 58), tick: 1, completedTick: 0, Unreleased, out _)!;
        old.Slots = 0b1;
        var read = table.Acquire(Table(0x1000, 58), tick: 2, completedTick: 0, Unreleased, out _)!;
        Assert.True(read.ReadByPending);

        var fork = table.Fork(read, tick: 2, completedTick: 0, Unreleased, out var moved)!;

        Assert.False(moved);
        Assert.NotSame(old, fork);
        Assert.Equal(new HeapView(58, 440, 0, 58), fork.View);
        Assert.Equal(0b1u, fork.Slots);
        Assert.Same(fork, table.Acquire(Table(0x1000, 58), tick: 2, completedTick: 0, Unreleased, out _));
        Assert.Same(old, table.Find(0));
        Assert.Equal(2, table.Regions.Count);

        // The old run is idle now: the next fork takes it over instead of new elements.
        var again = table.Acquire(Table(0x1000, 58), tick: 3, completedTick: 2, Unreleased, out _)!;
        Assert.Same(fork, again);
        Assert.True(table.Fork(again, tick: 3, completedTick: 2, Unreleased, out moved) is { } back && ReferenceEquals(back, old));
        Assert.True(moved);
        Assert.Equal(2, table.Regions.Count);
    }

    // With no other run to be had the view keeps its run, which the caller updates in place.
    [Fact]
    public void AForkWithoutRoomKeepsTheRun()
    {
        var table = new BindlessHeapViewTable(60);
        var view = table.Acquire(Table(0x1000, 58), tick: 1, completedTick: 0, Unreleased, out _)!;

        Assert.Null(table.Fork(view, tick: 1, completedTick: 0, Unreleased, out _));
        Assert.Same(view, table.Acquire(Table(0x1000, 58), tick: 1, completedTick: 0, Unreleased, out _));
        Assert.Single(table.Regions);
    }

    // A view of 16-byte T#s (R128) and a view of 32-byte T#s over records of one shape resolve different descriptors:
    // neither takes over the other's run.
    [Fact]
    public void AnR128ViewDoesNotTakeOverTheRunOfAViewOfWholeDescriptors()
    {
        var table = new BindlessHeapViewTable(1000);
        var whole = table.Acquire(Table(0x1000, 23), tick: 1, completedTick: 0, Unreleased, out _)!;

        var r128 = table.Acquire(Table(0x5000, 23) with { R128 = true }, tick: 3, completedTick: 1, Unreleased, out var moved)!;
        Assert.False(moved);
        Assert.NotSame(whole, r128);
        Assert.Equal(23u, r128.View.First);

        var next = table.Acquire(Table(0x9000, 23) with { R128 = true }, tick: 5, completedTick: 3, Unreleased, out moved)!;
        Assert.True(moved);
        Assert.Same(r128, next);
    }

    // The records of a run are read again in guest memory where its heap holds their descriptors, each of the size the
    // heap's reads take. Material records can hold 16-byte T#s several hundred bytes in: read as 32 bytes,
    // the next T# followed each one, and the record lost its view the first time it was read again.
    [Fact]
    public void ARunsDescriptorsAreTheSizeItsHeapReads()
    {
        var table = new BindlessHeapViewTable(1000);
        var r128 = table.Acquire(Table(0x1000, 257, 1016, 468) with { R128 = true }, tick: 1, completedTick: 0, Unreleased, out _)!;
        var whole = table.Acquire(Table(0x1000, 257, 1016, 468), tick: 1, completedTick: 0, Unreleased, out _)!;

        Assert.Equal(new BindlessHeapDescriptor(false, 0x1000 + 468, 257, 1016, 0, R128: true), r128.Descriptors);
        Assert.Equal(BindlessHeap.R128RecordBytes, r128.Descriptors.DescriptorBytes);
        Assert.Equal(new BindlessHeapDescriptor(false, 0x1000 + 468, 257, 1016), whole.Descriptors);
        Assert.Equal(BindlessHeap.ImageRecordBytes, whole.Descriptors.DescriptorBytes);
    }

    // With no free elements left, a view takes those of the view used longest ago, but only one whose commands have all
    // completed: a pending command still reads its elements.
    [Fact]
    public void AFullTableGivesANewViewTheElementsOfTheOldestCompletedOne()
    {
        var table = new BindlessHeapViewTable(100);
        var oldest = table.Acquire(Table(0x1000, 40), tick: 1, completedTick: 0, Unreleased, out _)!;
        var newer = table.Acquire(Table(0x2000, 40, immediate: 64), tick: 2, completedTick: 0, Unreleased, out _)!;
        var released = new List<BindlessHeapViewTable.Region>();

        // Nothing has completed: the view has no elements, and nothing is taken.
        Assert.Null(table.Acquire(Table(0x3000, 40, immediate: 128), tick: 3, completedTick: 0, released.Add, out _));
        Assert.Empty(released);

        var moved = table.Acquire(Table(0x3000, 40, immediate: 128), tick: 3, completedTick: 1, released.Add, out _)!;

        Assert.Equal([oldest], released);
        Assert.Equal(new HeapView(0, 440, 128, 40), moved.View);
        Assert.Same(moved, table.Find(0));
        Assert.Same(newer, table.Find(40));
        Assert.Equal(2, table.Regions.Count);
        // The view that gave its elements up is a new view when it is read again.
        var back = table.Acquire(Table(0x1000, 20), tick: 4, completedTick: 1, Unreleased, out _)!;
        Assert.Equal(80u, back.View.First);
    }

    // A view larger than the gap between two others takes the elements of as many completed views as it needs.
    [Fact]
    public void ALargeViewTakesSeveralCompletedViews()
    {
        var table = new BindlessHeapViewTable(100);
        var a = table.Acquire(Table(0x1000, 30), tick: 1, completedTick: 0, Unreleased, out _)!;
        var b = table.Acquire(Table(0x2000, 30), tick: 2, completedTick: 0, Unreleased, out _)!;
        var c = table.Acquire(Table(0x3000, 30), tick: 3, completedTick: 0, Unreleased, out _)!;
        var released = new List<BindlessHeapViewTable.Region>();

        var large = table.Acquire(Table(0x4000, 60), tick: 4, completedTick: 3, released.Add, out _)!;

        Assert.Equal([a, b], released);
        Assert.Equal(0u, large.View.First);
        Assert.Same(c, table.Find(60));
        Assert.Null(table.Find(95));
    }

    // A title's texture heap of packed descriptors can be larger than half of set 1, and the guest keeps two copies of it
    // (46158 records each in a real title). The second copy finds no free run and shares the first one's, as a heap that
    // moved: the caller compares the run's elements with the new copy's records. Tables keep their own runs.
    [Fact]
    public void AViewWithoutFreeElementsSharesTheRunOfAViewOfItsShape()
    {
        var table = new BindlessHeapViewTable(100);
        var terrain = table.Acquire(Table(0x1000, 10), tick: 1, completedTick: 0, Unreleased, out _)!;
        var heap = table.Acquire(new BindlessHeapDescriptor(false, 0x10000, 80, 32), tick: 1, completedTick: 0, Unreleased, out var moved)!;
        Assert.False(moved);

        var copy = table.Acquire(new BindlessHeapDescriptor(false, 0x20000, 80, 32), tick: 1, completedTick: 0, Unreleased, out moved);

        Assert.Same(heap, copy);
        Assert.True(moved);
        Assert.Equal(0x20000ul, heap.Heap.Base);
        Assert.Equal(new HeapView(10, 32, 0, 80), heap.View);
        // Back to the first copy: the run moves again.
        Assert.Same(heap, table.Acquire(new BindlessHeapDescriptor(false, 0x10000, 80, 32), tick: 2, completedTick: 0, Unreleased, out moved));
        Assert.True(moved);
        // The copy it holds now is found as it is.
        Assert.Same(heap, table.Acquire(new BindlessHeapDescriptor(false, 0x10000, 80, 32), tick: 3, completedTick: 0, Unreleased, out moved));
        Assert.False(moved);
        // Another shape does not share: with nothing completed it has no elements.
        Assert.Null(table.Acquire(new BindlessHeapDescriptor(false, 0x30000, 80, 32, 0) with { Records = 81 }, tick: 3, completedTick: 0, Unreleased, out _));
        Assert.Same(terrain, table.Find(0));
    }

    // A texture heap the guest streams more textures into names more records from frame to frame, and a draw prepared
    // earlier still names fewer. It stays one view: a draw that names more records than the run holds gives the view a
    // larger run with room to grow (the caller moves the resolved elements over), a draw that names fewer reads what the
    // view holds, and the shader reads up to the most records a draw named, never into the spare room.
    [Fact]
    public void AGrowingPackedHeapStaysOneViewAndReadsUpToTheMostRecordsADrawNamed()
    {
        var table = new BindlessHeapViewTable(1000);
        var heap = new BindlessHeapDescriptor(false, 0x10000, 100, 32);
        var view = table.Acquire(heap, tick: 1, completedTick: 0, Unreleased, out _, out var grownFrom)!;
        Assert.Null(grownFrom);
        Assert.Equal(new HeapView(0, 32, 0, 100), view.View);

        var grown = table.Acquire(heap with { Records = 160 }, tick: 2, completedTick: 0, Unreleased, out var moved, out grownFrom)!;
        Assert.False(moved);
        Assert.Same(view, grownFrom);
        Assert.Equal(new HeapView(100, 32, 0, 160), grown.View);
        Assert.Equal(180u, grown.Length);
        // The outgrown run stays for the commands that read it.
        Assert.Equal(2, table.Regions.Count);

        Assert.Same(grown, table.Acquire(heap with { Records = 170 }, tick: 3, completedTick: 0, Unreleased, out _, out grownFrom));
        Assert.Null(grownFrom);
        Assert.Equal(new HeapView(100, 32, 0, 170), grown.View);
        Assert.Same(grown, table.Acquire(heap, tick: 4, completedTick: 0, Unreleased, out _, out _));
        Assert.Equal(170u, grown.View.Records);

        // Released once idle, the outgrown run leaves the heap's current run in place.
        var released = new List<BindlessHeapViewTable.Region>();
        table.ReleaseIdle(idleBefore: 3, completedTick: 4, released.Add);
        Assert.Equal([view], released);
        Assert.Same(grown, table.Acquire(heap, tick: 5, completedTick: 4, Unreleased, out _, out _));
    }

    [Fact]
    public void AViewHasNoMoreElementsThanTheTable()
    {
        var table = new BindlessHeapViewTable(100);

        Assert.Null(table.Acquire(Table(0x1000, 0), tick: 1, completedTick: 0, Unreleased, out _));
        Assert.Null(table.Acquire(Table(0, 16), tick: 1, completedTick: 0, Unreleased, out _));
        var cut = table.Acquire(Table(0x1000, 5000), tick: 1, completedTick: 0, Unreleased, out _)!;

        Assert.Equal(new HeapView(0, 440, 0, 100), cut.View);
    }

    [Fact]
    public void IdleViewsGiveTheirElementsUpOnceTheirCommandsCompleted()
    {
        var table = new BindlessHeapViewTable(100);
        var idle = table.Acquire(Table(0x1000, 10), tick: 1, completedTick: 0, Unreleased, out _)!;
        var pending = table.Acquire(Table(0x2000, 10), tick: 5, completedTick: 0, Unreleased, out _)!;
        var used = table.Acquire(Table(0x3000, 10), tick: 9, completedTick: 0, Unreleased, out _)!;
        var released = new List<BindlessHeapViewTable.Region>();

        table.ReleaseIdle(idleBefore: 8, completedTick: 4, released.Add);

        Assert.Equal([idle], released);
        Assert.Equal([pending, used], table.Regions);
    }

    [Fact]
    public void AViewReadEachFrameSurvivesMoreThanTheIdleTickLimitBetweenReads()
    {
        var table = new BindlessHeapViewTable(1024);
        var heap = new BindlessHeapDescriptor(false, 0x10000, 256, 32);
        var view = table.Acquire(heap, tick: 100, completedTick: 90, Unreleased, out _, frame: 20)!;
        var released = new List<BindlessHeapViewTable.Region>();

        table.ReleaseIdle(idleBefore: 113, completedTick: 360, released.Add, idleBeforeFrame: 19);
        Assert.False(released.Count != 0, "A view used in the previous frame was released.");
        Assert.Same(view, table.Acquire(heap, tick: 369, completedTick: 360, Unreleased, out _, frame: 21));

        table.ReleaseIdle(idleBefore: 449, completedTick: 700, released.Add, idleBeforeFrame: 22);
        Assert.Equal([view], released);
    }
}
