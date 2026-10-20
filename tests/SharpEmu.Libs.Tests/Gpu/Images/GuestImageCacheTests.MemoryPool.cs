// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Images share device-memory blocks, and a full device is recovered from by freeing images.
public sealed partial class GuestImageCacheTests
{
    private static ImageDescription SmallImage(ulong address, uint width = 4)
    {
        var size = (ulong)width * width * 4;
        var description = LinearRequest(address, size, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color2D,
            new Extent3D(width, width, 1), 1, 4, 1).Description;
        if (address == 0)
        {
            description.Data = default;
        }

        return description;
    }

    // At least OptimalImageMemoryPool.DedicatedThreshold: needs a device allocation of its own.
    private static ImageDescription LargeImage() => SmallImage(0, 4096);

    [Fact]
    public void ManyImages_ShareFewDeviceAllocations()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var device = _vulkan.DeviceInfo;
        var before = device.LiveAllocations;
        const int Count = 4200;
        harness.Worker.Run(() =>
        {
            for (var index = 0; index < Count; index++)
            {
                harness.Images.InsertImageForTest(SmallImage(0));
            }
        });

        // One allocation per image would pass the 4096 limit of common drivers.
        var added = device.LiveAllocations - before;
        Assert.InRange(added, 1, 64);
        Assert.True(Count - added > 4000);
        harness.Shutdown();
    }

    [Fact]
    public void OutOfDeviceMemory_ReclaimsOldImagesAndRetries()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var device = _vulkan.DeviceInfo;
        var address = harness.MapBacked(0x10000, ReadWrite);
        ResourceSlotIdentifier old1 = default, old2 = default, current = default;
        harness.Worker.Run(() =>
        {
            old1 = harness.Images.InsertImageForTest(SmallImage(address));
            old2 = harness.Images.InsertImageForTest(SmallImage(address + 0x1000));
            harness.Images.SetCollectionThresholds(ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, 5);
            current = harness.Images.InsertImageForTest(SmallImage(address + 0x2000));
        });

        // The device refuses memory until a command buffer was submitted and waited for.
        var armedAt = 0UL;
        var refusals = 0;
        harness.Worker.Run(() =>
        {
            armedAt = harness.Scheduler.CurrentTick;
            device.AllocationFailure = _ =>
            {
                refusals++;
                return harness.Scheduler.CurrentTick == armedAt;
            };
        });
        try
        {
            ResourceSlotIdentifier created = default;
            harness.Worker.Run(() => created = harness.Images.InsertImageForTest(LargeImage()));
            Assert.True(refusals > 0);
            Assert.True(created.IsValid);
            // The images of earlier frames went; the one the current frame touched stayed.
            Assert.False(harness.Images.Contains(old1));
            Assert.False(harness.Images.Contains(old2));
            Assert.True(harness.Images.Contains(current));
            Assert.True(harness.Image(created).Backing.Exists);
        }
        finally
        {
            device.AllocationFailure = null;
        }

        harness.Shutdown();
    }

    [Fact]
    public void OutOfDeviceMemory_AfterReclaimFailsClearlyAndLeaksNothing()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new CacheHarness(_vulkan);
        var device = _vulkan.DeviceInfo;
        harness.Worker.Run(() => harness.Images.InsertImageForTest(SmallImage(0)));
        var held = device.LiveAllocations;
        device.AllocationFailure = _ => true;
        try
        {
            var failure = Assert.Throws<SchedulerFatalException>(() =>
                harness.Worker.Run(() => harness.Images.InsertImageForTest(LargeImage())));
            Assert.Contains("vkAllocateMemory", failure.Message);
            Assert.Contains("ErrorOutOfDeviceMemory", failure.Message);
        }
        finally
        {
            device.AllocationFailure = null;
        }

        // The idle pool memory went back to the device, and nothing of the failed image remains.
        Assert.True(device.LiveAllocations <= held);
        Assert.Equal(1, harness.Images.ImageCount);
        harness.Shutdown();
    }

    [Fact]
    public void BufferOutOfDeviceMemory_ReclaimsAndRetries()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var device = _vulkan.DeviceInfo;
        var address = harness.MapBacked(0x400000, ReadWrite);
        var armedAt = 0UL;
        var refusals = 0;
        harness.Worker.Run(() =>
        {
            armedAt = harness.Scheduler.CurrentTick;
            device.AllocationFailure = _ =>
            {
                refusals++;
                return harness.Scheduler.CurrentTick == armedAt;
            };
        });
        try
        {
            // Larger than a slab block, so the buffer needs an allocation of its own.
            var identifier = harness.Worker.Run(() => harness.Cache.FindBuffer(address, 0x200000));
            Assert.True(refusals > 0);
            Assert.True(identifier.IsValid);
        }
        finally
        {
            device.AllocationFailure = null;
        }

        harness.Shutdown();
    }

    [Fact]
    public void BufferOutOfDeviceMemory_AfterReclaimFailsClearly()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new CacheHarness(_vulkan);
        var device = _vulkan.DeviceInfo;
        var address = harness.MapBacked(0x400000, ReadWrite);
        device.AllocationFailure = _ => true;
        try
        {
            var failure = Assert.Throws<SchedulerFatalException>(() =>
                harness.Worker.Run(() => harness.Cache.FindBuffer(address, 0x200000)));
            Assert.Contains("vkAllocateMemory", failure.Message);
        }
        finally
        {
            device.AllocationFailure = null;
        }

        harness.Shutdown();
    }

    [Fact]
    public void RetiredImagesStayCountedUntilTheirWorkCompletes()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        ResourceSlotIdentifier image = default;
        harness.Worker.Run(() => image = harness.Images.InsertImageForTest(SmallImage(address)));
        var accounted = harness.Image(image).AccountedSize;
        Assert.NotEqual(0UL, accounted);
        Assert.Equal(accounted, harness.Images.TotalUsedMemory);

        harness.Worker.Run(() => harness.Images.DeleteImageForTest(image));
        // Unregistered, but the device memory is held until the GPU is done with the tick.
        Assert.False(harness.Images.Contains(image));
        Assert.Equal(accounted, harness.Images.TotalUsedMemory);
        Assert.False(harness.Images.RetirementOverBudget);

        harness.Finish();
        Assert.Equal(0UL, harness.Images.TotalUsedMemory);
        harness.Shutdown();
    }

    [Fact]
    public void RetirementOverBudget_WaitsForTheOldTicksOnly()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        ResourceSlotIdentifier older = default;
        ResourceSlotIdentifier newer = default;
        harness.Worker.Run(() =>
        {
            older = harness.Images.InsertImageForTest(SmallImage(address, 64));
            newer = harness.Images.InsertImageForTest(SmallImage(address + 0x10000, 64));
        });
        var olderBytes = harness.Image(older).AccountedSize;
        var newerBytes = harness.Image(newer).AccountedSize;
        harness.Worker.Run(() =>
        {
            harness.Images.DeleteImageForTest(older);
            harness.Scheduler.Flush();
            harness.Images.DeleteImageForTest(newer);
        });

        // Room for the newer retirement only: the older tick must complete, the recording one need not.
        harness.Images.SetCollectionThresholds(1, newerBytes + 1, newerBytes + 1, 10);
        Assert.True(harness.Images.RetirementOverBudget);
        var recording = harness.Scheduler.CurrentTick;
        harness.Worker.Run(harness.Images.ReleaseRetiredImages);

        Assert.Equal(recording, harness.Scheduler.CurrentTick);
        Assert.Equal(newerBytes, harness.Images.TotalUsedMemory);
        Assert.False(harness.Images.RetirementOverBudget);

        // When the recording tick holds the excess, it is submitted and waited for.
        harness.Images.SetCollectionThresholds(1, 1, 1, 10);
        harness.Worker.Run(harness.Images.ReleaseRetiredImages);
        Assert.NotEqual(recording, harness.Scheduler.CurrentTick);
        Assert.Equal(0UL, harness.Images.TotalUsedMemory);
        Assert.NotEqual(0UL, olderBytes);
        harness.Shutdown();
    }

    [Fact]
    public void AllocationNearTheBudgetRetiresOldImagesFirst()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint Width = 1024;
        const ulong Size = (ulong)Width * Width * 4;
        var address = harness.MapBacked(6 * Size, ReadWrite);
        var old = new List<ResourceSlotIdentifier>();
        harness.Worker.Run(() =>
        {
            for (var index = 0u; index < 4; index++)
            {
                old.Add(harness.Images.InsertImageForTest(SmallImage(address + index * Size, Width)));
            }
        });

        var current = harness.Images.CollectionMemoryBytes;
        // Every image so far is from an earlier frame; the budget has room for one more megabyte.
        harness.Images.SetCollectionThresholds(1, current - 1, current + (1UL << 20), 10);
        ResourceSlotIdentifier created = default;
        harness.Worker.Run(() => created = harness.Images.InsertImageForTest(SmallImage(address + 4 * Size, Width)));

        Assert.True(old.Count(identifier => !harness.Images.Contains(identifier)) >= 1);
        Assert.True(harness.Images.Contains(created));
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void FreePoolSpaceAndRetiredImagesAreNotPressure()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint Width = 1024;
        const ulong Size = (ulong)Width * Width * 4;
        var address = harness.MapBacked(4 * Size, ReadWrite);
        var images = new List<ResourceSlotIdentifier>();
        harness.Worker.Run(() =>
        {
            for (var index = 0u; index < 4; index++)
            {
                images.Add(harness.Images.InsertImageForTest(SmallImage(address + index * Size, Width)));
            }
        });

        // The pool holds a 256 MiB block for 16 MiB of images; the images used last frame fit the
        // 100 MiB threshold and must all survive the collection.
        Assert.True(harness.Images.TotalUsedMemory < (50UL << 20));
        harness.Images.SetCollectionThresholds(1, 100UL << 20, 100UL << 20, 40);
        harness.Worker.Run(() =>
        {
            harness.Images.ResetRecency(Array.Empty<ResourceSlotIdentifier>(), 39);
            harness.Images.RunGarbageCollector();
        });
        Assert.All(images, identifier => Assert.True(harness.Images.Contains(identifier)));
        harness.Finish();
        harness.Shutdown();
    }

    [Fact]
    public void PressureEvictsTheOldestImagesOnlyDownToTheThreshold()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint Width = 1024;
        const ulong Size = (ulong)Width * Width * 4;
        var address = harness.MapBacked(4 * Size, ReadWrite);
        var images = new List<ResourceSlotIdentifier>();
        harness.Worker.Run(() =>
        {
            for (var index = 0u; index < 4; index++)
            {
                images.Add(harness.Images.InsertImageForTest(SmallImage(address + index * Size, Width)));
            }
        });

        var used = harness.Images.CollectionMemoryBytes;
        var each = used / 4;
        // One image over the threshold.
        harness.Images.SetCollectionThresholds(1, used - each / 2, ulong.MaxValue, 40);
        harness.Worker.Run(() =>
        {
            harness.Images.ResetRecency(new[] { images[2] }, 39);
            harness.Images.RunGarbageCollector();
        });
        Assert.False(harness.Images.Contains(images[2]));
        Assert.Equal(3, images.Count(identifier => harness.Images.Contains(identifier)));
        harness.Finish();
        harness.Shutdown();
    }

    // Collection also runs between the submissions of a frame. Under pressure the images the
    // previous frame used are still the ones the rest of this frame samples: deleting them there
    // recreated and uploaded them again before the flip. Only the critical level retires them.
    [Fact]
    public void MidFramePressureKeepsThePreviousFramesImages()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint Width = 1024;
        const ulong Size = (ulong)Width * Width * 4;
        var address = harness.MapBacked(4 * Size, ReadWrite);
        var images = new List<ResourceSlotIdentifier>();
        harness.Worker.Run(() =>
        {
            for (var index = 0u; index < 4; index++)
            {
                images.Add(harness.Images.InsertImageForTest(SmallImage(address + index * Size, Width)));
            }
        });

        var used = harness.Images.CollectionMemoryBytes;
        var threshold = used - used / 8;
        harness.Images.SetCollectionThresholds(1, threshold, ulong.MaxValue, 40);
        harness.Worker.Run(() =>
        {
            // Every image was last used by the previous frame.
            harness.Images.ResetRecency(Array.Empty<ResourceSlotIdentifier>(), 39);
            for (var submission = 0; submission < 8; submission++)
            {
                harness.Images.RunGarbageCollector(endsFrame: false);
            }
        });
        Assert.All(images, identifier => Assert.True(harness.Images.Contains(identifier)));

        // Past the critical level one of them goes even mid-frame.
        harness.Images.SetCollectionThresholds(1, threshold, threshold, 40);
        harness.Worker.Run(() => harness.Images.RunGarbageCollector(endsFrame: false));
        Assert.Equal(3, images.Count(identifier => harness.Images.Contains(identifier)));
        harness.Finish();
        harness.Shutdown();
    }
}
