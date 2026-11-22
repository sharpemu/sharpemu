// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// A depth surface the guest clears through its HTile and then samples before binding it as a depth target again.
public sealed partial class GuestImageCacheTests
{
    private static ImageRequest HtileDepth(ulong address, ulong metadataAddress, uint layers)
    {
        var depth = AsDepthTarget(LinearRequest(address, 16 * 4 * layers, Format.D32Sfloat, GuestPixelFormat.Bits32Float,
            GuestImageType.Color2D, new Extent3D(4, 4, 1), layers, 4, 1), Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadataAddress, 0x100);
        return depth;
    }

    private static ImageRequest SampledDepth(ImageRequest depth)
    {
        depth.Role = ImageRole.Texture;
        depth.Description.Metadata = default;
        depth.View = depth.View with { Usage = ImageUsageFlags.SampledBit };
        return depth;
    }

    [Fact]
    public void SampledDepth_TakesThePendingHtileClearWithTheValueOfItsLastBind()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        var depth = HtileDepth(address, metadataAddress, 1);
        var depthId = harness.Acquire(ref depth);
        harness.Images.NoteDepthClearValue(metadataAddress, 0.25f);
        Assert.True(harness.Images.ClearMetadata(metadataAddress));

        var sampled = SampledDepth(depth);
        Assert.Equal(depthId, harness.Find(ref sampled));
        // The render thread records the clear: a recording worker leaves such a sample to it.
        Assert.False(harness.Images.TryPrepareTextureViewForSlice(depthId, sampled, out _));
        Assert.Equal(depthId, harness.Acquire(ref sampled));

        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
        Assert.True(harness.Images.TryPrepareTextureViewForSlice(depthId, sampled, out _));
        Assert.Equal(Bytes(Enumerable.Repeat(0.25f, 16).ToArray()), harness.ReadImageBytes(harness.Image(depthId), ImageAspectFlags.DepthBit));
        harness.Shutdown();
    }

    // A layered surface's clear bits are not exact per slice: its pending clear stays for its next depth-target bind.
    [Fact]
    public void SampledDepth_LeavesALayeredSurfacesPendingClearToItsNextBind()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        var depth = HtileDepth(address, metadataAddress, 2);
        var depthId = harness.Acquire(ref depth);
        harness.Images.NoteDepthClearValue(metadataAddress, 0.25f);
        Assert.True(harness.Images.ClearMetadata(metadataAddress));

        var sampled = SampledDepth(depth);
        Assert.Equal(depthId, harness.Acquire(ref sampled));

        Assert.True(harness.Images.IsMetadataCleared(metadataAddress, 0));
        harness.Shutdown();
    }
}
