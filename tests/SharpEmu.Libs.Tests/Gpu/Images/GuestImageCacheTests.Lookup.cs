// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed partial class GuestImageCacheTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("00000001,00000002")]
    public void FindImage_ReusesASameBackingLookupUntilTheIndexChanges(string? descriptor)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Write(address, Bytes(0x44332211u));

        var first = Sampled(address);
        first.TraceTextureDescriptor = descriptor;
        var firstId = harness.Find(ref first);
        var created = Sampled(address);
        created.TraceTextureDescriptor = descriptor;
        Assert.Equal(firstId, harness.Find(ref created));
        var baseline = harness.Images.LookupMemoCounters;

        var repeated = Sampled(address);
        repeated.TraceTextureDescriptor = descriptor is null ? null : new string(descriptor.ToCharArray());
        Assert.Equal(firstId, harness.Find(ref repeated));
        Assert.Equal(baseline.Hits + 1, harness.Images.LookupMemoCounters.Hits);
        Assert.Equal(first.View, repeated.View);

        var stale = new ResourceSlotIdentifier(firstId.Index, firstId.Generation + 1);
        harness.Images.AddPageOwner(address, stale);
        Assert.True(harness.Images.RemovePageOwner(address, stale));
        var afterChange = Sampled(address);
        afterChange.TraceTextureDescriptor = descriptor;
        Assert.Equal(firstId, harness.Find(ref afterChange));
        Assert.Equal(baseline.Hits + 1, harness.Images.LookupMemoCounters.Hits);

        var reusedAgain = Sampled(address);
        reusedAgain.TraceTextureDescriptor = descriptor;
        Assert.Equal(firstId, harness.Find(ref reusedAgain));
        Assert.Equal(baseline.Hits + 2, harness.Images.LookupMemoCounters.Hits);
    }

    [Fact]
    public void FindImage_ADifferentViewIsNotReused()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Write(address, Bytes(0x44332211u));

        var first = Sampled(address);
        var firstId = harness.Find(ref first);
        var again = Sampled(address);
        harness.Find(ref again);
        var baseline = harness.Images.LookupMemoCounters;

        var swizzled = Sampled(address);
        swizzled.View = swizzled.View with { Mapping = new ComponentMapping(ComponentSwizzle.B, ComponentSwizzle.G, ComponentSwizzle.R, ComponentSwizzle.A) };
        Assert.Equal(firstId, harness.Find(ref swizzled));
        Assert.Equal(ComponentSwizzle.B, swizzled.View.Mapping.R);
        Assert.Equal(baseline.Hits, harness.Images.LookupMemoCounters.Hits);
    }

    // A title submits dozens of command buffers per frame. An image it writes early in a frame and reads late in it
    // is past the removal window of submissions in between, and an unrelated image the frame requested inside its
    // memory deleted it with its contents: its next use uploaded stale guest memory. An image used this frame stays;
    // after the frame ends, the overlapping request parks it with its contents.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindImage_AnUnrelatedImageInsideAnImageUsedThisFrameKeepsItsContents(bool frameEnded)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint width = 128;
        const uint deviceValue = 0x11223344;
        var address = harness.MapBacked(0x20000, ReadWrite);
        var written = LinearRequest(address, width * width * 4, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D,
            new Extent3D(width, width, 1), 1, 4, 1);
        var writtenId = harness.Acquire(ref written);
        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address, width * width * 4, deviceValue)));
        AgeBeyondTheRemovalWindow(harness, writtenId, endFrame: frameEnded);

        var inside = LinearRequest(address + 0x8000, 0x4000, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D,
            new Extent3D(64, 64, 1), 1, 4, 1);
        harness.Acquire(ref inside);

        Assert.True(harness.Images.Contains(writtenId));
        Assert.Equal(frameEnded, harness.Image(writtenId).Parked);
        Assert.True(harness.Image(writtenId).IsGpuModified);
        Assert.Equal(Bytes(Enumerable.Repeat(deviceValue, (int)(width * width)).ToArray()), harness.ReadImageBytes(harness.Image(writtenId)));

        harness.Shutdown();
    }

    [Fact]
    public void FindImage_AnImageFilledFromGuestMemoryIsParkedAndReusedWithTheNewerBytes()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint width = 128;
        var address = harness.MapBacked(0x20000, ReadWrite);
        harness.Write(address, Bytes(Enumerable.Repeat(0x11111111u, (int)(width * width)).ToArray()));
        var filled = LinearRequest(address, width * width * 4, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D,
            new Extent3D(width, width, 1), 1, 4, 1);
        var filledId = harness.Acquire(ref filled);
        Assert.False(harness.Image(filledId).IsGpuModified);
        AgeBeyondTheRemovalWindow(harness, filledId, endFrame: true);

        var inside = LinearRequest(address + 0x8000, 0x4000, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D,
            new Extent3D(64, 64, 1), 1, 4, 1);
        harness.Acquire(ref inside);
        Assert.True(harness.Image(filledId).Parked);

        harness.Write(address, Bytes(Enumerable.Repeat(0x22222222u, (int)(width * width)).ToArray()));
        harness.Images.InvalidateMemory(address, width * width * 4);
        var again = LinearRequest(address, width * width * 4, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D,
            new Extent3D(width, width, 1), 1, 4, 1);
        Assert.Equal(filledId, harness.Acquire(ref again));
        Assert.False(harness.Image(filledId).Parked);
        Assert.Equal(Bytes(Enumerable.Repeat(0x22222222u, (int)(width * width)).ToArray()), harness.ReadImageBytes(harness.Image(filledId)));

        harness.Shutdown();
    }

    [Fact]
    public void TryClearImageFromBuffer_AClearOfOneLayerClearsOnlyThatLayer()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint width = 64;
        const uint layers = 4;
        const ulong layerSize = width * width * 4;
        var address = harness.MapBacked(layerSize * layers, ReadWrite);
        harness.Write(address, Bytes(Enumerable.Repeat(0x11111111u, (int)(width * width * layers)).ToArray()));
        var request = LinearRequest(address, layerSize * layers, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D,
            new Extent3D(width, width, 1), layers, 4, 1);
        var imageId = harness.Acquire(ref request);

        Assert.True(harness.Worker.Run(() => harness.Images.TryClearImageFromBuffer(address + layerSize * 2, layerSize, 0x22222222u)));

        var expected = Enumerable.Repeat(0x11111111u, (int)(width * width * layers)).ToArray();
        Array.Fill(expected, 0x22222222u, (int)(width * width * 2), (int)(width * width));
        Assert.True(harness.Image(imageId).IsGpuModified);
        Assert.Equal(Bytes(expected), harness.ReadImageBytes(harness.Image(imageId)));

        harness.Shutdown();
    }
}
