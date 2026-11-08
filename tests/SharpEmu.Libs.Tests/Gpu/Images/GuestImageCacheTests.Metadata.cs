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
    public void MetadataLookBackFindsLongRangesAcrossLaterEntriesAndGrowth()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x200000, ReadWrite);
        var metadata = address + 0x10000;
        harness.Images.RegisterDccMetadataForTest(metadata, 0x10000);
        for (ulong offset = 0x100; offset < 0x10000; offset += 0x100)
            harness.Images.RegisterHtileMetadataForTest(metadata + offset);
        Assert.True(harness.Images.OverlapsDccMetadata(metadata + 0xffff, 1));
        Assert.False(harness.Images.OverlapsDccMetadata(metadata + 0x10000, 1));
        harness.ImageStore.MarkCpuWrite(metadata + 0xfff8, 8);
        Assert.False(harness.Images.IsMetadata(metadata));
        Assert.True(harness.Images.IsMetadata(metadata + 0xff00));
        harness.Images.RegisterDccMetadataForTest(metadata, 0x20000);
        Assert.True(harness.Images.OverlapsDccMetadata(metadata + 0x1ffff, 1));
        harness.ImageStore.MarkCpuWrite(metadata + 0x1ffff, 1);
        Assert.False(harness.Images.IsMetadata(metadata));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(0x10000ul)]
    [InlineData(0x200000ul)]
    public void CpuWrite_InvalidatesComputedDccRange(ulong metadataOffset)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(metadataOffset + 0x10000, ReadWrite);
        var metadataAddress = address + metadataOffset;
        var color = AsColorTarget(LinearRequest(address + 0x4000, 4, Format.R8G8B8A8Srgb,
            GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1));
        color.Description.Metadata.Kind = MetadataKind.Dcc;
        color.Description.Metadata.Range = new GuestSpan(metadataAddress, 0);
        color.Description.TileMode = GuestTileMode.RenderTarget;
        var metadataSize = color.Description.DccSliceSize * color.Description.TransferLayers;
        Assert.True(metadataSize > 8);
        harness.Images.RegisterDccMetadataForTest(metadataAddress, metadataSize);
        Assert.True(harness.Images.IsMetadata(metadataAddress));
        harness.ImageStore.MarkCpuWrite(metadataAddress + metadataSize, 8);
        Assert.True(harness.Images.IsMetadata(metadataAddress));
        harness.ImageStore.MarkCpuWrite(metadataAddress + metadataSize / 2, 8);
        Assert.False(harness.Images.IsMetadata(metadataAddress));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(0x10000ul)]
    [InlineData(0x200000ul)]
    public void CpuWrite_InvalidatesMetadataWithoutDeletingItsImageOrReplacement(ulong metadataOffset)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(metadataOffset + 0x10000, ReadWrite);
        var metadataAddress = address + metadataOffset;
        var depth = LinearRequest(address + 0x4000, 4, Format.D32Sfloat,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        depth = AsDepthTarget(depth, Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadataAddress, 0x100);
        var depthIdentifier = harness.Acquire(ref depth);
        Assert.True(harness.Images.ClearMetadata(metadataAddress));

        harness.ImageStore.MarkCpuWrite(metadataAddress + 0x100, 8);
        Assert.True(harness.Images.IsMetadataCleared(metadataAddress, 0));
        harness.ImageStore.MarkCpuWrite(metadataAddress + 0x80, 8);
        Assert.False(harness.Images.IsMetadata(metadataAddress));
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
        Assert.True(harness.Image(depthIdentifier).Registered);
        Assert.True(harness.Image(depthIdentifier).IsGpuModified);

        harness.Worker.Run(() => harness.Images.AcquireDepthTargetView(depthIdentifier, depth));
        Assert.True(harness.Images.IsMetadata(metadataAddress));
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
        harness.ImageStore.MarkCpuWrite(metadataAddress + 0x80, 8);

        var color = LinearRequest(address + 0x8000, 4, Format.R8G8B8A8Srgb,
            GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        color = AsColorTarget(color);
        color.Description.Metadata.Kind = MetadataKind.Dcc;
        // A zero range size selects deferred metadata registration.
        color.Description.Metadata.Range = new GuestSpan(metadataAddress, 0);
        color.Description.Metadata.Compression = DisplayCompression.Dcc256_256_0;
        harness.Acquire(ref color);
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
        Assert.False(harness.Images.ClearMetadata(metadataAddress));

        harness.Worker.Run(() =>
        {
            harness.Image(depthIdentifier).ClearGpuModified();
            harness.Images.DeleteImageForTest(depthIdentifier);
        });
        Assert.True(harness.Images.IsMetadata(metadataAddress));
        Assert.True(harness.Images.TryAbsorbDccFill(metadataAddress, 0x100, 0xc0c0c0c0u));
        harness.Shutdown();
    }

    // A depth target whose HTile lands where a color target kept its DCC ends that use: the HTile takes the
    // address, and deleting the color target later leaves it.
    [Fact]
    public void DepthTarget_ReplacesTheDccOfAnotherSurfaceAtItsHtileAddress()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        var color = AsColorTarget(LinearRequest(address + 0x4000, 4, Format.R8G8B8A8Srgb,
            GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1));
        color.Description.Metadata.Kind = MetadataKind.Dcc;
        // A zero range size selects deferred metadata registration.
        color.Description.Metadata.Range = new GuestSpan(metadataAddress, 0);
        color.Description.Metadata.Compression = DisplayCompression.Dcc256_256_0;
        color.Description.TileMode = GuestTileMode.RenderTarget;
        var colorIdentifier = harness.Acquire(ref color);
        Assert.True(harness.Images.OverlapsDccMetadata(metadataAddress, 1));

        var depth = AsDepthTarget(LinearRequest(address + 0x8000, 4, Format.D32Sfloat,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1), Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadataAddress, 0x100);
        harness.Acquire(ref depth);

        Assert.True(harness.Images.IsMetadata(metadataAddress));
        Assert.False(harness.Images.OverlapsDccMetadata(metadataAddress, 1));
        Assert.True(harness.Images.IsMetadataCleared(metadataAddress, 0));
        harness.Worker.Run(() =>
        {
            harness.Image(colorIdentifier).ClearGpuModified();
            harness.Images.DeleteImageForTest(colorIdentifier);
        });
        Assert.True(harness.Images.IsMetadata(metadataAddress));
        Assert.True(harness.Images.ClearMetadata(metadataAddress));
        harness.Shutdown();
    }

    // A DCC fill seen before its target leaves a pending registration, and a write over the fill's range leaves
    // it unknown. A depth target that takes the address as its HTile drops the pending DCC state either way, so
    // it starts as at an address nothing registered.
    [Fact]
    public void DepthTarget_StartsAsNewOverAPendingDccFillAWriteLeftUnknown()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        Assert.False(harness.Images.TryAbsorbDccFill(metadataAddress, 0x100, 0));
        Assert.False(harness.ImageStore.MarkCpuWrite(metadataAddress, 8));

        var depth = AsDepthTarget(LinearRequest(address + 0x8000, 4, Format.D32Sfloat,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1), Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadataAddress, 0x100);
        harness.Acquire(ref depth);

        Assert.True(harness.Images.IsMetadataCleared(metadataAddress, 0));
        harness.Shutdown();
    }

    // A color target whose DCC lands where a depth target kept its HTile takes the address the same way.
    [Fact]
    public void ColorTarget_ReplacesTheHtileOfAnotherSurfaceAtItsDccAddress()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadataAddress = address + 0x10000;
        var depth = AsDepthTarget(LinearRequest(address + 0x8000, 4, Format.D32Sfloat,
            GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1), Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadataAddress, 0x100);
        harness.Acquire(ref depth);
        Assert.True(harness.Images.ClearMetadata(metadataAddress));

        var color = AsColorTarget(LinearRequest(address + 0x4000, 4, Format.R8G8B8A8Srgb,
            GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1));
        color.Description.Metadata.Kind = MetadataKind.Dcc;
        // A zero range size selects deferred metadata registration.
        color.Description.Metadata.Range = new GuestSpan(metadataAddress, 0);
        color.Description.Metadata.Compression = DisplayCompression.Dcc256_256_0;
        color.Description.TileMode = GuestTileMode.RenderTarget;
        harness.Acquire(ref color);

        Assert.True(harness.Images.OverlapsDccMetadata(metadataAddress, 1));
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
        Assert.False(harness.Images.ClearMetadata(metadataAddress));
        harness.Shutdown();
    }

    [Fact]
    public void CpuWriteInvalidatesTheExpandedPendingDccFill()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x300000, ReadWrite);
        var metadataAddress = address + 0x200000;
        Assert.False(harness.Images.TryAbsorbDccFill(metadataAddress, 0x100, 0xc0c0c0c0u));
        Assert.False(harness.Images.TryAbsorbDccFill(metadataAddress, 0x300, 0xc0c0c0c0u));
        Assert.False(harness.ImageStore.MarkCpuWrite(metadataAddress + 0x200, 8));

        var color = LinearRequest(address, 4, Format.R8G8B8A8Srgb,
            GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        color = AsColorTarget(color);
        color.Description.Metadata.Kind = MetadataKind.Dcc;
        // Keep this request on the deferred fill path.
        color.Description.Metadata.Range = new GuestSpan(metadataAddress, 0);
        color.Description.Metadata.Compression = DisplayCompression.Dcc256_256_0;
        harness.Acquire(ref color);
        Assert.True(harness.Images.IsMetadata(metadataAddress));
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
    }

    [Fact]
    public void ClearStateChanges_AreSeenOnlyForTheirAddress()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var cleared = address + 0x1000;
        var untouched = address + 0x2000;
        harness.Images.RegisterHtileMetadataForTest(cleared);
        harness.Images.RegisterHtileMetadataForTest(untouched);
        var generation = harness.Images.MetadataGeneration;
        Assert.True(harness.Images.MetadataUnchangedSinceForTest(generation, cleared));

        Assert.True(harness.Images.ClearMetadata(cleared));
        Assert.False(harness.Images.MetadataUnchangedSinceForTest(generation, cleared));
        Assert.True(harness.Images.MetadataUnchangedSinceForTest(generation, untouched));

        generation = harness.Images.MetadataGeneration;
        Assert.True(harness.Images.SetMetadataSlice(untouched, 0, true));
        Assert.False(harness.Images.MetadataUnchangedSinceForTest(generation, untouched));
        Assert.True(harness.Images.MetadataUnchangedSinceForTest(generation, cleared));
        harness.Shutdown();
    }

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

    // A fill seen before its target leaves its range known as DCC metadata, so the next fill of it
    // runs on the host and the range stays CPU-readable.
    [Fact]
    public void APendingDccFill_MarksItsRangeAsDccMetadata()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadata = address + 0x10000;
        Assert.False(harness.Images.OverlapsDccMetadata(metadata, 0x100));

        Assert.False(harness.Images.TryAbsorbDccFill(metadata, 0x100, 0x00000000u));

        Assert.True(harness.Images.OverlapsDccMetadata(metadata + 0x80, 0x10));
        Assert.False(harness.Images.OverlapsDccMetadata(metadata + 0x100, 0x10));
    }

    [Fact]
    public void DccFills_RemainGuestProducersUntilDescriptorDiscovery()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadata = address + 0x10000;
        harness.Write(metadata, Bytes(0x11223344u));
        Assert.False(harness.Images.TryAbsorbDccFill(metadata, 0x1000, 0x20202020u));
        Assert.False(harness.Images.IsMetadata(metadata));
        Assert.False(harness.Images.TryAbsorbDccFill(metadata, 0x1000, 0x40404040u));

        harness.Write(address + 0xc000, Bytes(0x55667788u));
        var target = LinearRequest(address + 0xc000, 4, Format.R8G8B8A8Srgb, GuestPixelFormat.Bits8_8_8_8Srgb, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        target = AsColorTarget(target);
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        target.Description.Metadata.Range = new GuestSpan(metadata, 0x1000);
        target.Description.Metadata.Compression = DisplayCompression.Dcc256_256_0;
        var targetId = harness.Acquire(ref target);
        Assert.False(harness.Images.IsMetadata(metadata));
        Assert.False(harness.Images.IsMetadataCleared(metadata, 0));
        Assert.Equal(0x11223344u, harness.ReadUInt32(metadata));
        Assert.False(harness.Images.TryAbsorbDccFill(metadata, 0x1000, 0x01010101u));
        Assert.False(harness.Images.TryAbsorbDccFill(metadata, 0x1000, 0xc0c0c0c0u));
        Assert.False(harness.Images.IsMetadataCleared(metadata, 5));
        Assert.Equal(0x11223344u, harness.ReadUInt32(metadata));
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

        var depth = LinearRequest(address + 0xe000, 4, Format.D32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        depth = AsDepthTarget(depth, Format.D32Sfloat);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        depth.Description.Metadata.Range = new GuestSpan(metadata, 0x100);
        var depthId = harness.Find(ref depth);
        var depthLocal = depth;
        harness.Worker.Run(() => Assert.NotEqual(
            0UL,
            harness.Images.AcquireDepthTargetView(depthId, depthLocal).Handle));
        Assert.True(harness.Images.IsMetadata(metadata));
        harness.Shutdown();
    }

    [Fact]
    public void DccVolumeOverThirtyTwoSlices_MaterializesAndConsumesEveryUniformSlice()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x80000, ReadWrite);
        var imageAddress = address + 0x1000;
        var metadataAddress = address + 0x10000;
        const uint depth = 64;
        const ulong metadataSliceSize = 0x1000;
        var metadataBytes = new byte[checked((int)(depth * metadataSliceSize))];
        Array.Fill(metadataBytes, (byte)0xc0);
        harness.Write(metadataAddress, metadataBytes);

        const uint width = 4;
        const uint height = 4;
        var imageSize = (ulong)width * height * depth * 4;
        var target = LinearRequest(
            imageAddress,
            imageSize,
            Format.R8G8B8A8Unorm,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            GuestImageType.Color3D,
            new Extent3D(width, height, depth),
            1,
            4,
            1);
        target = AsColorTarget(target);
        target.View = target.View with
        {
            Type = ImageViewType.Type2DArray,
            BaseLayer = 0,
            LayerCount = depth,
        };
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        target.Description.Metadata.Range = new GuestSpan(metadataAddress, depth * metadataSliceSize);
        target.Description.Metadata.DccAlphaMsb = true;

        Assert.False(harness.Images.TryAbsorbDccFill(
            metadataAddress,
            depth * metadataSliceSize,
            0xc0c0c0c0u));
        var targetId = harness.Find(ref target);
        harness.Finish();

        Assert.True(targetId.IsValid);
        Assert.Equal(uint.MaxValue, harness.ReadUInt32(metadataAddress));
        Assert.Equal(uint.MaxValue, harness.ReadUInt32(metadataAddress + 31 * metadataSliceSize));
        Assert.Equal(uint.MaxValue, harness.ReadUInt32(metadataAddress + 32 * metadataSliceSize));
        Assert.Equal(uint.MaxValue, harness.ReadUInt32(metadataAddress + (depth - 1) * metadataSliceSize));
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 0));
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 31));
        Assert.False(harness.Images.IsMetadataCleared(metadataAddress, 32));
        harness.Shutdown();
    }

    [Fact]
    public void SelflossShapeDccVolume_HandlesOneHundredTwentyEightSlicesIndependently()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x140000, ReadWrite);
        var imageAddress = address + 0x1000;
        var metadataAddress = address + 0x20000;
        const uint depth = 128;
        const ulong metadataSliceSize = 0x2000;
        var metadataBytes = new byte[checked((int)(depth * metadataSliceSize))];
        Array.Fill(metadataBytes, (byte)0xc0);
        const uint mixedSlice = 73;
        Array.Fill(
            metadataBytes,
            (byte)0x55,
            checked((int)(mixedSlice * metadataSliceSize + 1)),
            checked((int)metadataSliceSize - 1));
        harness.Write(metadataAddress, metadataBytes);

        const uint width = 4;
        const uint height = 4;
        var imageSize = (ulong)width * height * depth * 8;
        var texture = LinearRequest(
            imageAddress,
            imageSize,
            Format.R16G16B16A16Sfloat,
            GuestPixelFormat.Bits16_16_16_16Float,
            GuestImageType.Color3D,
            new Extent3D(width, height, depth),
            1,
            8,
            1);
        texture.Description.Metadata.Kind = MetadataKind.Dcc;
        texture.Description.Metadata.Range = new GuestSpan(
            metadataAddress,
            depth * metadataSliceSize);
        texture.Description.Metadata.DccAlphaMsb = true;

        var textureId = harness.Find(ref texture);
        harness.Finish();

        Assert.True(textureId.IsValid);
        Assert.Equal(uint.MaxValue, harness.ReadUInt32(metadataAddress));
        Assert.Equal(
            0x555555c0u,
            harness.ReadUInt32(metadataAddress + mixedSlice * metadataSliceSize));
        Assert.Equal(
            uint.MaxValue,
            harness.ReadUInt32(metadataAddress + (depth - 1) * metadataSliceSize));
        Assert.False(harness.Images.IsMetadata(metadataAddress));
        harness.Shutdown();
    }

    [Fact]
    public void DccMetadataScan_RejectsAMismatchBeyondTheFirstPooledChunk()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x50000, ReadWrite);
        var imageAddress = address + 0x1000;
        var metadataAddress = address + 0x10000;
        const uint layers = 2;
        const ulong metadataSliceSize = 0x11000;
        var metadataBytes = new byte[checked((int)(layers * metadataSliceSize))];
        Array.Fill(metadataBytes, (byte)0xc0);
        metadataBytes[^1] = 0x55;
        harness.Write(metadataAddress, metadataBytes);

        const uint width = 4;
        const uint height = 4;
        var texture = LinearRequest(
            imageAddress,
            (ulong)width * height * layers * 4,
            Format.R8G8B8A8Unorm,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            GuestImageType.Color2D,
            new Extent3D(width, height, 1),
            layers,
            4,
            1);
        texture.Description.Metadata.Kind = MetadataKind.Dcc;
        texture.Description.Metadata.Range = new GuestSpan(
            metadataAddress,
            layers * metadataSliceSize);
        texture.Description.Metadata.DccAlphaMsb = true;

        var textureId = harness.Find(ref texture);
        harness.Finish();

        Assert.True(textureId.IsValid);
        Assert.Equal(uint.MaxValue, harness.ReadUInt32(metadataAddress));
        Assert.Equal(0xc0c0c0c0u, harness.ReadUInt32(metadataAddress + metadataSliceSize));
        Assert.Equal(0x55, harness.Read(metadataAddress + 2 * metadataSliceSize - 1, 1)[0]);
        harness.Shutdown();
    }

    // A layer that a texture grew into a mip chain is still looked up as a one-level color target every frame. Its metadata slice lies outside
    // the image's range or, as in a packed allocation, inside it behind the first level.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DccClear_ReachesAOneLevelColorTargetOfAnImageThatHasAMipChain(bool metadataInsideTheImage)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var imageAddress = address + 0x1000;
        var metadataAddress = address + (metadataInsideTheImage ? 0x3000UL : 0x10000UL);
        var secondLevelOffset = metadataInsideTheImage ? 0x4000UL : 32UL;
        const ulong metadataSliceSize = 0x1000;
        // 0x40 is the fast-clear code of (0, 0, 0, 1).
        harness.Write(metadataAddress, Enumerable.Repeat((byte)0x40, (int)metadataSliceSize).ToArray());

        var chain = LinearRequest(imageAddress, secondLevelOffset + 8, Format.R16G16B16A16Sfloat, GuestPixelFormat.Bits16_16_16_16Float, GuestImageType.Color2D, new Extent3D(2, 2, 1), 1, 8, 1);
        chain.Description.Resources = new SubresourceCount(2, 1);
        chain.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 32, Pitch = 2, Height = 2 };
        chain.Description.MipLayout[1] = new MipLevelLayout { Offset = secondLevelOffset, Size = 8, Pitch = 1, Height = 1 };
        chain.View = chain.View with { LevelCount = 2 };
        var chainId = harness.Find(ref chain);

        var target = AsColorTarget(chain);
        target.Description.Resources = new SubresourceCount(1, 1);
        target.View = target.View with { LevelCount = 1 };
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        target.Description.Metadata.Range = new GuestSpan(metadataAddress, metadataSliceSize);
        target.Description.Metadata.DccAlphaMsb = true;
        var targetId = harness.Find(ref target);
        Assert.Equal(chainId, targetId);

        // The fill that consumes the code is the emulator's own write to the metadata slice; the image it has just cleared keeps its contents.
        Assert.False(harness.Image(targetId).IsBufferModified);
        var local = target;
        harness.Worker.Run(() => harness.Images.AcquireColorTargetView(targetId, local));
        harness.Finish();

        var opaqueBlack = Enumerable.Range(0, 4).SelectMany(_ => new byte[] { 0, 0, 0, 0, 0, 0, 0, 0x3c }).ToArray();
        Assert.Equal(opaqueBlack, harness.ReadImageBytes(harness.Image(targetId)));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestDccMetadataFilledOnTheGpu_IsDownloadedWhenTheTargetIsSampled(bool storage)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong SliceSize = 0x1000;
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadata = address + 0x10000;
        var sampled = LinearRequest(address + 0xc000, 4, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        var target = AsColorTarget(sampled);
        var targetId = harness.Find(ref target);
        var image = harness.Image(targetId);
        image.Description.TileMode = GuestTileMode.RenderTarget;
        image.Description.Metadata.Kind = MetadataKind.Dcc;
        image.Description.Metadata.Range = new GuestSpan(metadata, SliceSize);
        Assert.Equal(SliceSize, image.Description.DccSliceSize);
        harness.Write(metadata, Enumerable.Repeat((byte)0x55, (int)SliceSize).ToArray());
        harness.Worker.Run(() =>
        {
            Assert.NotNull(harness.Cache.ObtainBuffer(metadata, SliceSize, isWritten: true).Buffer);
            harness.Cache.FillBuffer(metadata, SliceSize, 0, isGds: false);
        });
        Assert.True(harness.Cache.HasGpuDirtyBytes(metadata, SliceSize));
        Assert.False(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 0, out _, out _));

        sampled.Description.TileMode = GuestTileMode.RenderTarget;
        if (storage)
        {
            sampled = AsStorage(sampled);
        }

        Assert.Equal(targetId, harness.Find(ref sampled));
        Assert.False(harness.Cache.HasGpuDirtyBytes(metadata, SliceSize));
        Assert.True(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 0, out _, out var code));
        Assert.Equal(0x00, code);
        harness.Shutdown();
    }

    [Theory]
    [InlineData(64u)]
    [InlineData(65u)]
    public void DccVolume_TracksAndConsumesEverySlice(uint depth)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x100000, ReadWrite);
        var metadata = address + 0x80000;
        Assert.False(harness.Images.TryAbsorbDccFill(metadata, depth * 4096ul, 0x40404040));
        var target = AsColorTarget(LinearRequest(address, depth * 4ul, Format.R8G8B8A8Unorm,
            GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color3D, new Extent3D(1, 1, depth), 1, 4, 1));
        target.View = target.View with { Type = ImageViewType.Type2DArray, LayerCount = depth };
        target.Description.Metadata.Kind = MetadataKind.Dcc;
        // Test deferred slice state without consuming native metadata bytes.
        target.Description.Metadata.Range = new GuestSpan(metadata, 0);
        harness.Acquire(ref target);
        for (uint slice = 0; slice < depth; slice++)
        {
            Assert.True(harness.Images.IsMetadataCleared(metadata, slice, out var fill));
            Assert.Equal(0x40404040u, fill);
        }
        Assert.False(harness.Images.IsMetadataCleared(metadata, depth));
        Assert.False(harness.Images.SetMetadataSlice(metadata, depth, false));
        for (uint slice = 0; slice < depth; slice++)
            Assert.True(harness.Images.SetMetadataSlice(metadata, slice, false));
        for (uint slice = 0; slice < depth; slice++)
            Assert.False(harness.Images.IsMetadataCleared(metadata, slice));
        Assert.True(harness.Images.TryAbsorbDccFill(metadata, depth * 4096ul, 0x40404040));
        Assert.True(harness.Images.IsMetadataCleared(metadata, depth - 1));
        harness.Shutdown();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DccDescriptor_SynchronizesMetadataOnAnImageFoundWithoutIt(bool native)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong SliceSize = 0x1000;
        var address = harness.MapBacked(0x20000, ReadWrite);
        var metadata = address + 0x10000;
        var plain = LinearRequest(address + 0xc000, 4, Format.R8G8B8A8Unorm, GuestPixelFormat.Bits8_8_8_8UNorm, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        var imageId = harness.Find(ref plain);
        var image = harness.Image(imageId);
        image.Description.TileMode = GuestTileMode.RenderTarget;
        Assert.Equal(MetadataKind.None, image.Description.Metadata.Kind);
        harness.Write(metadata, Enumerable.Repeat((byte)0x55, (int)SliceSize).ToArray());
        harness.Worker.Run(() =>
        {
            Assert.NotNull(harness.Cache.ObtainBuffer(metadata, SliceSize, isWritten: true).Buffer);
            harness.Cache.FillBuffer(metadata, SliceSize, 0, isGds: false);
        });
        Assert.True(harness.Cache.HasGpuDirtyBytes(metadata, SliceSize));

        var compressed = plain;
        compressed.Description.TileMode = GuestTileMode.RenderTarget;
        compressed.Description.Metadata.Kind = MetadataKind.Dcc;
        compressed.Description.Metadata.Range = new GuestSpan(metadata, native ? SliceSize : 0);
        Assert.Equal(imageId, harness.Find(ref compressed));
        Assert.Equal(MetadataKind.Dcc, image.Description.Metadata.Kind);
        Assert.Equal(metadata, image.Description.Metadata.Range.Address);
        Assert.Equal(SliceSize, image.Description.DccSliceSize);
        Assert.False(harness.Cache.HasGpuDirtyBytes(metadata, SliceSize));
        harness.Finish();
        Assert.Equal(native ? uint.MaxValue : 0u, harness.ReadUInt32(metadata));
        Assert.Equal(!native, harness.Images.TryReadGuestDccClear(metadata, SliceSize, 0, out _, out _));
        harness.Shutdown();
    }

    [Fact]
    public void GuestDccClear_ReadsOnlyUniformClearSlices()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong SliceSize = 0x2000;
        var metadata = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(metadata, Enumerable.Repeat((byte)0x80, (int)SliceSize).ToArray());
        harness.Write(metadata + SliceSize, Enumerable.Repeat((byte)0x00, (int)SliceSize).ToArray());
        var mixed = Enumerable.Repeat((byte)0x40, (int)SliceSize).ToArray();
        mixed[^1] = 0xff;
        harness.Write(metadata + 2 * SliceSize, mixed);
        harness.Write(metadata + 3 * SliceSize, Enumerable.Repeat((byte)0xff, (int)SliceSize).ToArray());

        Assert.True(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 0, out var slice, out var code));
        Assert.Equal(metadata, slice);
        Assert.Equal(0x80, code);
        Assert.True(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 1, out slice, out code));
        Assert.Equal(metadata + SliceSize, slice);
        Assert.Equal(0x00, code);
        Assert.False(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 2, out _, out _));
        Assert.False(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 3, out _, out _));
        Assert.False(harness.Images.TryReadGuestDccClear(metadata, 0, 0, out _, out _));

        harness.Worker.Run(() => harness.Cache.FillBuffer(metadata, SliceSize, uint.MaxValue, isGds: false));
        Assert.False(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 0, out _, out _));
        Assert.True(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 1, out _, out _));
        harness.Shutdown();
    }

    [Fact]
    public void GpuDccFill_IsReadableWithoutSynchronization()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong SliceSize = 0x2000;
        var memory = harness.MapBacked(0x20000, ReadWrite);
        var metadata = memory + 0x4000;
        var plain = memory + 0x10000;
        harness.Write(metadata, Enumerable.Repeat((byte)0xff, (int)SliceSize).ToArray());
        harness.Write(plain, Enumerable.Repeat((byte)0xff, (int)SliceSize).ToArray());
        harness.Images.RegisterDccMetadataForTest(metadata, SliceSize);
        harness.Worker.Run(() =>
        {
            _ = harness.Cache.ObtainBuffer(metadata, SliceSize, isWritten: true);
            _ = harness.Cache.ObtainBuffer(plain, SliceSize, isWritten: true);
        });
        Assert.True(harness.Cache.HasGpuDirtyBytes(metadata, SliceSize));
        Assert.True(harness.Cache.HasGpuDirtyBytes(plain, SliceSize));

        harness.Worker.Run(() =>
        {
            harness.Cache.FillBuffer(metadata, SliceSize, 0x00000000, isGds: false);
            harness.Cache.FillBuffer(plain, SliceSize, 0x00000000, isGds: false);
        });

        Assert.False(harness.Cache.HasGpuDirtyBytes(metadata, SliceSize));
        Assert.True(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 0, out _, out var code));
        Assert.Equal(0x00, code);
        Assert.True(harness.Cache.HasGpuDirtyBytes(plain, SliceSize));
        harness.Shutdown();
    }

    // The pages inside a filled range are clean after it; a page it shares with another GPU-written
    // range keeps that range's state.
    [Fact]
    public void DirtyDccFill_KeepsASharedBoundaryPageDirty()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong Page = SharpEmu.HLE.GpuMemory.TrackerLayout.PageBytes;
        var memory = harness.MapBacked(8 * Page, ReadWrite);
        var metadata = memory + Page + Page / 2;
        var size = 3 * Page;
        var neighbour = metadata - 0x100;
        harness.Images.RegisterDccMetadataForTest(metadata, size);
        harness.Worker.Run(() =>
        {
            _ = harness.Cache.ObtainBuffer(neighbour, 0x100, isWritten: true);
            _ = harness.Cache.ObtainBuffer(metadata, size, isWritten: true);
        });

        harness.Worker.Run(() => harness.Cache.FillBuffer(metadata, size, 0x00000000, isGds: false));

        Assert.False(harness.Cache.HasGpuDirtyBytes(metadata, size));
        Assert.False(harness.Cache.HasGpuDirtyPages(memory + 2 * Page, 2 * Page));
        Assert.False(harness.Cache.HasGpuDirtyPages(memory + 4 * Page, Page));
        Assert.True(harness.Cache.HasGpuDirtyPages(memory + Page, Page));
        Assert.True(harness.Cache.HasGpuDirtyBytes(neighbour, 0x100));
        harness.Shutdown();
    }

    [Fact]
    public void CleanDccFill_WritesBothCopiesWithoutMarkingTheGpuCopyNewer()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const ulong SliceSize = 0x2000;
        var metadata = harness.MapBacked(0x10000, ReadWrite) + 0x4000;
        harness.Write(metadata, Enumerable.Repeat((byte)0xff, (int)SliceSize).ToArray());
        harness.Images.RegisterDccMetadataForTest(metadata, SliceSize);
        _ = harness.Worker.Run(() => harness.Cache.ObtainBuffer(metadata, SliceSize, isWritten: false));

        harness.Worker.Run(() => harness.Cache.FillDccMetadata(metadata, SliceSize, 0x20202020));

        Assert.False(harness.Cache.HasGpuDirtyBytes(metadata, SliceSize));
        Assert.False(harness.Cache.HasGpuDirtyPages(metadata, SliceSize));
        Assert.True(harness.Images.TryReadGuestDccClear(metadata, SliceSize, 0, out _, out var code));
        Assert.Equal(0x20, code);
        harness.Finish();
        var (after, afterOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(metadata, SliceSize, isWritten: false));
        Assert.Equal(Enumerable.Repeat((byte)0x20, 16), harness.ReadBack(after, afterOffset + SliceSize - 16, 16));
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
