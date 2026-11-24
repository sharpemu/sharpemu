// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// A slice segment whose join applies a depth view's pending HTILE clear itself.
public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void SliceJoin_ADepthUseWithAClearEntryPassesAPendingClear()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        var depth = AsDepthTarget(LinearRequest(address + 0x8000, 4, Format.D32Sfloat,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1), Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadataAddress, 0x100);
        var identifier = harness.Acquire(ref depth);
        Assert.True(harness.Images.SetMetadataSlice(metadataAddress, 0, false));
        List<ImageSliceUse> uses = [new ImageSliceUse(identifier, harness.Image(identifier), depth)];
        var lookup = harness.Images.LookupGeneration;
        var metadata = harness.Images.MetadataGeneration;
        Assert.True(harness.Images.ClearMetadata(metadataAddress));

        // The clear still waits on the view's slice: only an entry for the same view lets the use pass.
        Assert.False(harness.Images.ValidateSliceImages(uses, lookup, metadata));
        Assert.True(harness.Images.ValidateSliceImages(uses, lookup, metadata, [(identifier, depth.View)]));
        Assert.False(harness.Images.ValidateSliceImages(uses, lookup, metadata, [(identifier, depth.View with { BaseLayer = 1 })]));

        // An event other than a clear-state change fails the use with an entry too.
        harness.ImageStore.MarkCpuWrite(metadataAddress + 0x10, 8);
        Assert.False(harness.Images.ValidateSliceImages(uses, lookup, metadata, [(identifier, depth.View)]));
        harness.Shutdown();
    }
}
