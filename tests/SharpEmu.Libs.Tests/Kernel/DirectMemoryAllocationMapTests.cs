// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class DirectMemoryAllocationMapTests
{
    [Fact]
    public void AllocationUsesFirstAlignedRangeWithinSearchBounds()
    {
        var allocations = new DirectMemoryAllocationMap(128);
        Allocate(allocations, 24, 16, 7);
        Assert.True(allocations.TryAllocate(3, 100, 16, 8, 2, out var first));
        Assert.Equal(8UL, first);
        Assert.True(allocations.TryAllocate(3, 100, 16, 8, 2, out var second));
        Assert.Equal(40UL, second);
        Assert.False(allocations.TryAllocate(56, 64, 9, 1, 0, out _));
        Assert.True(allocations.TryAllocate(127, ulong.MaxValue, 1, 1, 0, out var last));
        Assert.Equal(127UL, last);
        Assert.Equal(79UL, allocations.AvailableBytes);
    }

    [Fact]
    public void AvailableQuerySelectsLargestAlignedSpanAndKeepsFirstTie()
    {
        var allocations = new DirectMemoryAllocationMap(64);
        Allocate(allocations, 16, 8);
        Allocate(allocations, 40, 8);
        Assert.True(allocations.TryFindAvailableRange(0, 64, 8, out var start, out var length));
        Assert.Equal((0UL, 16UL), (start, length));
        Assert.True(allocations.TryFindAvailableRange(1, 43, 8, out start, out length));
        Assert.Equal((24UL, 16UL), (start, length));
        Assert.True(allocations.TryFindAvailableRange(1, 16, 6, out start, out length));
        Assert.Equal((6UL, 10UL), (start, length));
    }

    [Fact]
    public void CoverageAcceptsAdjacentAllocationsButRejectsHolesAndOverflow()
    {
        var allocations = new DirectMemoryAllocationMap(128);
        Allocate(allocations, 16, 16);
        Allocate(allocations, 32, 16);
        Allocate(allocations, 64, 16);
        Assert.True(allocations.ContainsAllocatedRange(20, 28));
        Assert.False(allocations.ContainsAllocatedRange(20, 45));
        Assert.False(allocations.ContainsAllocatedRange(0, 32));
        Assert.False(allocations.ContainsAllocatedRange(48, 1));
        Assert.False(allocations.ContainsAllocatedRange(20, 0));
        Assert.False(allocations.ContainsAllocatedRange(ulong.MaxValue - 3, 8));
    }

    [Fact]
    public void PartialReleasePreservesBothEndsAndMemoryTypesAcrossAllocations()
    {
        var allocations = new DirectMemoryAllocationMap(128);
        Allocate(allocations, 16, 32, 3);
        Allocate(allocations, 48, 32, 7);
        allocations.ReleaseRange(32, 32);
        Assert.True(allocations.TryFindAllocation(16, false, out var left));
        Assert.Equal(new DirectMemoryAllocationMap.Allocation(16, 16, 3), left);
        Assert.True(allocations.TryFindAllocation(64, false, out var right));
        Assert.Equal(new DirectMemoryAllocationMap.Allocation(64, 16, 7), right);
        Assert.False(allocations.ContainsAllocatedRange(31, 34));
        Allocate(allocations, 32, 32, 9);
        Assert.True(allocations.ContainsAllocatedRange(16, 64));
        allocations.SetMemoryType(32, 5);
        Assert.True(allocations.TryFindAllocation(47, false, out var changed));
        Assert.Equal(5, changed.MemoryType);
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void ReleaseMergesFreeNeighborsInEveryOrder(int first, int second, int third)
    {
        var allocations = new DirectMemoryAllocationMap(64);
        Allocate(allocations, 8, 16);
        Allocate(allocations, 24, 16);
        Allocate(allocations, 40, 16);
        foreach (var index in new[] { first, second, third })
            allocations.ReleaseRange(8 + (ulong)index * 16, 16);
        Assert.Equal(64UL, allocations.AvailableBytes);
        Allocate(allocations, 0, 64);
        Assert.Equal(0UL, allocations.AvailableBytes);
    }

    [Fact]
    public void FailedReleaseDoesNotChangeAllocationsOrFreeRanges()
    {
        var allocations = new DirectMemoryAllocationMap(64);
        Allocate(allocations, 8, 8);
        Allocate(allocations, 24, 8);
        Assert.Throws<InvalidOperationException>(() => allocations.ReleaseRange(8, 24));
        Assert.Equal(48UL, allocations.AvailableBytes);
        Assert.True(allocations.ContainsAllocatedRange(8, 8));
        Assert.True(allocations.ContainsAllocatedRange(24, 8));
        Allocate(allocations, 16, 8);
        allocations.ReleaseRange(8, 24);
        Assert.Throws<InvalidOperationException>(() => allocations.ReleaseRange(8, 24));
        Assert.Equal(64UL, allocations.AvailableBytes);
    }

    [Fact]
    public void QueryDistinguishesContainingAndNextAllocations()
    {
        var allocations = new DirectMemoryAllocationMap(128);
        Allocate(allocations, 16, 16, 1);
        Allocate(allocations, 64, 16, 2);
        Assert.False(allocations.TryFindAllocation(0, false, out _));
        Assert.True(allocations.TryFindAllocation(0, true, out var first));
        Assert.Equal(16UL, first.Start);
        Assert.True(allocations.TryFindAllocation(31, true, out first));
        Assert.Equal(16UL, first.Start);
        Assert.False(allocations.TryFindAllocation(32, false, out _));
        Assert.True(allocations.TryFindAllocation(32, true, out var next));
        Assert.Equal(64UL, next.Start);
        Assert.False(allocations.TryFindAllocation(80, true, out _));
        Assert.False(allocations.TryFindAllocation(ulong.MaxValue, true, out _));
    }

    [Fact]
    public void AlignmentAndRangeLimitsCannotWrap()
    {
        var allocations = new DirectMemoryAllocationMap(ulong.MaxValue);
        Assert.False(allocations.TryAllocate(ulong.MaxValue - 3, ulong.MaxValue, 1, 8, 0, out _));
        Assert.False(allocations.TryFindAvailableRange(ulong.MaxValue - 3, ulong.MaxValue, 8, out _, out _));
        Assert.True(allocations.TryAllocate(ulong.MaxValue - 3, ulong.MaxValue, 3, 1, 0, out var address));
        Assert.True(allocations.ContainsAllocatedRange(address, 3));
        Assert.False(allocations.ContainsAllocatedRange(address, 4));
        allocations.ReleaseRange(address, 3);
        Assert.Equal(ulong.MaxValue, allocations.AvailableBytes);
        Assert.False(allocations.TryAllocate(0, 128, 1, 0, 0, out _));
        Assert.False(allocations.TryAllocate(0, 128, 0, 1, 0, out _));
        Assert.False(allocations.TryAllocate(128, 128, 1, 1, 0, out _));
        Assert.False(allocations.TryFindAvailableRange(0, 128, 0, out _, out _));
    }

    [Fact]
    public void ResetRestoresAllCapacityIncludingUnmappedAllocations()
    {
        var allocations = new DirectMemoryAllocationMap(64);
        Allocate(allocations, 0, 64);
        allocations.ReleaseRange(16, 8);
        allocations.Reset();
        Assert.False(allocations.TryFindAllocation(0, true, out _));
        Assert.Equal(64UL, allocations.AvailableBytes);
        Allocate(allocations, 0, 64);
        var empty = new DirectMemoryAllocationMap(0);
        Assert.False(empty.TryAllocate(0, 64, 1, 1, 0, out _));
        Assert.False(empty.TryFindAvailableRange(0, 64, 1, out _, out _));
    }

    [Fact]
    public void RepeatedQueriesDoNotAllocateManagedStorage()
    {
        var allocations = new DirectMemoryAllocationMap(10000);
        for (ulong address = 0; address < 10000; address += 2)
            Allocate(allocations, address, 1);
        // Tiered JIT recompilation can still allocate on this thread during the first measured
        // passes when the machine is busy; storage allocated by the queries would show in every pass.
        const int passes = 5;
        var steadyStateAllocation = long.MaxValue;
        for (var pass = 0; pass < passes; pass++)
        {
            var matches = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (ulong address = 0; address < 10000; address += 2)
            {
                if (allocations.ContainsAllocatedRange(address, 1)) matches++;
                if (allocations.TryFindAllocation(address, false, out _)) matches++;
                if (allocations.TryFindAvailableRange(address, address + 2, 1, out _, out _)) matches++;
            }
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(15000, matches);
            if (pass != 0)
                steadyStateAllocation = Math.Min(steadyStateAllocation, allocatedBytes);
        }

        Assert.Equal(0L, steadyStateAllocation);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(83)]
    [InlineData(491)]
    public void RandomAllocationAndReleaseMatchByteMap(int seed)
    {
        const int capacity = 128;
        var allocations = new DirectMemoryAllocationMap(capacity);
        var occupied = new bool[capacity];
        var random = new System.Random(seed);
        for (var operation = 0; operation < 1500; operation++)
        {
            var start = random.Next(capacity);
            var length = random.Next(1, Math.Min(24, capacity - start) + 1);
            if (random.Next(2) == 0)
            {
                var end = random.Next(start + 1, capacity + 1);
                var alignment = random.Next(1, 9);
                var expected = FindFirstFit(occupied, start, end, length, alignment);
                Assert.Equal(expected >= 0, allocations.TryAllocate((ulong)start, (ulong)end,
                    (ulong)length, (ulong)alignment, 0, out var actual));
                if (expected >= 0)
                {
                    Assert.Equal((ulong)expected, actual);
                    Array.Fill(occupied, true, expected, length);
                }
            }
            else
            {
                var covered = occupied.Skip(start).Take(length).All(value => value);
                Assert.Equal(covered, allocations.ContainsAllocatedRange((ulong)start, (ulong)length));
                if (covered)
                {
                    allocations.ReleaseRange((ulong)start, (ulong)length);
                    Array.Fill(occupied, false, start, length);
                }
                else
                    Assert.Throws<InvalidOperationException>(() => allocations.ReleaseRange((ulong)start, (ulong)length));
            }
            Assert.Equal((ulong)occupied.Count(value => !value), allocations.AvailableBytes);
            for (var address = 0; address < capacity; address++)
                Assert.Equal(occupied[address], allocations.ContainsAllocatedRange((ulong)address, 1));

            var searchEnd = random.Next(start + 1, capacity + 1);
            var searchAlignment = random.Next(1, 9);
            var expectedSpan = FindLargestSpan(occupied, start, searchEnd, searchAlignment);
            Assert.Equal(expectedSpan.Length != 0, allocations.TryFindAvailableRange((ulong)start,
                (ulong)searchEnd, (ulong)searchAlignment, out var availableStart, out var availableLength));
            Assert.Equal(expectedSpan, (availableStart, availableLength));
        }
    }

    private static int FindFirstFit(bool[] occupied, int start, int end, int length, int alignment)
    {
        for (var address = start; address <= end - length; address++)
            if (address % alignment == 0 && !occupied.Skip(address).Take(length).Any(value => value))
                return address;
        return -1;
    }

    private static (ulong Start, ulong Length) FindLargestSpan(bool[] occupied, int start, int end, int alignment)
    {
        (ulong Start, ulong Length) best = (0, 0);
        for (var address = start; address < end; address++)
        {
            if (address % alignment != 0) continue;
            var stop = address;
            while (stop < end && !occupied[stop]) stop++;
            if ((ulong)(stop - address) > best.Length)
                best = ((ulong)address, (ulong)(stop - address));
        }
        return best;
    }

    private static void Allocate(DirectMemoryAllocationMap allocations, ulong address, ulong length, int memoryType = 0)
    {
        Assert.True(allocations.TryAllocate(address, address + length, length, 1, memoryType, out var actual));
        Assert.Equal(address, actual);
    }
}
