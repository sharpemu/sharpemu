// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

// Read obtains served from the memo of persistent buffers, the reads it must leave to the full path, and its switch.
[Collection(SchedulingStateCollection.Name)]
public sealed class GuestBufferObtainMemoTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Page = GuestBufferCache.CachingPageSize;
    private const ulong Size = 2 * Page;

    private readonly HeadlessVulkan? _vulkan = fixture.Vulkan;

    private static byte[] Bytes(ulong length, byte seed) => Enumerable.Range(0, (int)length).Select(index => (byte)(seed + index * 13)).ToArray();

    private static (GpuBuffer Buffer, ulong Offset) Read(CacheHarness harness, ulong address, bool texel = false) =>
        harness.Cache.ObtainBuffer(address, Size, isWritten: false, isTexelBuffer: texel, allowStream: false);

    [Fact]
    public void RepeatedCleanReadsAreServed_AndACpuWriteTakesTheFullPathAgain()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(Size, 1));
        harness.Worker.Run(() =>
        {
            var first = Read(harness, address);
            Assert.Equal(0, harness.Cache.ObtainMemoHits);
            var second = Read(harness, address);
            Assert.Equal(1, harness.Cache.ObtainMemoHits);
            Assert.Same(first.Buffer, second.Buffer);
            Assert.Equal(first.Offset, second.Offset);
        });

        // An unmap reaches the cache the same way: its pages turn CPU-dirty.
        var latest = Bytes(Size, 99);
        Assert.True(harness.Store.MarkCpuWrite(address, Size));
        harness.Write(address, latest);
        var current = harness.Worker.Run(() =>
        {
            var read = Read(harness, address);
            Assert.Equal(1, harness.Cache.ObtainMemoHits);
            _ = Read(harness, address);
            Assert.Equal(2, harness.Cache.ObtainMemoHits);
            return read;
        });
        Assert.Equal(latest, harness.ReadBack(current.Buffer, current.Offset, Size));
        harness.Shutdown();
    }

    [Fact]
    public void TwoRangesOfOneSetAreBothServed()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var address = harness.MapBacked(0x40000, ReadWrite);
        var first = address;
        var second = 0UL;
        // A second range of the same size in the same set, a page clear of the first so neither buffer merges the other.
        for (var offset = Size + Page; offset + Size <= 0x40000; offset += 0x10)
        {
            if (GuestBufferCache.ObtainMemoSet(address + offset, Size) == GuestBufferCache.ObtainMemoSet(first, Size))
            {
                second = address + offset;
                break;
            }
        }

        Assert.NotEqual(0UL, second);
        harness.Worker.Run(() =>
        {
            for (var round = 0; round < 3; round++)
            {
                _ = Read(harness, first);
                _ = Read(harness, second);
            }

            Assert.Equal(4, harness.Cache.ObtainMemoHits);
        });
        harness.Shutdown();
    }

    [Fact]
    public void ACleanTexelReadOverAnImageIsServed_AndStillTakesTheImageBytes()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes(Size, 5));
        var images = new ImageOverEveryRange();
        harness.Cache.ImageCache = images;
        harness.Worker.Run(() =>
        {
            var first = Read(harness, address, texel: true);
            var second = Read(harness, address, texel: true);
            Assert.Equal(1, harness.Cache.ObtainMemoHits);
            Assert.Same(first.Buffer, second.Buffer);
        });
        Assert.Equal(2, images.Synchronizations);
        harness.Shutdown();
    }

    [Fact]
    public void AnOwnerMergedIntoALargerBufferIsNotServed()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var address = harness.MapBacked(0x10000, ReadWrite);
        var range = address + Page;
        var expected = Bytes(Size, 7);
        harness.Write(range, expected);
        var current = harness.Worker.Run(() =>
        {
            var first = Read(harness, range);
            _ = harness.Cache.FindBuffer(address, 0x8000);
            var second = Read(harness, range);
            Assert.Equal(0, harness.Cache.ObtainMemoHits);
            Assert.NotSame(first.Buffer, second.Buffer);
            _ = Read(harness, range);
            Assert.Equal(1, harness.Cache.ObtainMemoHits);
            return second;
        });
        Assert.Equal(expected, harness.ReadBack(current.Buffer, current.Offset, Size));
        harness.Shutdown();
    }

    // A written obtain of the range is not served and leaves the memo for the reads after it.
    [Fact]
    public void AWrittenObtainTakesTheFullPath()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var read = Read(harness, address);
            var written = harness.Cache.ObtainBuffer(address, Size, isWritten: true);
            Assert.Equal(0, harness.Cache.ObtainMemoHits);
            Assert.Same(read.Buffer, written.Buffer);
            Assert.True(harness.Cache.HasGpuDirtyBytes(address, Size));
            _ = Read(harness, address);
            Assert.Equal(1, harness.Cache.ObtainMemoHits);
        });
        harness.Shutdown();
    }

    [Fact]
    public void OffServesNoRead()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.Off;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var first = Read(harness, address);
            var second = Read(harness, address);
            Assert.Same(first.Buffer, second.Buffer);
            Assert.Equal(0, harness.Cache.ObtainMemoHits);
            Assert.Equal(0, harness.Cache.ObtainMemoVerifications);
        });
        harness.Shutdown();
    }

    [Fact]
    public void VerifyComparesEveryReadItWouldServeAndServesNone()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.Verify;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            for (var use = 0; use < 4; use++)
            {
                _ = Read(harness, address);
            }

            Assert.Equal(0, harness.Cache.ObtainMemoHits);
            Assert.Equal(3, harness.Cache.ObtainMemoVerifications);
            Assert.Equal(0, harness.Cache.ObtainMemoMismatches);
        });
        harness.Shutdown();
    }

    // A remembered answer that differs from the full path's is counted, and the full path's answer is bound.
    [Fact]
    public void VerifyCountsAnAnswerTheFullPathDoesNotGive()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.Verify;
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Worker.Run(() =>
        {
            var first = Read(harness, address);
            ShiftRememberedOffset(harness.Cache, address, Size, 0x100);
            var second = Read(harness, address);
            Assert.Equal(first.Offset, second.Offset);
            Assert.Equal(1, harness.Cache.ObtainMemoVerifications);
            Assert.Equal(1, harness.Cache.ObtainMemoMismatches);
        });
        harness.Shutdown();
    }

    private static void ShiftRememberedOffset(GuestBufferCache cache, ulong address, ulong size, ulong shift)
    {
        var memos = (Array)typeof(GuestBufferCache).GetField("_obtainMemos", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        var set = GuestBufferCache.ObtainMemoSet(address, size);
        for (var way = set; way < set + 2; way++)
        {
            var memo = memos.GetValue(way)!;
            var type = memo.GetType();
            if ((ulong)type.GetProperty("Address")!.GetValue(memo)! != address || type.GetProperty("Buffer")!.GetValue(memo) is null)
            {
                continue;
            }

            memos.SetValue(Activator.CreateInstance(type, address, size, type.GetProperty("Identifier")!.GetValue(memo),
                (ulong)type.GetProperty("Offset")!.GetValue(memo)! + shift, type.GetProperty("Buffer")!.GetValue(memo)), way);
            return;
        }

        Assert.Fail("The range is not remembered.");
    }

    // Every range may hold image bytes, and none needs a copy: counts the texel synchronizations asked of it.
    private sealed class ImageOverEveryRange : IGuestImageCache
    {
        public int Synchronizations;

        public ImageRegionInfo QueryRegion(ulong address, ulong size) => default;

        public bool ClearMetadata(ulong address) => false;

        public bool OverlapsDccMetadata(ulong address, ulong size) => false;

        public void InvalidateMemory(ulong address, ulong size)
        {
        }

        public void InvalidateMemoryFromGpu(ulong address, ulong size)
        {
        }

        public bool TrySynchronizeBufferFromImage(GpuBuffer buffer, ulong address, ulong size)
        {
            Synchronizations++;
            return false;
        }

        public bool MayHaveImageBytes(ulong address, ulong size) => true;

        public bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear) => false;
    }
}
