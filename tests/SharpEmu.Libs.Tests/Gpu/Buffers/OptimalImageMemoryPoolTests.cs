// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class OptimalImageMemoryPoolTests
{
    private sealed class LimitedDevice(ulong budget) : IDeviceMemoryAllocator
    {
        public readonly List<ulong> Requests = [];
        public readonly Dictionary<ulong, ulong> Allocations = [];
        private ulong _next;

        public Result AllocateMemory(in MemoryAllocateInfo info, out DeviceMemory memory)
        {
            Requests.Add(info.AllocationSize);
            memory = default;
            if (info.AllocationSize > budget - Allocations.Values.Aggregate(0UL, (sum, bytes) => sum + bytes))
            {
                return Result.ErrorOutOfDeviceMemory;
            }

            memory = new DeviceMemory(++_next);
            Allocations.Add(memory.Handle, info.AllocationSize);
            return Result.Success;
        }

        public void FreeMemory(DeviceMemory memory) => Allocations.Remove(memory.Handle);
    }

    private static MemoryRequirements Requirements(ulong size, ulong alignment = 256) =>
        new() { Size = size, Alignment = alignment, MemoryTypeBits = 1 };

    [Fact]
    public void LargeBlockRejected_SmallImageStillFitsAndIsReleased()
    {
        var device = new LimitedDevice(32UL << 20);
        var pool = new OptimalImageMemoryPool(device);
        var requirements = Requirements(22UL << 20);
        Assert.Equal(Result.Success, pool.Allocate(requirements, 0, out var image));
        Assert.Equal(requirements.Size, pool.AllocatedBytes);
        Assert.Equal(requirements.Size, pool.PlacedBytes);
        Assert.Equal(new[] { OptimalImageMemoryPool.BlockSize, requirements.Size }, device.Requests);
        pool.Free(image);
        pool.ReleaseRetained();
        Assert.Empty(device.Allocations);
        Assert.Equal(0UL, pool.AllocatedBytes);
        Assert.Equal(0UL, pool.PlacedBytes);
    }

    [Fact]
    public void ActualImageCannotFit_FailureLeavesPoolAccountingUnchanged()
    {
        var device = new LimitedDevice(1UL << 20);
        var pool = new OptimalImageMemoryPool(device);
        Assert.Equal(Result.ErrorOutOfDeviceMemory, pool.Allocate(Requirements(22UL << 20), 0, out _));
        Assert.Empty(device.Allocations);
        Assert.Equal(0UL, pool.AllocatedBytes);
        Assert.Equal(0UL, pool.PlacedBytes);
        Assert.Equal(0, pool.Placements);
    }

    [Theory]
    [InlineData(256UL)]
    [InlineData(4096UL)]
    [InlineData(65536UL)]
    public void Placements_HonourAlignmentAndNeverOverlap(ulong alignment)
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device, blockSize: 4UL << 20);
        var placed = new List<OptimalImageMemoryAllocation>();
        foreach (var size in new ulong[] { 100, 5000, 70000, 1, 65537, 300 })
        {
            Assert.Equal(Result.Success, pool.Allocate(Requirements(size, alignment), 0, out var memory));
            Assert.Equal(0UL, memory.Offset % alignment);
            Assert.Equal(size, memory.Size);
            placed.Add(memory);
        }

        foreach (var first in placed)
        {
            foreach (var second in placed.Where(candidate => candidate != first && candidate.Block == first.Block))
            {
                Assert.True(first.Offset + first.Size <= second.Offset || second.Offset + second.Size <= first.Offset);
            }
        }

        Assert.Equal(placed.Sum(memory => (long)memory.Size), (long)pool.PlacedBytes);
    }

    [Fact]
    public void FullBlock_GrowsANewBlockAndFreedRangesAreReused()
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device, blockSize: 1UL << 20);
        Assert.Equal(Result.Success, pool.Allocate(Requirements(600UL << 10), 0, out var first));
        Assert.Equal(Result.Success, pool.Allocate(Requirements(600UL << 10), 0, out var second));
        Assert.Equal(2, pool.Blocks);
        Assert.NotEqual(first.Block, second.Block);
        Assert.Equal(2, device.Allocations.Count);

        pool.Free(first);
        Assert.Equal(Result.Success, pool.Allocate(Requirements(500UL << 10), 0, out var reused));
        Assert.Equal(first.Block, reused.Block);
        Assert.Equal(first.Offset, reused.Offset);
        Assert.Equal(2, device.Allocations.Count);
    }

    [Fact]
    public void NeighbouringFreeRangesMerge()
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device, blockSize: 1UL << 20);
        pool.Allocate(Requirements(256UL << 10), 0, out var a);
        pool.Allocate(Requirements(256UL << 10), 0, out var b);
        pool.Allocate(Requirements(256UL << 10), 0, out var c);
        pool.Free(a);
        pool.Free(c);
        pool.Free(b);
        // The three ranges and the untouched tail coalesce back into the whole block.
        Assert.Equal(Result.Success, pool.Allocate(Requirements(1UL << 20), 0, out var whole));
        Assert.Equal(0UL, whole.Offset);
        Assert.Equal(1, pool.Blocks);
    }

    [Fact]
    public void MemoryTypesNeverShareABlock()
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device, blockSize: 1UL << 20);
        pool.Allocate(Requirements(4096), 0, out var first);
        pool.Allocate(Requirements(4096), 1, out var second);
        Assert.NotEqual(first.Memory, second.Memory);
        Assert.Equal(2, pool.Blocks);
    }

    [Fact]
    public void ThousandsOfImages_UseAFewDeviceAllocations()
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device);
        var placed = new List<OptimalImageMemoryAllocation>();
        for (var index = 0; index < 5000; index++)
        {
            Assert.Equal(Result.Success, pool.Allocate(Requirements(64UL << 10, 65536), 0, out var memory));
            placed.Add(memory);
        }

        // 5000 x 64 KiB = 312 MiB: two 256 MiB blocks instead of 5000 allocations.
        Assert.Equal(2, device.Allocations.Count);
        Assert.Equal(5000, pool.Placements);
        foreach (var memory in placed)
        {
            pool.Free(memory);
        }

        Assert.Equal(0, pool.Placements);
        Assert.Equal(0UL, pool.PlacedBytes);
    }

    [Fact]
    public void LargeImages_GetDedicatedAllocationsThatAreRetainedAndReused()
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device);
        var size = OptimalImageMemoryPool.DedicatedThreshold;
        Assert.Equal(Result.Success, pool.Allocate(Requirements(size), 0, out var large));
        Assert.Equal(-1, large.Block);
        Assert.Equal(0UL, large.Offset);
        Assert.Equal(new[] { size }, device.Requests);

        pool.Free(large);
        Assert.Equal(size, pool.AllocatedBytes);
        Assert.Equal(0UL, pool.PlacedBytes);
        Assert.Equal(Result.Success, pool.Allocate(Requirements(size), 0, out var again));
        Assert.Equal(large.Memory, again.Memory);
        Assert.Single(device.Requests);

        pool.Free(again);
        pool.ReleaseRetained();
        Assert.Empty(device.Allocations);
        Assert.Equal(0UL, pool.AllocatedBytes);
    }

    [Fact]
    public void EmptyBlocksAboveTheRetentionLimitGoBackToTheDevice()
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device, blockSize: 1UL << 20);
        var placed = new List<OptimalImageMemoryAllocation>();
        for (var index = 0; index < OptimalImageMemoryPool.MaxEmptyBlocks + 3; index++)
        {
            pool.Allocate(Requirements(1UL << 20), 0, out var memory);
            placed.Add(memory);
        }

        foreach (var memory in placed)
        {
            pool.Free(memory);
        }

        Assert.Equal(OptimalImageMemoryPool.MaxEmptyBlocks, pool.Blocks);
        pool.ReleaseRetained();
        Assert.Equal(0, pool.Blocks);
        Assert.Empty(device.Allocations);
    }

    [Fact]
    public void ReleaseRetained_KeepsBlocksThatStillHoldImages()
    {
        var device = new LimitedDevice(ulong.MaxValue);
        var pool = new OptimalImageMemoryPool(device, blockSize: 1UL << 20);
        pool.Allocate(Requirements(4096), 0, out var live);
        pool.Allocate(Requirements(1UL << 20), 0, out var other);
        pool.Free(other);
        pool.ReleaseRetained();
        Assert.Equal(1, pool.Blocks);
        Assert.Single(device.Allocations);
        pool.Free(live);
    }
}
