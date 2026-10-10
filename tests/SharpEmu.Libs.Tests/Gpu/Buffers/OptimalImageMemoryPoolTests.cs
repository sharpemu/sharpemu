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
                return Result.ErrorOutOfDeviceMemory;
            memory = new DeviceMemory(++_next);
            Allocations.Add(memory.Handle, info.AllocationSize);
            return Result.Success;
        }
        public void FreeMemory(DeviceMemory memory) => Allocations.Remove(memory.Handle);
    }

    [Fact]
    public void LargeBlockRejected_SmallImageStillFitsAndIsReleased()
    {
        var device = new LimitedDevice(32UL << 20);
        var pool = new OptimalImageMemoryPool(device);
        var requirements = new MemoryRequirements { Size = 22UL << 20, Alignment = 256, MemoryTypeBits = 1 };
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
        var requirements = new MemoryRequirements { Size = 22UL << 20, Alignment = 256, MemoryTypeBits = 1 };
        Assert.Equal(Result.ErrorOutOfDeviceMemory, pool.Allocate(requirements, 0, out _));
        Assert.Empty(device.Allocations);
        Assert.Equal(0UL, pool.AllocatedBytes);
        Assert.Equal(0UL, pool.PlacedBytes);
        Assert.Equal(0, pool.Placements);
    }
}
