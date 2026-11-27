// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.VideoOut;
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
    public void PublicationMayReplaceBothMemoWaysDuringARead()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const int length = 512 * 1024;
        var address = harness.MapBacked(length, ReadWrite);
        var outer = address + Page;
        var set = GuestBufferCache.ObtainMemoSet(outer, Size);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        harness.Worker.Run(() =>
        {
            var owner = harness.Cache.ObtainBuffer(address, length, isWritten: false, allowStream: false).Buffer;
            var first = Read(harness, outer);
            var colliding = 0;
            harness.Cache.OverlayGuestPublications = (_, _, _, _) =>
            {
                harness.Cache.OverlayGuestPublications = null;
                for (var next = outer + 16; colliding < 2 && next + Size <= address + length; next += 16)
                {
                    if (GuestBufferCache.ObtainMemoSet(next, Size) != set) continue;
                    Assert.Same(owner, Read(harness, next).Buffer);
                    colliding++;
                }
            };
            var second = Read(harness, outer);
            Assert.Equal(2, colliding);
            Assert.Same(owner, second.Buffer);
            Assert.Equal(first.Offset, second.Offset);
        });
        harness.Shutdown();
    }

    [Fact]
    public void RecordedReadsRecheckPublicationsDirtyBytesAndBufferReplacementAtJoin()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var cache = harness.Cache;
        cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var publications = new PendingGuestPublications();
        var address = harness.MapBacked(0x10000, ReadWrite);
        var range = address + Page;
        var current = harness.Worker.Run(() => Read(harness, range)).Buffer;
        List<BufferSliceUse> uses = [new(range, Size, false, false, false, current)];
        long serial = cache.RegistrationSerial, clean = cache.CpuDirtyEpoch;
        var disjoint = publications.Add(1, range + Size, 4);
        int Join() => cache.ApplyRecordedBufferUses(uses, serial, clean, publications);
        var expected = 1;
        var overlays = 0;
        cache.OverlayGuestPublications = (_, _, start, length) =>
        {
            if (publications.HasOverlap(start, length, includeLabels: false)) overlays++;
        };
        harness.Worker.Run(() =>
        {
            Assert.Equal(expected, Join());
            Assert.Equal(0, cache.ApplyRecordedBufferUses(uses, serial, null, publications)); // No clean epoch from the recording.
            var late = publications.Add(1, range, 8);
            late.GpuSlot = 2;
            Assert.Equal(0, Join());
            Assert.Equal(1, overlays);
            publications.Publish(late, static () => { });
            publications.Publish(disjoint, static () => { });
            Assert.Equal(expected, Join());
        });

        var bytes = Bytes(Size, 42);
        Assert.True(harness.Store.MarkCpuWrite(range, Size));
        harness.Write(range, bytes);
        harness.Worker.Run(() => Assert.Equal(0, Join()));
        Assert.Equal(bytes, harness.ReadBack(current, current.Offset(range), Size));
        harness.Worker.Run(() =>
        {
            Assert.Equal(expected, Join()); // Epoch moved, fresh range check is clean again.
            _ = cache.FindBuffer(address, 0x8000);
            Assert.False(current.CacheIdentifier.IsValid);
            Assert.Equal(0, Join());
            var replacement = Read(harness, range).Buffer;
            Assert.NotSame(current, replacement);
            serial = cache.RegistrationSerial;
            uses[0] = new(range, Size, true, false, false, replacement);
            Assert.Equal(0, Join());
            Assert.True(cache.HasGpuDirtyBytes(range, Size));
        });
        harness.Shutdown();
    }

    [Fact]
    public void PrologueReadsTouchEveryOwnerWithTheProductionPublicationCallbacks()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var presenter = new PresenterUnderTest(_vulkan!);
        var cache = presenter.Harness.Cache;
        cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var publications = presenter.GetField<PendingGuestPublications>("_guestPublications");
        var address = presenter.Harness.MapBacked(0x40000, ReadWrite);
        var complete = presenter.Instance.GetType().GetMethod("CompleteOverlappingPublications", PresenterUnderTest.InstanceMembers)!
            .CreateDelegate<Action<ulong, ulong, bool, bool>>(presenter.Instance);
        cache.CompleteGuestPublications = (a, s, access) => complete(a, s, access == GuestBufferCache.PublicationAccess.Write,
            access == GuestBufferCache.PublicationAccess.OverlaidRead);
        cache.OverlayGuestPublications = presenter.Instance.GetType().GetMethod("OverlayPendingDumps", PresenterUnderTest.InstanceMembers)!
            .CreateDelegate<Action<GpuBuffer, ulong, ulong, ulong>>(presenter.Instance);
        presenter.Run(() =>
        {
            var uses = new List<BufferSliceUse>();
            for (int group = 0; group < 4; group++)
            {
                var start = address + (ulong)group * 0x10000;
                var buffer = Read(presenter.Harness, start).Buffer;
                for (int i = 0; i < 16; i++) uses.Add(new(start + (ulong)i * 128, 64, false, false, false, buffer));
            }
            var serial = cache.RegistrationSerial;
            var clean = cache.CpuDirtyEpoch;
            int Join() => cache.ApplyRecordedBufferUses(uses, serial, clean, publications);
            var owners = uses.Select(use => use.Buffer.CacheIdentifier).Distinct().ToHashSet();
            var fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var registry = (GuestBufferRegistry<GpuBuffer>)typeof(GuestBufferCache).GetField("_registry", fields)!.GetValue(cache)!;
            var policy = (BufferRetirementPolicy)typeof(GuestBufferCache).GetField("_retirementPolicy", fields)!.GetValue(cache)!;
            var previousTick = policy.CurrentTick;
            cache.RunGarbageCollector();
            int Untouched()
            {
                int count = 0;
                registry.VisitRetirementCandidates(previousTick, id => { if (owners.Contains(id)) count++; return false; });
                return count;
            }
            Assert.Equal(4, Untouched());
            Assert.Equal(uses.Count, Join());
            Assert.Equal(0, Untouched());
        });
    }

    [Fact]
    public void SliceReadProofRejectsChangedGuestBytesBeforeTheNextUse()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var use = harness.Worker.Run(() =>
        {
            var (buffer, _) = Read(harness, address);
            var slice = new BufferSliceUse(address, Size, false, false, false, buffer);
            harness.Cache.ApplySliceUse(slice);
            harness.Cache.ApplySliceUse(slice);
            return slice;
        });
        var bytes = Bytes(Size, 94);
        Assert.True(harness.Store.MarkCpuWrite(address, Size));
        harness.Write(address, bytes);
        harness.Worker.Run(() => harness.Cache.ApplySliceUse(use));
        Assert.Equal(bytes, harness.ReadBack(use.Buffer, use.Buffer.Offset(address), Size));
        harness.Shutdown();
    }

    [Fact]
    public void VersionedReadRepeatsPublicationsAndRejectsADirtyEpoch()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Cache.ObtainMemoUse = GuestBufferCache.ObtainMemoMode.On;
        var overlays = 0;
        harness.Cache.OverlayGuestPublications = (_, _, _, _) => overlays++;
        harness.Worker.Run(() =>
        {
            for (var i = 0; i < 4; i++) _ = Read(harness, address);
        });
        var bytes = Bytes(Size, 83);
        Assert.True(harness.Store.MarkCpuWrite(address, Size));
        harness.Write(address, bytes);
        var current = harness.Worker.Run(() => Read(harness, address));
        Assert.Equal(5, overlays);
        Assert.Equal(bytes, harness.ReadBack(current.Buffer, current.Offset, Size));
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
