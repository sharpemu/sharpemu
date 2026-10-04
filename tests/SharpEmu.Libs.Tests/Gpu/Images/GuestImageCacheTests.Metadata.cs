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

        // DCC is not retained in the generic metadata map, so a later HTile allocation can
        // classify the same address without inheriting stale DCC side state.
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
