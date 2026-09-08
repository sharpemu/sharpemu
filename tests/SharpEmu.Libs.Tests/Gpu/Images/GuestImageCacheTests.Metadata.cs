// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// Surface metadata registration, the pending DCC state and compressed surfaces.
public sealed partial class GuestImageCacheTests
{
    [Fact]
    public void HtileEntries_ReportRegisteredAndClearedState()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        ulong[] entries = [address + 0x1f00, address + 0x2000, address + 0x2100];
        foreach (var entry in entries)
        {
            harness.Images.RegisterHtileMetadataForTest(entry);
            Assert.True(harness.Images.IsMetadata(entry));
            Assert.False(harness.Images.IsMetadataCleared(entry, 0));
        }

        Assert.True(harness.Images.ClearMetadata(entries[2]));
        Assert.True(harness.Images.IsMetadataCleared(entries[2], 0, out var fill));
        Assert.Equal(0xffffffffu, fill);
        Assert.False(harness.Images.IsMetadataCleared(entries[0], 0));
        Assert.False(harness.Images.IsMetadataCleared(entries[2], 32));
        Assert.False(harness.Images.SetMetadataSlice(entries[2], 32, true));
        Assert.False(harness.Images.IsMetadata(address + 0x3000));
        Assert.False(harness.Images.ClearMetadata(address + 0x3000));
        harness.Shutdown();
    }

    [Fact]
    public void PendingDccAndHtile_StayApart()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataA = address + 0x13000;
        var metadataB = address + 0x13100;

        ImageRequest MetadataDepth(ulong data, ulong metadata)
        {
            var request = LinearRequest(data, 4, Format.D32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
            request = AsDepthTarget(request, Format.D32Sfloat);
            request.Description.Metadata.Kind = MetadataKind.Htile;
            request.Description.Metadata.Range = new GuestSpan(metadata, 0x80);
            return request;
        }

        var depthA = MetadataDepth(address + 0x12000, metadataA);
        var depthB = MetadataDepth(address + 0x12100, metadataB);
        depthA.Description.HtileClearMask = 0;
        var depthAId = harness.Find(ref depthA);
        var depthBId = harness.Find(ref depthB);
        Assert.False(harness.Images.TryAbsorbDccFill(metadataA, 0x80, 0));
        Assert.False(harness.Images.IsMetadata(metadataA));

        var localA = depthA;
        var localB = depthB;
        harness.Worker.Run(() =>
        {
            Assert.NotEqual(0UL, harness.Images.AcquireDepthTargetView(depthAId, localA).Handle);
            Assert.NotEqual(0UL, harness.Images.AcquireDepthTargetView(depthBId, localB).Handle);
        });
        Assert.False(harness.Images.IsMetadataCleared(metadataA, 0));
        Assert.True(harness.Images.ClearMetadata(metadataA));
        Assert.True(harness.Images.ClearMetadata(metadataB));

        var alias = LinearRequest(metadataA, 4, Format.R32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        alias = AsColorTarget(alias);
        var aliasId = harness.Acquire(ref alias, exactFormat: true);
        Assert.True(aliasId.IsValid);
        Assert.True(harness.Image(depthAId).IsGpuModified);
        Assert.True(harness.Image(depthBId).IsGpuModified);
        Assert.True(harness.Images.IsMetadata(metadataA));
        Assert.True(harness.Images.IsMetadataCleared(metadataA, 0));
        Assert.True(harness.Images.IsMetadata(metadataB));
        Assert.True(harness.Images.IsMetadataCleared(metadataB, 0));

        Assert.True(harness.Images.SetMetadataSlice(metadataA, 0, false));
        Assert.False(harness.Images.TryAbsorbDccFill(metadataA, 0x80, 0));
        Assert.False(harness.Images.IsMetadataCleared(metadataA, 0));
        Assert.True(harness.Images.SetMetadataSlice(metadataB, 0, true));
        Assert.True(harness.Images.IsMetadataCleared(metadataB, 0));
        Assert.True(harness.Images.SetMetadataSlice(metadataB, 0, false));
        Assert.False(harness.Images.IsMetadataCleared(metadataB, 0));
        harness.Worker.Run(() => harness.Cache.FillBuffer(metadataB, 4, 0, isGds: false));
        Assert.True(harness.Images.IsMetadataCleared(metadataB, 0));
        harness.Shutdown();
    }

    [Fact]
    public void DccFills_AreAbsorbedOnlyByRegisteredDcc()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadata = address + 0x10000;
        Assert.False(harness.Images.TryAbsorbDccFill(metadata, 0x100, 0x20202020u));
        Assert.False(harness.Images.IsMetadata(metadata));
        Assert.False(harness.Images.TryAbsorbDccFill(metadata, 0x100, 0x40404040u));

        harness.Write(address + 0xc000, Bytes(0x55667788u));
        var target = LinearRequest(address + 0xc000, 4, Format.R8G8B8A8Srgb, GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        target = AsColorTarget(target);
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        target.Description.Metadata.Range = new GuestSpan(metadata, 0x100);
        target.Description.Metadata.Compression = DisplayCompression.Dcc256_256_0;
        var targetId = harness.Acquire(ref target);
        Assert.True(harness.Images.IsMetadata(metadata));
        Assert.True(harness.Images.IsMetadataCleared(metadata, 0, out var fill));
        Assert.Equal(0x40404040u, fill);
        Assert.True(harness.Images.TryAbsorbDccFill(metadata, 0x100, 0x01010101u));
        Assert.False(harness.Images.IsMetadataCleared(metadata, 0));
        Assert.True(harness.Images.TryAbsorbDccFill(metadata, 0x100, 0xc0c0c0c0u));
        Assert.True(harness.Images.IsMetadataCleared(metadata, 5));
        Assert.False(harness.Images.ClearMetadata(metadata));

        // A compressed target rejects readback and read claims but keeps GPU ownership.
        Assert.Equal(0x55667788u, harness.ReadUInt32(address + 0xc000));
        Assert.True(harness.Image(targetId).IsGpuModified);
        Assert.False(harness.Worker.Run(() => harness.Images.TryDownloadForTest(targetId)));
        Assert.True(harness.Image(targetId).IsGpuModified);
        Assert.False(harness.Image(targetId).IsBufferModified);
        var video = target;
        video.Role = ImageRole.DisplaySurface;
        video.View = video.View with { Usage = ImageUsageFlags.SampledBit };
        var videoId = harness.Find(ref video);
        var videoImage = harness.Image(videoId);
        Assert.Equal(targetId, videoId);
        Assert.False(videoImage.Binding.IsBound);
        Assert.False(videoImage.Binding.IsTarget);
        Assert.False(videoImage.Binding.NeedsRebind);
        Assert.False(videoImage.Uses.VideoOut);

        // A depth target must not reuse DCC metadata.
        var depth = LinearRequest(address + 0xe000, 4, Format.D32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        depth = AsDepthTarget(depth, Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadata, 0x100);
        var depthId = harness.Find(ref depth);
        var depthLocal = depth;
        harness.Worker.Run(() => Assert.Throws<SchedulerFatalException>(() => harness.Images.AcquireDepthTargetView(depthId, depthLocal)));
        Assert.Contains(fatal.Messages, message => message.Contains("depth target reuses metadata that is not HTile"));
        harness.Shutdown();
    }

    [Fact]
    public void Unregister_DropsMetadataInTheRange()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Images.RegisterHtileMetadataForTest(address + 0x1000);
        harness.Images.RegisterHtileMetadataForTest(address + 0x3000);
        harness.Worker.Run(() => harness.ImageStore.Unregister(address, 0x2000));
        Assert.False(harness.Images.IsMetadata(address + 0x1000));
        Assert.True(harness.Images.IsMetadata(address + 0x3000));
        harness.Shutdown();
    }
}
