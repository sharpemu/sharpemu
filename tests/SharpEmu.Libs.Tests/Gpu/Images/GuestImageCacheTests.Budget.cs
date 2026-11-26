// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void CompletedPoolsExpireWithoutBudgetPressureAndRecreateOnDemand()
    {
        if (!GatePrerequisites.Ready(_vulkan) || !ImageBackingPool.Enabled) return;
        using var harness = new CacheHarness(_vulkan);
        var request = Color32(harness.MapBacked(0x10000, ReadWrite));
        var id = harness.Acquire(ref request);
        var cache = harness.Images;
        harness.Worker.Run(() =>
        {
            cache.DeleteImageForTest(id);
            cache.TilerForTest.GetScratchBuffer(8UL << 20);
            Assert.Equal((0UL, 0UL), cache.TrimExpiredPools(Environment.TickCount64));
        });
        harness.Finish();
        harness.Worker.Run(() =>
        {
            var released = cache.TrimExpiredPools(Environment.TickCount64 + 12_000);
            Assert.True(released.Scratch >= 8UL << 20);
            Assert.True(released.Images > 0);
        });
        Assert.True(harness.Acquire(ref request).IsValid);
        harness.Worker.Run(() => cache.TilerForTest.GetScratchBuffer(8UL << 20));
        harness.Finish();
    }

    [Theory]
    [InlineData(4096, 256)]
    [InlineData(8192, 512)]
    [InlineData(24576, 512)]
    public void BudgetPressureRecoversWithHeadroom(int budgetMiB, int marginMiB)
    {
        var budget = (ulong)budgetMiB << 20;
        var margin = (ulong)marginMiB << 20;
        var near = false;
        Assert.Equal(0UL, GuestImageCache.BudgetReclaimTarget(budget, budget - margin, ref near));
        Assert.False(near);
        Assert.Equal(margin + 1, GuestImageCache.BudgetReclaimTarget(budget, budget - margin + 1, ref near));
        Assert.True(near);
        Assert.Equal(1UL, GuestImageCache.BudgetReclaimTarget(budget, budget - 2 * margin + 1, ref near));
        Assert.Equal(0UL, GuestImageCache.BudgetReclaimTarget(budget, budget - 2 * margin, ref near));
        Assert.False(near);
        Assert.Equal(3 * margin, GuestImageCache.BudgetReclaimTarget(budget, budget + margin, ref near));
        Assert.Equal(0UL, GuestImageCache.BudgetReclaimTarget(0, budget, ref near));
        Assert.False(near);
    }

    [Fact]
    public void BudgetHeadroomCoalescesOnlyUnchangedSubmissions()
    {
        ulong last = 0;
        Assert.True(GuestImageCache.NeedsRecencyScan(true, false, false, 1, ref last));
        Assert.False(GuestImageCache.NeedsRecencyScan(true, false, false, 1, ref last));
        Assert.True(GuestImageCache.NeedsRecencyScan(true, false, false, 2, ref last));
        Assert.True(GuestImageCache.NeedsRecencyScan(true, true, false, 2, ref last));
        Assert.True(GuestImageCache.NeedsRecencyScan(true, false, true, 2, ref last));
        Assert.True(GuestImageCache.NeedsRecencyScan(false, false, false, 2, ref last));
    }

    [Fact]
    public void BudgetTrimsOnlyCompletedTilerScratch()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var device = harness.Device;
        var baseline = device.LiveAllocations;
        harness.Run(() =>
        {
            harness.Tiler.GetScratchBuffer(64);
            harness.Tiler.GetScratchBuffer(64);
            for (uint heap = 0; heap < device.MemoryHeapCount; heap++)
                Assert.Equal(0UL, harness.Tiler.TrimIdleScratch(heap, 1, long.MaxValue));
            harness.Scheduler.Finish();
            for (uint heap = 0; heap < device.MemoryHeapCount; heap++)
            {
                Assert.Equal(0UL, harness.Tiler.TrimIdleScratch(heap, 1, long.MinValue));
                if (harness.Tiler.TrimIdleScratch(heap, 1, long.MaxValue) > 0)
                    break;
            }
            Assert.Equal(baseline + 1, device.LiveAllocations);
        });
        harness.AssertNoValidationMessages();
    }

    [Fact]
    public void BudgetRetirementStopsAtTheTargetAndPreservesOwners()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var modified = Color32(address);
        modified.Description.TileMode = GuestTileMode.Standard4KB;
        var dirty = harness.Find(ref modified);
        harness.MarkGpuWritten(dirty);
        var held = Color32(address + 0x4000);
        var bound = harness.Acquire(ref held);
        var first = Color32(address + 0x8000);
        var second = Color32(address + 0xC000);
        harness.Write(first.Description.Data.Address, Bytes(0x12345678u));
        var clean = harness.Acquire(ref first);
        var other = harness.Acquire(ref second);
        var backing = harness.Image(clean);
        harness.Worker.Run(() =>
        {
            var cache = harness.Images;
            var heap = backing.Backing.MemoryHeap;
            harness.Image(bound).HeapChanged = static _ => { };
            cache.ResetRecency([dirty, bound, clean, other], 100);
            Assert.Equal(0UL, cache.RetireColdTextures(heap, 1, 1));
            Assert.Equal(0UL, cache.RetireColdTextures(uint.MaxValue, 1, 100));
            Assert.Equal(backing.Backing.AllocationSize, cache.RetireColdTextures(heap, 1, 100));
            Assert.True(cache.Contains(dirty) && harness.Image(dirty).IsGpuModified);
            Assert.True(cache.Contains(bound) && cache.Contains(other));
            Assert.False(cache.Contains(clean));
            Assert.True(backing.Backing.Exists);
            harness.Image(bound).HeapChanged = null;
        });
        harness.Finish();
        Assert.False(backing.Backing.Exists);
        var replacement = harness.Acquire(ref first);
        Assert.Equal(Bytes(0x12345678u), harness.ReadImageBytes(harness.Image(replacement)));
        _vulkan.AssertNoValidationMessages();
    }

    [Fact]
    public void BudgetAllocationRetryRequiresImmediateReleaseFromTheFailedHeap()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new ImageTestHarness(_vulkan);
        var device = harness.Device;
        using var pool = new ImageBackingPool(device);
        var description = CachedImageTests.Color2D(16, 16);
        uint heap;
        using (var idle = new CachedImage(device, harness.Scheduler, harness.Guest, description, pool))
            heap = idle.Backing.MemoryHeap;
        var before = device.LiveAllocations;
        Assert.Equal(0UL, pool.Trim(uint.MaxValue, 1, long.MaxValue));
        Assert.Equal(0UL, pool.Trim(heap, 1, long.MinValue));
        var keys = Assert.IsAssignableFrom<System.Collections.IDictionary>(typeof(ImageBackingPool)
            .GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(pool));
        Assert.Single(keys.Keys);
        using (var reused = new CachedImage(device, harness.Scheduler, harness.Guest, description, pool))
        {
            Assert.Empty(keys.Keys);
            Assert.Equal(before, device.LiveAllocations);
        }
        Assert.Single(keys.Keys);
        var calls = new List<uint>();
        var previous = device.ReleaseIdleMemory;
        try
        {
            var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = 4096 };
            info.MemoryTypeIndex = Enumerable.Range(0, (int)device.MemoryTypeCount).Select(i => (uint)i).First(i => device.GetMemoryHeapIndex(i) == heap);
            device.ReleaseIdleMemory = (failedHeap, size) => pool.Trim(failedHeap, size, long.MaxValue);
            device.SimulateExhaustedMemoryType = type => { calls.Add(type); return calls.Count == 1; };
            Assert.Equal(Result.Success, device.AllocateMemory(info, out var memory));
            device.FreeMemory(memory);
            Assert.Equal(new[] { info.MemoryTypeIndex, info.MemoryTypeIndex }, calls);
            Assert.Equal(before - 1, device.LiveAllocations);
            Assert.Empty(keys.Keys);
            calls.Clear();
            Assert.Equal(Result.ErrorOutOfDeviceMemory, device.AllocateMemory(info, out _));
            Assert.Single(calls);
        }
        finally
        {
            device.ReleaseIdleMemory = previous;
            device.SimulateExhaustedMemoryType = null;
        }
        _vulkan.AssertNoValidationMessages();
    }
}
