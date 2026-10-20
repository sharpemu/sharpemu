// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// A frame samples again what the previous frame did. Allocation-time collection above the
// critical target must not evict that working set unless the cache reaches its hard limit.
public sealed partial class GuestImageCacheTests
{
    private const uint WorkingSetWidth = 1024;
    private const ulong WorkingSetImageBytes = (ulong)WorkingSetWidth * WorkingSetWidth * 4;

    [Fact]
    public void AllocationAboveTheTargetKeepsThePreviousFramesImages()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(6 * WorkingSetImageBytes, ReadWrite);
        var previousFrame = new List<ResourceSlotIdentifier>();
        harness.Worker.Run(() =>
        {
            for (var index = 0u; index < 4; index++)
            {
                previousFrame.Add(harness.Images.InsertImageForTest(SmallImage(address + index * WorkingSetImageBytes, WorkingSetWidth)));
            }
        });

        var used = harness.Images.CollectionMemoryBytes;
        // The next image crosses the critical target but stays under the hard limit.
        harness.Images.SetCollectionThresholds(1, used - 1, used + (1UL << 20), 40, hardLimit: used + 8 * WorkingSetImageBytes);
        ResourceSlotIdentifier created = default;
        harness.Worker.Run(() =>
        {
            harness.Images.ResetRecency(Array.Empty<ResourceSlotIdentifier>(), 39);
            created = harness.Images.InsertImageForTest(SmallImage(address + 4 * WorkingSetImageBytes, WorkingSetWidth));
        });

        Assert.All(previousFrame, identifier => Assert.True(harness.Images.Contains(identifier)));
        Assert.True(harness.Images.Contains(created));
        Assert.False(harness.Images.RetirementOverBudget);

        // At the hard limit the previous frame's images are taken back as before.
        harness.Images.SetCollectionThresholds(1, used - 1, used + (1UL << 20), 40, hardLimit: used + (1UL << 20));
        harness.Worker.Run(() => harness.Images.InsertImageForTest(SmallImage(address + 5 * WorkingSetImageBytes, WorkingSetWidth)));
        Assert.True(previousFrame.Count(identifier => !harness.Images.Contains(identifier)) >= 1);
        harness.Finish();
        harness.Shutdown();
    }

    // Evicting a GPU-written image reads it back first, which waits for the GPU; an image guest
    // memory already holds costs nothing to drop. The older GPU-written images stay.
    [Fact]
    public void AllocationEvictsImagesWithoutGpuContentsFirst()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(6 * WorkingSetImageBytes, ReadWrite);
        var images = new List<ResourceSlotIdentifier>();
        harness.Worker.Run(() =>
        {
            for (var index = 0u; index < 4; index++)
            {
                images.Add(harness.Images.InsertImageForTest(SmallImage(address + index * WorkingSetImageBytes, WorkingSetWidth)));
            }
        });
        harness.MarkGpuWritten(images[0]);
        harness.MarkGpuWritten(images[1]);

        var used = harness.Images.CollectionMemoryBytes;
        // One eviction makes room: back under the pressure threshold, the pass stops.
        harness.Images.SetCollectionThresholds(1, used - 1, used, 40);
        harness.Worker.Run(() =>
        {
            harness.Images.ResetRecency(images.ToArray(), 0);
            harness.Images.InsertImageForTest(SmallImage(address + 4 * WorkingSetImageBytes, WorkingSetWidth));
        });

        Assert.True(harness.Images.Contains(images[0]));
        Assert.True(harness.Images.Contains(images[1]));
        Assert.Equal(1, images.Skip(2).Count(identifier => !harness.Images.Contains(identifier)));
        harness.Finish();
        harness.Shutdown();
    }
}
