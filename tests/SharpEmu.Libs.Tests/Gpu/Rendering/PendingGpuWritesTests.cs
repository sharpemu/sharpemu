// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class PendingGpuWritesTests
{
    [Fact]
    public void AMarkedRangeIsPendingForOverlappingReadsOnly()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1_0000_1000, 0x100, sequence: 5);

        Assert.True(pending.TryGetPending(0x1_0000_1080, 4, out var sequence));
        Assert.Equal(5, sequence);
        Assert.True(pending.TryGetPending(0x1_0000_0FF0, 0x20, out _));
        Assert.True(pending.TryGetPending(0x1_0000_10FC, 8, out _));
        Assert.False(pending.TryGetPending(0x1_0000_1100, 4, out _));
        Assert.False(pending.TryGetPending(0x1_0000_0FF0, 0x10, out _));
        Assert.False(pending.TryGetPending(0x2_0000_1080, 4, out _));
    }

    [Fact]
    public void ReleasingASequenceDropsEveryMarkUpToIt()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1000, 0x10, sequence: 1);
        pending.Mark(0x2000, 0x10, sequence: 2);
        pending.Mark(0x1000, 0x10, sequence: 3);

        Assert.True(pending.TryGetPending(0x1000, 4, out var sequence));
        Assert.Equal(3, sequence);
        pending.Release(2);
        Assert.True(pending.TryGetPending(0x1000, 4, out sequence));
        Assert.Equal(3, sequence);
        Assert.False(pending.TryGetPending(0x2000, 4, out _));
        Assert.Equal(2, pending.Released);
        pending.Release(3);
        Assert.False(pending.TryGetPending(0x1000, 4, out _));
        Assert.Equal(3, pending.Released);
    }

    [Fact]
    public void AMarkOfEverythingMakesEveryReadPendingUntilItIsReleased()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1000, 0x10, sequence: 1);
        pending.MarkEverything(sequence: 2);

        Assert.True(pending.TryGetPending(0x9_0000_0000, 4, out var sequence));
        Assert.Equal(2, sequence);
        pending.Release(2);
        Assert.False(pending.TryGetPending(0x9_0000_0000, 4, out _));
        Assert.False(pending.TryGetPending(0x1000, 4, out _));
    }

    [Fact]
    public void ARangeAcrossManyBlocksIsPendingInEachOfThem()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x4000_0000, 0x40_0000, sequence: 7);

        Assert.True(pending.TryGetPending(0x4000_0000, 4, out _));
        Assert.True(pending.TryGetPending(0x4020_1234, 4, out _));
        Assert.True(pending.TryGetPending(0x403F_FFFC, 4, out _));
        Assert.False(pending.TryGetPending(0x4040_0000, 4, out _));
        pending.Release(7);
        Assert.False(pending.TryGetPending(0x4020_1234, 4, out _));
    }

    [Fact]
    public void ReadsSeeMarksMadeByAnotherThread()
    {
        var pending = new PendingGpuWrites();
        var stop = false;
        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                _ = pending.TryGetPending(0x5000, 4, out _);
            }
        });
        reader.Start();
        for (var sequence = 1; sequence <= 2000; sequence++)
        {
            pending.Mark(0x5000, 0x10, sequence);
            pending.Mark(0x7000, 0x10, sequence);
            pending.Release(sequence - 1);
        }

        Volatile.Write(ref stop, true);
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
        Assert.True(pending.TryGetPending(0x5000, 4, out var last));
        Assert.Equal(2000, last);
        pending.Release(2000);
        Assert.False(pending.TryGetPending(0x5000, 4, out _));
        Assert.False(pending.TryGetPending(0x7000, 4, out _));
    }

    [Fact]
    public void SmallAndLargeWritesAreFoundByNarrowAndWideReads()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1_0004_2000, 0x40, sequence: 7);
        pending.Mark(0x1_0100_0000, 0x200_0000, sequence: 9);

        Assert.True(pending.TryGetPending(0x1_0004_2020, 4, out var sequence));
        Assert.Equal(7, sequence);
        Assert.False(pending.TryGetPending(0x1_0004_6000, 4, out _));
        Assert.True(pending.TryGetPending(0x1_0000_0000, 0x10_0000, out sequence));
        Assert.Equal(7, sequence);
        Assert.True(pending.TryGetPending(0x1_0180_0000, 4, out sequence));
        Assert.Equal(9, sequence);
        Assert.True(pending.TryGetPending(0x1_0000_0000, 0x400_0000, out sequence));
        Assert.Equal(9, sequence);
        Assert.False(pending.TryGetPending(0x1_0010_0000, 0x10_0000, out _));
        pending.Release(8);
        Assert.False(pending.TryGetPending(0x1_0004_2020, 4, out _));
        Assert.False(pending.TryGetPending(0x1_0000_0000, 0x10_0000, out _));
        Assert.True(pending.TryGetPending(0x1_0180_0000, 4, out _));
    }

    [Fact]
    public void ARemarkedWriteIsPendingAgainAfterItsOldSequenceWasReleased()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x3000, 0x10, sequence: 1);
        pending.Release(1);
        Assert.False(pending.TryGetPending(0x3000, 4, out _));
        pending.Mark(0x3000, 0x10, sequence: 4);
        Assert.True(pending.TryGetPending(0x3000, 4, out var sequence));
        Assert.Equal(4, sequence);
    }

    [Fact]
    public void RemarkingAWriteTheSweepRemovedMakesItPendingAgain()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x9000, 0x10, sequence: 1);
        pending.Release(1);
        for (var index = 0; index < 600; index++)
        {
            pending.Mark(0x10_0000 + (ulong)index * 0x100, 0x10, sequence: 2 + index);
        }

        Assert.False(pending.TryGetPending(0x9000, 4, out _));
        pending.Mark(0x9000, 0x10, sequence: 1000);
        Assert.True(pending.TryGetPending(0x9000, 4, out var sequence));
        Assert.Equal(1000, sequence);
        pending.Mark(0x9000, 0x10, sequence: 1001);
        Assert.True(pending.TryGetPending(0x9000, 4, out sequence));
        Assert.Equal(1001, sequence);
    }

    [Fact]
    public void ARemarkAfterAReaderKeepsTheEarlierUnreleasedWritePendingForIt()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1000, 0x10, sequence: 10);
        pending.Mark(0x1000, 0x10, sequence: 30);

        Assert.True(pending.HasPendingBetween(0x1000, 4, pending.Released, 20));
        Assert.True(pending.TryGetPending(0x1000, 4, out var newest));
        Assert.Equal(30, newest);
        pending.Release(30);
        Assert.False(pending.HasPendingBetween(0x1000, 4, pending.Released, 40));
    }

    [Fact]
    public void AnEarlierMarkArrivingAfterALaterOneStillCountsForReadersBetweenThem()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1000, 0x10, sequence: 30);
        pending.Mark(0x1000, 0x10, sequence: 10);

        Assert.True(pending.HasPendingBetween(0x1000, 4, pending.Released, 20));
        Assert.True(pending.TryGetPending(0x1000, 4, out var newest));
        Assert.Equal(30, newest);
    }

    [Fact]
    public void ARemarkAfterEverythingWasReleasedStartsAFreshSpan()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1000, 0x10, sequence: 10);
        pending.Release(15);
        pending.Mark(0x1000, 0x10, sequence: 30);

        Assert.False(pending.HasPendingBetween(0x1000, 4, pending.Released, 20));
        Assert.True(pending.HasPendingBetween(0x1000, 4, pending.Released, 31));
    }

    [Fact]
    public void EveryMarkAdvancesTheGeneration()
    {
        var pending = new PendingGpuWrites();
        var generation = pending.Generation;
        pending.Mark(0x1000, 0x10, sequence: 1);
        Assert.True(pending.Generation > generation);
        generation = pending.Generation;
        pending.Mark(0x1000, 0x10, sequence: 2);
        Assert.True(pending.Generation > generation);
        generation = pending.Generation;
        pending.Mark(0x1000, 0, sequence: 3);
        Assert.Equal(generation, pending.Generation);
    }

    [Theory]
    [InlineData(0x10ul)]
    [InlineData(0x10_0000ul)]
    [InlineData(0x80_0000ul)]
    public void ReleasedAndFutureWritesDoNotMakeAnEmptySequenceGapPending(ulong size)
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1000, size, sequence: 10);
        pending.Mark(0x1000, size, sequence: 30);
        pending.Release(15);

        Assert.False(pending.HasPendingBetween(0x1000, 4, 15, 20));
        Assert.False(pending.HasPendingBetween(0x1000, size, 15, 30));
        Assert.True(pending.HasPendingBetween(0x1000, size, 15, 31));
        pending.Mark(0x1000, size, sequence: 18);
        Assert.True(pending.HasPendingBetween(0x1000, 4, 15, 20));
        Assert.False(pending.HasPendingBetween(0x1000, size, 18, 30));
    }

    [Fact]
    public void SequencePruningPreservesUnreleasedWritersAndConservativeHistoricalQueries()
    {
        var pending = new PendingGpuWrites();
        pending.Mark(0x1000, 16, sequence: 10);
        pending.Mark(0x1000, 16, sequence: 20);
        pending.Mark(0x1000, 16, sequence: 30);
        pending.Release(15);
        pending.Mark(0x1000, 16, sequence: 40);

        Assert.True(pending.HasPendingBetween(0x1000, 4, 15, 21));
        Assert.True(pending.HasPendingBetween(0x1000, 4, 0, 15));
        Assert.False(pending.HasPendingBetween(0x1000, 4, 20, 30));
        pending.Release(35);
        pending.Mark(0x1000, 16, sequence: 50);
        Assert.False(pending.HasPendingBetween(0x1000, 4, 35, 40));
        Assert.True(pending.HasPendingBetween(0x1000, 4, 35, 41));
    }

    [Fact]
    public void ConcurrentOutOfOrderMarksPreserveEachActualSequence()
    {
        var pending = new PendingGpuWrites();
        Parallel.For(1, 128, index => pending.Mark(0x1000, 16, sequence: index * 2));
        pending.Release(101);
        pending.Mark(0x1000, 16, sequence: 256);

        for (var after = 101; after < 255; after++)
        {
            var expected = (after + 1) % 2 == 0;
            Assert.Equal(expected, pending.HasPendingBetween(0x1000, 4, after, after + 2));
        }
    }
}
