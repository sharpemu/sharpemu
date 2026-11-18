// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    // A heap record can name a texture the guest has unloaded (a table it no longer reads keeps the T#): the heap asks
    // whether the texels could be read before it finds the record's image, since an image of unmapped memory cannot
    // be populated. A streamed texture maps only a prefix of its range, which is enough.
    [Fact]
    public void AnImageRangeIsBackedWhenItsFirstPageIsMapped()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(65536, ReadWrite);

        Assert.True(harness.Images.IsBackedImageRange(new SharpEmu.HLE.GpuMemory.GuestSpan(address, 4096)));
        Assert.True(harness.Images.IsBackedImageRange(new SharpEmu.HLE.GpuMemory.GuestSpan(address, 4 * 65536)));
        Assert.False(harness.Images.IsBackedImageRange(new SharpEmu.HLE.GpuMemory.GuestSpan(address + 16 * 65536, 4096)));
        Assert.False(harness.Images.IsBackedImageRange(new SharpEmu.HLE.GpuMemory.GuestSpan(0, 0)));
        Assert.False(harness.Images.IsBackedImageRange(new SharpEmu.HLE.GpuMemory.GuestSpan(0x0000_2045_2881_9A00, 0x1000)));
        harness.Shutdown();
    }

    // An image deleted while the bindless heap names its view keeps its memory through completed ticks and is freed
    // after the tick in which the heap lets go of it; an image the heap does not name is freed after its tick.
    [Fact]
    public void HeapHeldImage_IsFreedOnlyAfterTheHeapLetsGo()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(4 * 65536, ReadWrite);
        var heldRequest = GrowthRequest(address, 1, GuestTileMode.Linear);
        var plainRequest = GrowthRequest(address + 2 * 65536, 1, GuestTileMode.Linear);
        var heldId = harness.Acquire(ref heldRequest);
        var plainId = harness.Acquire(ref plainRequest);
        var held = harness.Image(heldId);
        var plain = harness.Image(plainId);
        var changes = 0;
        held.HeapChanged = _ => changes++;

        harness.Worker.Run(() =>
        {
            harness.Images.DeleteImageForTest(heldId);
            harness.Images.DeleteImageForTest(plainId);
        });
        harness.Finish();

        Assert.Equal(1, changes);
        Assert.False(held.Registered);
        Assert.True(held.Backing.Exists);
        Assert.NotNull(held.DeferredErase);
        Assert.False(plain.Backing.Exists);
        Assert.Null(plain.DeferredErase);

        // What the heap does when it rewrites the image's last element.
        held.HeapChanged = null;
        var erase = held.DeferredErase!;
        held.DeferredErase = null;
        harness.Worker.Run(erase);
        Assert.True(held.Backing.Exists);
        harness.Finish();
        Assert.False(held.Backing.Exists);
        _vulkan.AssertNoValidationMessages();
    }
}
