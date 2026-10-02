// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Readback publication: scheduled readbacks, garbage collection under pressure, depth planes.
public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void AllocationPressure_CompletesRetirementAndKeepsCurrentImages()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new SharpEmu.Libs.Tests.Gpu.Scheduling.FatalScope();
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 1);
        var oldRequest = Color32(address);
        var oldImage = harness.Acquire(ref oldRequest);
        var oldOwner = harness.Image(oldImage);
        harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 2);
        var currentRequest = Color32(address + 256);
        var currentImage = harness.Acquire(ref currentRequest);
        var pool = (OptimalImageMemoryPool)typeof(GuestImageCache).GetField("_imageMemoryPool",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(harness.Images)!;
        var before = harness.Scheduler.CurrentTick;
        var callbackRan = false;
        harness.Worker.Run(() =>
        {
            harness.Scheduler.QueueCompletionAction(() =>
            {
                // Completion must be able to acquire the image cache lock.
                _ = harness.Images.FindImagesInRangeForTest(address + 256, 256, false);
                callbackRan = true;
            });
            harness.Images.SetCollectionThresholds(0, 0, pool.AllocatedBytes - 1, 2);
            var nextRequest = Color32(address + 512);
            _ = harness.Images.FindImage(ref nextRequest);
        });
        Assert.True(callbackRan);
        Assert.True(harness.Scheduler.CurrentTick > before);
        Assert.False(harness.Images.Contains(oldImage));
        Assert.False(oldOwner.Backing.Exists);
        Assert.True(harness.Images.Contains(currentImage));
        harness.Shutdown();
    }

    [Fact]
    public void AllocationPressure_DoesNotRetireTheEntireOldWorkingSetBeforeCompletion()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 1);
        var oldImages = new List<ResourceSlotIdentifier>();
        for (var i = 0; i < 60; i++)
        {
            var request = Color32(address + (ulong)i * 256);
            oldImages.Add(harness.Acquire(ref request));
        }
        var pool = (OptimalImageMemoryPool)typeof(GuestImageCache).GetField("_imageMemoryPool",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(harness.Images)!;
        harness.Worker.Run(() =>
        {
            harness.Images.SetCollectionThresholds(0, 0, pool.AllocatedBytes - 1, 2);
            var request = Color32(address + 60 * 256);
            _ = harness.Images.FindImage(ref request);
        });
        // One retirement batch satisfies this small request. Waiting for the
        // total resident counter to drop instead used to discard all 60.
        Assert.InRange(oldImages.Count(harness.Images.Contains), 20, 59);
        harness.Shutdown();
    }

    [Fact]
    public void OversizedReadbacks_PublishBothImagesWithoutGrowingTheSharedRing()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new SharpEmu.Libs.Tests.Gpu.Scheduling.FatalScope();
        const uint width = 4096, height = 2049;
        const ulong size = (ulong)width * height * 4;
        using var harness = new CacheHarness(_vulkan, backingBytes: 80UL * 1024 * 1024);
        var address = harness.MapBacked(size * 2, ReadWrite);
        var download = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Download);
        var ringSize = download.Size;
        Assert.True(size > ringSize);
        ulong[] samples = [0, size / 2, size - 4];
        uint[] values = [0x11223344, 0x55667788];
        var images = new List<ResourceSlotIdentifier>();
        var tick = harness.Scheduler.CurrentTick;
        for (var index = 0; index < values.Length; index++)
        {
            var imageAddress = address + (ulong)index * size;
            var request = LinearRequest(imageAddress, size, Format.R8G8B8A8Unorm,
                GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color2D, new Extent3D(width, height, 1), 1, 4, 1);
            var image = harness.Find(ref request);
            var value = values[index];
            harness.Worker.Run(() => Assert.True(harness.Images.TryClearImageFromBuffer(imageAddress, size, value)));
            foreach (var sample in samples) harness.Write(imageAddress + sample, Bytes(0u));
            images.Add(image);
        }
        var baselineAllocations = harness.Vulkan.DeviceInfo.LiveAllocations;
        foreach (var image in images)
            Assert.True(harness.Worker.Run(() => harness.Images.TryDownloadForTest(image)));
        Assert.Equal(tick, harness.Scheduler.CurrentTick);
        foreach (var index in Enumerable.Range(0, values.Length))
            foreach (var sample in samples) Assert.Equal(0u, harness.ReadUInt32(address + (ulong)index * size + sample));
        harness.Finish();
        Assert.Equal(baselineAllocations, harness.Vulkan.DeviceInfo.LiveAllocations);
        foreach (var index in Enumerable.Range(0, values.Length))
            foreach (var sample in samples) Assert.Equal(values[index], harness.ReadUInt32(address + (ulong)index * size + sample));
        Assert.Same(download, harness.Cache.GetUtilityBuffer(GpuBufferUsage.Download));
        Assert.Equal(ringSize, download.Size);
        harness.Shutdown();
    }

    [Fact]
    public void Pressure_RetiresTiledGpuImageAndPreservesItsGuestContents()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong size = 0x10000;
        const uint clear = 0x3f200000, stale = 0xdeadbeef;
        var address = harness.MapBacked(size, ReadWrite);
        harness.Write(address, Bytes(Enumerable.Repeat(stale, (int)(size / 4)).ToArray()));
        var request = AsDepthTarget(LinearRequest(address, size, Format.D32Sfloat,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(3, 2, 1), 1, 4, 1), Format.D32Sfloat);
        request.Description.Pitch = 4;
        request.Description.TileMode = GuestTileMode.Depth;
        request.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = 4, Height = 2 };
        var image = harness.Find(ref request);
        harness.Worker.Run(() =>
        {
            Assert.True(harness.Images.TryClearImageFromBuffer(address, size, clear));
            harness.Images.SetCollectionThresholds(0, 0, 1, 2);
            harness.Images.ResetRecency([image], 2);
            harness.Images.RunGarbageCollector();
        });
        Assert.False(harness.Images.Contains(image));
        Assert.All(harness.Read(address, (int)size).Chunk(4), bytes => Assert.Equal(stale, BitConverter.ToUInt32(bytes)));
        harness.Finish();
        var result = harness.Read(address, (int)size).Chunk(4).Select(bytes => BitConverter.ToUInt32(bytes)).ToArray();
        Assert.Equal(6, result.Count(value => value == clear));
        Assert.Equal(result.Length - 6, result.Count(value => value == stale));
        harness.Shutdown();
    }

    [Fact]
    public void Pressure_CompletesTiledReadbackBatchesBeforeAllocatingMoreScratch()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong size = 0x10000;
        const uint clear = 0x3f200000, stale = 0xdeadbeef;
        var address = harness.MapBacked(3 * size, ReadWrite);
        harness.Write(address, Bytes(Enumerable.Repeat(stale, (int)(3 * size / 4)).ToArray()));
        harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 1);
        var images = new List<ResourceSlotIdentifier>();
        for (var index = 0; index < 3; index++)
        {
            var imageAddress = address + (ulong)index * size;
            var request = AsDepthTarget(LinearRequest(imageAddress, size, Format.D32Sfloat,
                GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(3, 2, 1), 1, 4, 1), Format.D32Sfloat);
            request.Description.Pitch = 4;
            request.Description.TileMode = GuestTileMode.Depth;
            request.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = 4, Height = 2 };
            images.Add(harness.Find(ref request));
            harness.Worker.Run(() => Assert.True(harness.Images.TryClearImageFromBuffer(imageAddress, size, clear)));
        }
        harness.Worker.Run(() =>
        {
            harness.Images.TilerForTest.OutstandingScratchBudget = 1;
            harness.Images.SetCollectionThresholds(0, 0, ulong.MaxValue, 2);
            harness.Images.ResetRecency(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(images), 2);
            var tick = harness.Scheduler.CurrentTick;
            harness.Images.RunGarbageCollector();
            Assert.True(harness.Scheduler.CurrentTick > tick);
            Assert.All(images, image => Assert.False(harness.Images.Contains(image)));
            Assert.Equal(0UL, harness.Images.TilerForTest.PooledScratchBytes);
        });
        // Earlier batches have already published; the final batch may be pending.
        Assert.Contains(clear, harness.Read(address, (int)size).Chunk(4).Select(bytes => BitConverter.ToUInt32(bytes)));
        harness.Finish();
        for (var index = 0; index < 3; index++)
        {
            var words = harness.Read(address + (ulong)index * size, (int)size).Chunk(4).Select(bytes => BitConverter.ToUInt32(bytes)).ToArray();
            Assert.Equal(6, words.Count(word => word == clear));
            Assert.Equal(words.Length - 6, words.Count(word => word == stale));
        }
        harness.Shutdown();
    }

    [Fact]
    public void ProtectedStencilProxies_DoNotHideLaterEvictableImages()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 1);
        var ownerRequest = Color32(address);
        var owner = harness.Acquire(ref ownerRequest);
        var proxies = new List<ResourceSlotIdentifier>();
        for (var index = 1; index <= 40; index++)
        {
            var request = Color32(address + (ulong)index * 256);
            var proxy = harness.Acquire(ref request);
            harness.Image(proxy).DepthOwner = owner;
            proxies.Add(proxy);
        }
        var victimRequest = Color32(address + 41 * 256);
        var victim = harness.Acquire(ref victimRequest);
        harness.Worker.Run(() =>
        {
            harness.Images.SetCollectionThresholds(0, 0, 1, 2);
            _ = harness.Images.GetImage(owner);
            harness.Images.RunGarbageCollector();
        });
        Assert.False(harness.Images.Contains(victim));
        Assert.True(harness.Images.Contains(owner));
        Assert.All(proxies, proxy => Assert.True(harness.Images.Contains(proxy)));
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void CriticalPressure_RetiresRecentlyUnusedImagesButKeepsCurrentBindings()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var oldRequest = Color32(address);
        var currentRequest = Color32(address + 256);
        harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 1);
        var oldImage = harness.Acquire(ref oldRequest);
        harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 2);
        var currentImage = harness.Acquire(ref currentRequest);
        harness.Worker.Run(() =>
        {
            harness.Images.SetCollectionThresholds(0, 0, 1, 2);
            harness.Images.RunGarbageCollector();
        });
        Assert.False(harness.Images.Contains(oldImage));
        Assert.True(harness.Images.Contains(currentImage));
        // Retirement must not destroy memory still referenced by recorded work.
        Assert.True(harness.Image(oldImage).Backing.Exists);
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void SmallTextures_ShareDeviceMemoryWithoutSharingTheirContents()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var memories = new HashSet<ulong>();
        for (uint index = 0; index < 64; index++)
        {
            var request = Color32(address + index * 256);
            harness.Write(request.Description.Data.Address, Bytes(index + 1));
            var id = harness.Acquire(ref request);
            memories.Add(harness.Image(id).Backing.Memory.Handle);
            var textureAddress = request.Description.Data.Address;
            var clearValue = index + 128;
            harness.Worker.Run(() =>
            {
                Assert.True(harness.Images.TryClearImageFromBuffer(textureAddress, 4, clearValue));
                Assert.True(harness.Images.TryDownloadForTest(id));
            });
        }
        Assert.Single(memories);
        harness.Finish();
        for (uint index = 0; index < 64; index++)
            Assert.Equal(index + 128, BitConverter.ToUInt32(harness.Read(address + index * 256, 4)));
        harness.Shutdown();
    }

    [Fact]
    public void RetiredImages_StayAccountedUntilTheirGpuCompletion()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = Color32(address);
        var id = harness.Acquire(ref request);
        var allocated = harness.Images.TotalUsedMemory;
        Assert.True(allocated > 0);
        harness.Images.SetCollectionThresholds(allocated, allocated, allocated, 0);
        Assert.False(harness.Images.RetirementOverBudget);
        harness.Worker.Run(() => ((IGuestImageStore)harness.Images).Unregister(address, 4));
        Assert.False(harness.Images.Contains(id));
        Assert.True(harness.Image(id).Backing.Exists);
        Assert.Equal(allocated, harness.Images.TotalUsedMemory);
        Assert.True(harness.Images.RetirementOverBudget);
        harness.Finish();
        Assert.False(harness.Images.RetirementOverBudget);
        Assert.Equal(0UL, harness.Images.TotalUsedMemory);
        harness.Shutdown();
    }

    [Fact]
    public void GarbageCollector_RetiresUnderPressureAndPublishesInOneTick()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x400000, ReadWrite);
        ulong[] offsets = [0x330000, 0x332000];
        uint[] values = [0x76543210u, 0x89abcdefu];
        uint[] stale = [0x10293847u, 0x56473829u];
        for (var index = 0; index < 2; index++)
        {
            harness.Write(address + offsets[index], Bytes(values[index]));
        }

        var requestA = Color32(address + offsets[0]);
        var requestB = Color32(address + offsets[1]);
        harness.Worker.Run(() =>
        {
            Assert.NotNull(harness.Cache.ObtainBuffer(requestA.Description.Data.Address, 4, isWritten: true).Buffer);
        });
        Assert.True(harness.Cache.TrySynchronizeCpuRead(requestA.Description.Data.Address, 4));
        ResourceSlotIdentifier[] images = [harness.Find(ref requestA), harness.Find(ref requestB)];

        // Only a clean GPU-current image can be published; CPU-dirty contents are not.
        harness.Worker.Run(() =>
        {
            using var download = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, requestA.Description.Data.Address, GpuBuffer.AllFlags, 4);
            Assert.False(harness.Images.TrySynchronizeBufferFromImage(download, requestA.Description.Data.Address, 4));
        });
        harness.Acquire(ref requestA);
        harness.Acquire(ref requestB);
        foreach (var image in images)
        {
            harness.MarkGpuWritten(image);
        }

        harness.Worker.Run(() =>
        {
            using var download = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, requestA.Description.Data.Address, GpuBuffer.AllFlags, 4);
            Assert.True(harness.Images.TrySynchronizeBufferFromImage(download, requestA.Description.Data.Address, 4));
            harness.Scheduler.Finish();
        });
        harness.Image(images[0]).InvalidateCpuWrite(requestA.Description.Data.Address, 4);
        Assert.False(harness.Image(images[0]).SafeToDownload);
        harness.MarkGpuWritten(images[0]);
        for (var index = 0; index < 2; index++)
        {
            harness.Write(address + offsets[index], Bytes(stale[index]));
        }

        harness.Worker.Run(() =>
        {
            harness.Images.SetCollectionThresholds(0, ulong.MaxValue, ulong.MaxValue, 17);
            harness.Images.ResetRecency(images, 17);
            harness.Images.RunGarbageCollector();
        });
        Assert.All(images, image => Assert.True(harness.Images.Contains(image)));

        var batchTick = harness.Scheduler.CurrentTick;
        harness.Worker.Run(() =>
        {
            harness.Images.SetCollectionThresholds(0, 0, ulong.MaxValue, 81);
            harness.Images.ResetRecency(images, 81);
            harness.Images.RunGarbageCollector();
        });
        Assert.All(images, image => Assert.False(harness.Images.Contains(image)));
        Assert.Equal(batchTick, harness.Scheduler.CurrentTick);
        Assert.Equal(stale[0], harness.ReadUInt32(address + offsets[0]));
        Assert.Equal(stale[1], harness.ReadUInt32(address + offsets[1]));
        harness.Finish();
        var (refreshed, refreshedOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(requestA.Description.Data.Address, 4, isWritten: false));
        Assert.NotNull(refreshed);
        Assert.Equal(batchTick + 1, harness.Scheduler.CurrentTick);
        Assert.Equal(values[0], harness.ReadUInt32(address + offsets[0]));
        Assert.Equal(values[1], harness.ReadUInt32(address + offsets[1]));
        Assert.Equal(Bytes(values[0]), harness.ReadBufferBytes(refreshed, refreshedOffset, 4));
        harness.Shutdown();
    }

    [Fact]
    public void ScheduledReadback_PublishesAfterTheTickAndKeepsTheImage()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var request = AsStorage(Color32(address + 0x6000));
        harness.Images.SetLinearReadback(true);
        var imageIdentifier = harness.Find(ref request);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address + 0x6000, 4, 0x13579bdfu)));
        harness.Worker.Run(() => harness.Images.ScheduleReadbackForTest(imageIdentifier));
        Assert.True(harness.Images.IsReadbackScheduled(imageIdentifier));
        harness.Write(address + 0x6000, Bytes(0x2468ace0u));
        var tick = harness.Scheduler.CurrentTick;
        harness.Worker.Run(() => harness.Images.FlushScheduledReadbacks());
        harness.Images.SetLinearReadback(false);
        Assert.Equal(0x2468ace0u, harness.ReadUInt32(address + 0x6000));
        Assert.Equal(tick, harness.Scheduler.CurrentTick);
        harness.Finish();
        Assert.Equal(0x13579bdfu, harness.ReadUInt32(address + 0x6000));
        Assert.True(harness.Images.Contains(imageIdentifier));
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        Assert.False(harness.Images.IsReadbackScheduled(imageIdentifier));

        // With linear readback enabled, acquiring a storage view enrolls the image.
        harness.Images.SetLinearReadback(true);
        var again = request;
        var againId = harness.Acquire(ref again);
        Assert.Equal(imageIdentifier, againId);
        Assert.True(harness.Images.IsReadbackScheduled(imageIdentifier));
        harness.Shutdown();
    }

    [Fact]
    public void LinearDepthReadback_PreservesPaddingAndStencil()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xa000;
        var stencilAddress = address + 0xb000;
        const uint width = 3, height = 2, pitch = 4, layers = 2;
        const uint words = pitch * height * layers;
        const uint clear = 0x3f400000u;
        var guest = new uint[words];
        for (uint index = 0; index < words; index++)
        {
            guest[index] = 0x51000000u + index;
        }

        byte[] stencil = [0x91, 0x82, 0x73, 0x64, 0x55, 0x46, 0x37, 0x28];
        harness.Write(depthAddress, Bytes(guest));
        harness.Write(stencilAddress, stencil);
        var request = LinearRequest(depthAddress, words * 4, Format.D32SfloatS8Uint, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(width, height, 1), layers, 4, 1);
        request = AsDepthTarget(request, Format.D32SfloatS8Uint);
        request.Description.Pitch = pitch;
        request.Description.Stencil = new GuestSpan(stencilAddress, 8);
        request.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = words * 4, Pitch = pitch, Height = height };
        request.View = request.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var imageIdentifier = harness.Find(ref request);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(depthAddress, words * 4, clear)));
        harness.Images.SetLinearReadback(true);
        harness.Worker.Run(() => harness.Images.ScheduleReadbackForTest(imageIdentifier));
        var tick = harness.Scheduler.CurrentTick;
        harness.Worker.Run(() => harness.Images.FlushScheduledReadbacks());
        harness.Images.SetLinearReadback(false);
        Assert.Equal(Bytes(guest), harness.Read(depthAddress, (int)(words * 4)));
        Assert.Equal(tick, harness.Scheduler.CurrentTick);
        harness.Finish();
        var after = harness.Read(depthAddress, (int)(words * 4));
        for (uint layer = 0; layer < layers; layer++)
        {
            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < pitch; x++)
                {
                    var index = layer * pitch * height + y * pitch + x;
                    var expected = x < width ? clear : guest[index];
                    Assert.Equal(expected, BitConverter.ToUInt32(after, (int)index * 4));
                }
            }
        }

        Assert.Equal(stencil, harness.Read(stencilAddress, 8));
        Assert.True(harness.Images.Contains(imageIdentifier));
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        harness.Shutdown();
    }

    [Fact]
    public void TiledDepthReadback_PreservesInactiveBacking()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint width = 3, height = 2, pitch = 4, layers = 2;
        const ulong slice = 0x10000;
        const ulong size = slice * layers;
        const uint clear = 0x3f200000u;
        const uint stale = 0xdeadbeefu;
        var address = harness.MapBacked(0x40000, ReadWrite);
        var depthAddress = address + 0x20000;
        harness.Write(depthAddress, Bytes(Enumerable.Repeat(stale, (int)(size / 4)).ToArray()));
        var request = LinearRequest(depthAddress, size, Format.D32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(width, height, 1), layers, 4, 1);
        request = AsDepthTarget(request, Format.D32Sfloat);
        request.Description.Pitch = pitch;
        request.Description.TileMode = GuestTileMode.Depth;
        request.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = pitch, Height = height };
        var imageIdentifier = harness.Find(ref request);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(depthAddress, size, clear)));

        // A nonzero ring offset proves the download uses its mapped offset.
        var download = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Download);
        harness.Worker.Run(() =>
        {
            Assert.True(download.TryMap(64, out var prefixOffset, 64));
            download.Commit();
            Assert.NotEqual(0UL, prefixOffset + 64);
        });
        var tick = harness.Scheduler.CurrentTick;
        Assert.True(harness.Worker.Run(() => harness.Images.TryDownloadForTest(imageIdentifier)));
        Assert.All(harness.Read(depthAddress, (int)size).Chunk(4), chunk => Assert.Equal(stale, BitConverter.ToUInt32(chunk)));
        Assert.Equal(tick, harness.Scheduler.CurrentTick);
        harness.Finish();
        var after = harness.Read(depthAddress, (int)size).Chunk(4).Select(chunk => BitConverter.ToUInt32(chunk)).ToArray();
        var active = (int)(width * height * layers);
        Assert.Equal(active, after.Count(value => value == clear));
        Assert.Equal(after.Length - active, after.Count(value => value == stale));

        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Depth64KB, 4, out var block));
        var tiles = new TileTransfer[layers];
        for (uint layer = 0; layer < layers; layer++)
        {
            tiles[layer] = new TileTransfer
            {
                Kind = block.Kind,
                BytesPerElement = block.BytesPerElement,
                LinearOffset = slice * layer,
                LinearSize = slice,
                TiledOffset = slice * layer,
                TiledSize = slice,
                Width = width,
                Height = height,
                Depth = 1,
                Pitch = pitch,
                SurfaceZ = layer,
            };
        }

        var linear = harness.Worker.Run(() =>
        {
            var input = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, size);
            input.Write(0, Bytes(after));
            var detiled = harness.Images.TilerForTest.Detile(input.Handle, 0, size, size, tiles);
            var bytes = harness.CopyFromDevice(detiled.Buffer, detiled.Offset, size);
            input.Dispose();
            return bytes;
        });
        for (uint layer = 0; layer < layers; layer++)
        {
            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    var index = layer * (slice / 4) + y * pitch + x;
                    Assert.Equal(clear, BitConverter.ToUInt32(linear, (int)index * 4));
                }
            }
        }

        Assert.True(harness.Images.Contains(imageIdentifier));
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        harness.Shutdown();
    }

    [Fact]
    public void TiledD16Readback_ConvertsAndPreservesStencil()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint width = 3, height = 2, pitch = 4, layers = 2;
        const ulong slice = 0x10000;
        const ulong size = slice * layers;
        const ushort clear = 0x8000;
        const ushort stale = 0xbeef;
        var address = harness.MapBacked(0x60000, ReadWrite);
        var depthAddress = address + 0x20000;
        var stencilAddress = address + 0x40000;
        byte[] stencil = [0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98, 0xa9, 0xba, 0xcb, 0xdc, 0xed, 0xfe, 0x0f];
        harness.Write(depthAddress, Bytes(Enumerable.Repeat(stale, (int)(size / 2)).ToArray()));
        harness.Write(stencilAddress, stencil);
        var request = LinearRequest(depthAddress, size, Format.D32SfloatS8Uint, GuestPixelFormat.Bits16UNorm, GuestImageType.Color2D, new Extent3D(width, height, 1), layers, 2, 1);
        request = AsDepthTarget(request, Format.D32SfloatS8Uint);
        request.Description.Pitch = pitch;
        request.Description.TileMode = GuestTileMode.Depth;
        request.Description.Stencil = new GuestSpan(stencilAddress, 16);
        request.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = pitch, Height = height };
        request.View = request.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var imageIdentifier = harness.Find(ref request);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(depthAddress, size, DepthFormatRule.EncodeD16AsD32(clear))));
        Assert.True(harness.Worker.Run(() => harness.Images.TryDownloadForTest(imageIdentifier)));
        harness.Finish();
        var after = harness.Read(depthAddress, (int)size).Chunk(2).Select(chunk => BitConverter.ToUInt16(chunk)).ToArray();
        var active = (int)(width * height * layers);
        Assert.Equal(active, after.Count(value => value == clear));
        Assert.Equal(after.Length - active, after.Count(value => value == stale));
        Assert.Equal(stencil, harness.Read(stencilAddress, 16));

        Assert.True(TileGeometry.TryGetBlockLayout(TileBlockKind.Depth64KB, 2, out var block));
        var tiles = new TileTransfer[layers];
        for (uint layer = 0; layer < layers; layer++)
        {
            tiles[layer] = new TileTransfer
            {
                Kind = block.Kind,
                BytesPerElement = block.BytesPerElement,
                LinearOffset = slice * layer,
                LinearSize = slice,
                TiledOffset = slice * layer,
                TiledSize = slice,
                Width = width,
                Height = height,
                Depth = 1,
                Pitch = pitch,
                SurfaceZ = layer,
            };
        }

        var linear = harness.Worker.Run(() =>
        {
            var input = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.Upload, 0, GpuBuffer.AllFlags, size);
            input.Write(0, Bytes(after));
            var detiled = harness.Images.TilerForTest.Detile(input.Handle, 0, size, size, tiles);
            var bytes = harness.CopyFromDevice(detiled.Buffer, detiled.Offset, size);
            input.Dispose();
            return bytes;
        });
        for (uint layer = 0; layer < layers; layer++)
        {
            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    var index = layer * (slice / 2) + y * pitch + x;
                    Assert.Equal(clear, BitConverter.ToUInt16(linear, (int)index * 2));
                }
            }
        }

        Assert.True(harness.Images.Contains(imageIdentifier));
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        harness.Shutdown();
    }

    [Fact]
    public void DepthStencilPairs_CollectWithinTheDeletionBudget()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        const int pairs = 6;
        var depths = new ResourceSlotIdentifier[pairs];
        var stencils = new ResourceSlotIdentifier[pairs];
        var recency = new ResourceSlotIdentifier[pairs * 2];
        for (var index = 0; index < pairs; index++)
        {
            var depthAddress = address + 0x10000 + (ulong)index * 0x1000;
            var depth = LinearRequest(depthAddress, 4, Format.D32SfloatS8Uint, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
            depth = AsDepthTarget(depth, Format.D32SfloatS8Uint);
            depth.Description.Stencil = new GuestSpan(depthAddress + 0x800, 4);
            depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
            depths[index] = harness.Find(ref depth);
            var local = depth;
            var depthId = depths[index];
            harness.Worker.Run(() => harness.Images.AssociateStencilForTest(depthId, local.Description.Stencil));
            stencils[index] = harness.ProxyAt(depthAddress + 0x800, 4);
            Assert.True(depths[index].IsValid && stencils[index].IsValid);
            recency[index * 2] = depths[index];
            recency[index * 2 + 1] = stencils[index];
        }

        harness.Worker.Run(() =>
        {
            harness.Images.SetCollectionThresholds(0, ulong.MaxValue, ulong.MaxValue, 81);
            harness.Images.ResetRecency(recency, 81);
            harness.Images.RunGarbageCollector();
        });
        for (var index = 0; index < pairs; index++)
        {
            var expectedLive = index == pairs - 1;
            Assert.Equal(expectedLive, harness.Images.Contains(depths[index]));
            Assert.Equal(expectedLive, harness.Images.Contains(stencils[index]));
        }

        harness.Shutdown();
    }

    [Fact]
    public void NearCapacityReadback_ReusesTheSharedDownloadRing()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        const uint width = 4096, height = 2047;
        const ulong size = (ulong)width * height * 4;
        using var harness = new CacheHarness(_vulkan, backingBytes: 40UL * 1024 * 1024);
        var address = harness.MapBacked(size, ReadWrite);
        var request = LinearRequest(address, size, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color2D, new Extent3D(width, height, 1), 1, 4, 1);
        ulong[] samples = [0, size / 2, size - 4];
        var download = harness.Cache.GetUtilityBuffer(GpuBufferUsage.Download);
        foreach (var clearValue in new[] { 0xa5a5a5a5u, 0x5a5a5a5au })
        {
            var imageIdentifier = harness.Find(ref request);
            Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address, size, clearValue)));
            foreach (var offset in samples)
            {
                harness.Write(address + offset, Bytes(0u));
            }

            var tick = harness.Scheduler.CurrentTick;
            harness.Worker.Run(() =>
            {
                harness.Images.SetCollectionThresholds(0, 0, ulong.MaxValue, 81);
                harness.Images.ResetRecency(new[] { imageIdentifier }, 81);
                harness.Images.RunGarbageCollector();
            });
            Assert.False(harness.Images.Contains(imageIdentifier));
            Assert.Equal(tick, harness.Scheduler.CurrentTick);
            harness.Finish();
            foreach (var offset in samples)
            {
                Assert.Equal(clearValue, harness.ReadUInt32(address + offset));
            }

            Assert.Same(download, harness.Cache.GetUtilityBuffer(GpuBufferUsage.Download));
        }

        harness.Shutdown();
    }
}
