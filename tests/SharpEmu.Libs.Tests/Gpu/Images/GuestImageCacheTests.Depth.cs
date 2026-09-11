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

// Depth and stencil overlaps, buffer synchronization from images, and layered conversions.
public sealed unsafe partial class GuestImageCacheTests
{
    [Fact]
    public void BufferSynchronization_CopiesFittingMipsVolumesAndWholeDepthOnly()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x40000, ReadWrite);

        // A single mip larger than a caching page; the prefix must fit whole.
        const uint width = 4097;
        var pitch = TileGeometry.TexturePitch(GuestPixelFormat.Bits32UInt, width, GuestTileMode.Linear);
        var spans = new TileLevelSpan[TiledSurfaceLayout.MaxLevels];
        var padded = new TilePaddedSize[TiledSurfaceLayout.MaxLevels];
        Assert.True(TileGeometry.TryGetTextureSize(GuestPixelFormat.Bits32UInt, width, 1, 1, GuestTileMode.Linear, out var total, spans, padded));
        var prefixSize = (ulong)spans[0].Offset + spans[0].Size;
        var guestSize = total.Size + 256UL;
        Assert.True(spans[0].Offset == 0 && prefixSize > GuestBufferCache.CachingPageSize && prefixSize < guestSize);
        var native = new uint[guestSize / 4];
        for (var index = 0; index < native.Length; index++)
        {
            native[index] = 0x61000000u + (uint)index;
        }

        harness.Write(address, Bytes(native));
        var request = LinearRequest(address, guestSize, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D, new Extent3D(width, 1, 1), 1, 4, 1);
        request.Description.Pitch = pitch;
        request.Description.MipLayout[0] = new MipLevelLayout { Offset = spans[0].Offset, Size = spans[0].Size, Pitch = padded[0].Width, Height = padded[0].Height };
        var imageIdentifier = harness.Acquire(ref request);
        harness.MarkGpuWritten(imageIdentifier);
        harness.Write(address, Enumerable.Repeat((byte)0xef, (int)guestSize).ToArray());

        harness.Worker.Run(() =>
        {
            using var insufficient = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, address, GpuBuffer.AllFlags, prefixSize - 1);
            using var prefix = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, address, GpuBuffer.AllFlags, prefixSize);
            Assert.False(harness.Images.TrySynchronizeBufferFromImage(insufficient, address, prefixSize - 1));
            Assert.True(harness.Images.TrySynchronizeBufferFromImage(prefix, address, prefixSize));
            Assert.True(harness.Image(imageIdentifier).IsGpuModified);
            Assert.Equal(native[0], BitConverter.ToUInt32(harness.CopyFromDevice(prefix.Handle, 0, 4)));
        });
        Assert.False(harness.Cache.IsRegionRegistered(address, guestSize));
        var (formatted, formattedOffset) = harness.Worker.Run(() => harness.Cache.ObtainBuffer(address, guestSize, isWritten: false, isTexelBuffer: true));
        Assert.NotNull(formatted);
        Assert.True(harness.Image(imageIdentifier).IsGpuModified);
        Assert.Equal(native[0], BitConverter.ToUInt32(harness.ReadBufferBytes(formatted, formattedOffset, 4)));

        // A volume synchronizes only as a whole; each linear slice is padded to its layout stride.
        const ulong volumeSlice = 256;
        harness.Write(address + 0x20000, Bytes(0x10203040u));
        harness.Write(address + 0x20000 + volumeSlice, Bytes(0x50607080u));
        var volume = LinearRequest(address + 0x20000, 2 * volumeSlice, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color3D, new Extent3D(1, 1, 2), 1, 4, 1);
        var volumeId = harness.Acquire(ref volume);
        harness.MarkGpuWritten(volumeId);
        harness.Worker.Run(() =>
        {
            using var partial = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, address + 0x20000, GpuBuffer.AllFlags, 2 * volumeSlice - 4);
            using var full = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, address + 0x20000, GpuBuffer.AllFlags, 2 * volumeSlice);
            Assert.False(harness.Images.TrySynchronizeBufferFromImage(partial, address + 0x20000, 2 * volumeSlice - 4));
            Assert.True(harness.Images.TrySynchronizeBufferFromImage(full, address + 0x20000, 2 * volumeSlice));
            Assert.True(harness.Image(volumeId).IsGpuModified);
            harness.Scheduler.Finish();
        });

        // Depth synchronizes only as a whole image.
        harness.Write(address + 0x30000, Bytes(0.25f, 0.75f, 0.5f));
        var depth = LinearRequest(address + 0x30000, 12, Format.D32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(2, 1, 1), 1, 4, 1);
        depth = AsDepthTarget(depth, Format.D32Sfloat);
        depth.Description.Resources = new SubresourceCount(2, 1);
        depth.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 8, Pitch = 2, Height = 1 };
        depth.Description.MipLayout[1] = new MipLevelLayout { Offset = 8, Size = 4, Pitch = 1, Height = 1 };
        depth.View = depth.View with { LevelCount = 2 };
        var depthId = harness.Acquire(ref depth);
        harness.MarkGpuWritten(depthId);
        harness.Worker.Run(() =>
        {
            using var partial = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.DeviceLocal, address + 0x30000, GpuBuffer.AllFlags, 8);
            Assert.False(harness.Images.TrySynchronizeBufferFromImage(partial, address + 0x30000, 8));
            Assert.True(harness.Image(depthId).IsGpuModified);
            harness.Scheduler.Finish();
        });
        harness.Shutdown();
    }

    [Fact]
    public void RawD16UintTexture_RecreatesAnIntegerBacking()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        harness.Write(address, Bytes((ushort)0x2000, (ushort)0xc000));
        var rawDepth = LinearRequest(address, 4, Format.D16Unorm, GuestPixelFormat.Bits16UNorm, GuestImageType.Color2D, new Extent3D(2, 1, 1), 1, 2, 1);
        rawDepth = AsDepthTarget(rawDepth, Format.D16Unorm);
        var rawDepthId = harness.Acquire(ref rawDepth);

        var rawUint = rawDepth;
        rawUint.Role = ImageRole.Texture;
        rawUint.Description.PixelFormat = Format.R16Uint;
        rawUint.Description.GuestFormat = GuestPixelFormat.Bits16UInt;
        rawUint.View = rawUint.View with { Format = Format.R16Uint, Aspect = ImageAspectFlags.ColorBit, Usage = ImageUsageFlags.SampledBit };
        var rawUintId = harness.Find(ref rawUint);
        Assert.True(rawUintId.IsValid);
        Assert.NotEqual(rawDepthId, rawUintId);
        Assert.False(harness.Images.Contains(rawDepthId));
        Assert.Equal(Format.R16Uint, harness.Image(rawUintId).Backing.Format);
        var local = rawUint;
        Assert.NotEqual(0UL, harness.Worker.Run(() => harness.Images.AcquireTextureView(rawUintId, local)).Handle);

        // A partial depth view of a six-layer color image keeps the six-layer owner.
        var layeredAddress = address + 0x1000;
        var layeredValues = new ushort[] { 0x0102, 0x0304, 0x1112, 0x1314, 0x2122, 0x2324, 0x3132, 0x3334, 0x4142, 0x4344, 0x5152, 0x5354 };
        harness.Write(layeredAddress, Bytes(layeredValues));
        var layeredColor = LinearRequest(layeredAddress, 24, Format.R16Unorm, GuestPixelFormat.Bits16UNorm, GuestImageType.Color2D, new Extent3D(2, 1, 1), 6, 2, 1);
        var layeredColorId = harness.Acquire(ref layeredColor);
        harness.Worker.Run(() =>
        {
            var image = harness.Image(layeredColorId);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
            var clear = new ClearColorValue { Float32_0 = 1.0f };
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 6);
            harness.Vulkan.Vk.CmdClearColorImage(command, image.Backing.Handle, ImageLayout.TransferDstOptimal, &clear, 1, &range);
        });
        harness.MarkGpuWritten(layeredColorId);

        var layeredDepth = AsDepthTarget(layeredColor, Format.D16Unorm);
        layeredDepth.Description.Data = new GuestSpan(layeredAddress, 20);
        layeredDepth.Description.GuestFormat = GuestPixelFormat.Bits16UNorm;
        layeredDepth.Description.Resources = new SubresourceCount(1, 5);
        layeredDepth.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 20, Pitch = 2, Height = 1 };
        layeredDepth.View = layeredDepth.View with { LayerCount = 5 };
        var layeredDepthId = harness.Find(ref layeredDepth);
        var layeredOwner = harness.Image(layeredDepthId);
        Assert.NotEqual(layeredColorId, layeredDepthId);
        Assert.False(harness.Images.Contains(layeredColorId));
        Assert.Equal(6u, layeredOwner.Description.Resources.Layers);
        Assert.Equal(24UL, layeredOwner.Description.Data.Size);
        Assert.Equal(24UL, layeredOwner.Description.MipLayout[0].Size);
        var depthLocal = layeredDepth;
        harness.Worker.Run(() => harness.Images.AcquireDepthTargetView(layeredDepthId, depthLocal));

        var reacquired = layeredColor;
        reacquired.Description.PixelFormat = Format.R16Uint;
        reacquired.Description.GuestFormat = GuestPixelFormat.Bits16UInt;
        reacquired.View = reacquired.View with { Format = Format.R16Uint };
        var reacquiredId = harness.Acquire(ref reacquired);
        Assert.True(reacquiredId.IsValid);
        Assert.NotEqual(layeredDepthId, reacquiredId);
        Assert.False(harness.Images.Contains(layeredDepthId));
        Assert.Equal(Format.R16Uint, harness.Image(reacquiredId).Backing.Format);
        Assert.All(harness.ReadImageBytes(harness.Image(reacquiredId)), value => Assert.Equal(0xff, value));
        harness.Shutdown();
    }

    [Fact]
    public void PartialDepthWithHtile_PreservesTheLayeredBackingAndRefreshesEveryLayer()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        const uint layers = 6;
        const ulong sliceSize = 0x10000;
        const ulong totalSize = sliceSize * layers;
        var address = harness.MapBacked(totalSize + 0x10000, ReadWrite);
        var color = LinearRequest(address, totalSize, Format.R32Sfloat, GuestPixelFormat.Bits32Float,
            GuestImageType.Color2D, new Extent3D(128, 128, 1), layers, 4, 1);
        color.Description.TileMode = GuestTileMode.Depth;
        var colorIdentifier = harness.Acquire(ref color);
        harness.Worker.Run(() =>
        {
            var image = harness.Image(colorIdentifier);
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            image.Transition(ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, null, command);
            for (uint layer = 0; layer < layers; layer++)
            {
                var clear = new ClearColorValue { Float32_0 = (layer + 1) / 8f };
                var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, layer, 1);
                harness.Vulkan.Vk.CmdClearColorImage(command, image.Backing.Handle, ImageLayout.TransferDstOptimal, &clear, 1, &range);
            }
        });
        harness.MarkGpuWritten(colorIdentifier);

        var depth = AsDepthTarget(color, Format.D32Sfloat);
        depth.Description.Data = new GuestSpan(address, sliceSize);
        depth.Description.Resources = SubresourceCount.Single;
        depth.Description.MipLayout[0].Size = sliceSize;
        depth.Description.Metadata = new MetadataDescription
        {
            Kind = MetadataKind.Htile,
            Range = new GuestSpan(address + totalSize, 0x10000),
        };
        depth.View = depth.View with { LayerCount = 1 };
        var depthIdentifier = harness.Find(ref depth);
        var owner = harness.Image(depthIdentifier);
        Assert.Equal(layers, owner.Description.Resources.Layers);
        Assert.Equal(totalSize, owner.Description.Data.Size);
        Assert.Equal(totalSize, owner.Description.MipLayout[0].Size);
        Assert.Equal(depth.Description.Metadata.Range, owner.Description.Metadata.Range);
        Assert.False(harness.Images.Contains(colorIdentifier));
        var localDepth = depth;
        harness.Worker.Run(() => harness.Images.AcquireDepthTargetView(depthIdentifier, localDepth));
        var copied = harness.ReadImageBytes(owner, ImageAspectFlags.DepthBit);
        for (var layer = 0; layer < layers; layer++)
            Assert.Equal((layer + 1) / 8f, BitConverter.ToSingle(copied, layer * (int)sliceSize));

        // A buffer invalidation must reload the complete owner, not divide a partial view among its layers.
        var replacement = Enumerable.Repeat(0.75f, (int)(totalSize / sizeof(float))).ToArray();
        harness.Write(address, Bytes(replacement));
        harness.Worker.Run(() =>
        {
            harness.Images.InvalidateMemoryFromGpu(address, totalSize);
            harness.Images.AcquireDepthTargetView(depthIdentifier, localDepth);
        });
        var refreshed = harness.ReadImageBytes(owner, ImageAspectFlags.DepthBit);
        for (var layer = 0; layer < layers; layer++)
            Assert.Equal(0.75f, BitConverter.ToSingle(refreshed, layer * (int)sliceSize));
        harness.Shutdown();
    }

    [Fact]
    public void UnequalSampleDepthOverlap_RunsTheColorToDepthBlit()
    {
        if (!GatePrerequisites.Ready(_vulkan, sampleRateShading: true)) return;
        using var harness = new CacheHarness(_vulkan);
        using var reader = new MultisampleDepthSampleReader(_vulkan, harness.Scheduler, harness.Worker.Run);
        var address = harness.MapBacked(0x100000, ReadWrite);
        harness.Write(address + 0x2000, Bytes((ushort)0x4000, (ushort)0xc000));
        var color = LinearRequest(address + 0x2000, 4, Format.R16G16Unorm, GuestPixelFormat.Bits16_16UNorm, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        var colorId = harness.Acquire(ref color);
        harness.MarkGpuWritten(colorId);
        Assert.True(harness.WriteFault(address + 0x2000));
        harness.Write(address + 0x2000, Bytes((ushort)0x2000, (ushort)0xe000));

        var stencilAddress = address + 0x80000;
        const ulong stencilSize = 0x10000;
        harness.Worker.Run(() =>
        {
            Assert.NotNull(harness.Cache.ObtainBuffer(stencilAddress, stencilSize, isWritten: false).Buffer);
            harness.Cache.FillBuffer(stencilAddress, stencilSize, 0x41414141u, isGds: false);
        });

        var depth = AsDepthTarget(color, Format.D24UnormS8Uint);
        depth.Description.Stencil = new GuestSpan(stencilAddress, stencilSize);
        depth.Description.GuestFormat = GuestPixelFormat.Bits16UNorm;
        depth.Description.BytesPerBlock = 2;
        depth.Description.Samples = 2;
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var depthId = harness.Find(ref depth);
        var htileAddress = address + 0x90000;
        depth.Description.Metadata.Range = new GuestSpan(htileAddress, 0x10000);
        depth.Description.Metadata.Kind = MetadataKind.Htile;
        var depthWithHtileId = harness.Find(ref depth);
        var depthLocal = depth;
        var depthView = harness.Worker.Run(() => harness.Images.AcquireDepthTargetView(depthWithHtileId, depthLocal));
        var sampledDepth = depth;
        sampledDepth.Role = ImageRole.Texture;
        sampledDepth.Description.Metadata = default;
        sampledDepth.View = sampledDepth.View with { Usage = ImageUsageFlags.SampledBit };
        var sampledDepthId = harness.Find(ref sampledDepth);

        Assert.True(depthId.IsValid);
        Assert.Equal(depthId, depthWithHtileId);
        Assert.Equal(depthId, sampledDepthId);
        Assert.NotEqual(colorId, depthId);
        Assert.NotEqual(0UL, depthView.Handle);
        var depthImage = harness.Image(depthId);
        Assert.Equal(2u, depthImage.Backing.Samples);
        Assert.Equal(ImageLayout.DepthStencilAttachmentOptimal, depthImage.Backing.State.Layout);
        Assert.True(depthImage.IsGpuModified);
        Assert.Equal(0u, depthImage.Description.HtileClearMask);
        Assert.Equal(depth.Description.Metadata.Range, depthImage.Description.Metadata.Range);
        Assert.True(harness.Images.IsMetadata(htileAddress));
        Assert.False(harness.Images.IsMetadataCleared(htileAddress, 0));
        Assert.False(harness.Cache.HasGpuDirtyBytes(stencilAddress, stencilSize));
        Assert.False(harness.Images.QueryRegion(stencilAddress, stencilSize).GpuImageBytes);

        var dataSize = depthImage.Description.Data.Size;
        depthImage.Description.Data = depthImage.Description.Data with { Size = (32UL << 20) + 4 };
        var oversizedRejected = harness.Worker.Run(() => !harness.Images.TryDownloadForTest(depthId));
        depthImage.Description.Data = depthImage.Description.Data with { Size = dataSize };
        Assert.True(oversizedRejected);
        Assert.True(depthImage.IsGpuModified);
        Assert.False(depthImage.IsBufferModified);

        harness.Write(address + 0x7000, Bytes((ushort)0x0000, (ushort)0x4000, (ushort)0x8000, (ushort)0xffff));
        var color4 = LinearRequest(address + 0x7000, 8, Format.R16G16B16A16Unorm, GuestPixelFormat.Bits16_16_16_16UNorm, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 8, 1);
        var color4Id = harness.Find(ref color4);
        var depth4 = AsDepthTarget(color4, Format.D16Unorm);
        depth4.Description.GuestFormat = GuestPixelFormat.Bits16UNorm;
        depth4.Description.BytesPerBlock = 2;
        depth4.Description.Samples = 4;
        var depth4Id = harness.Find(ref depth4);
        Assert.True(depth4Id.IsValid);
        Assert.NotEqual(color4Id, depth4Id);
        Assert.Equal(4u, harness.Image(depth4Id).Backing.Samples);

        var twoSamples = reader.ReadSamples(depthImage, 2);
        var fourSamples = reader.ReadSamples(harness.Image(depth4Id), 4);
        float[] expectedTwo = [0x2000 / 65535.0f, 0xe000 / 65535.0f];
        float[] expectedFour = [0.0f, 0x4000 / 65535.0f, 0x8000 / 65535.0f, 1.0f];
        for (var sample = 0; sample < 2; sample++)
        {
            Assert.InRange(BitConverter.UInt32BitsToSingle(twoSamples[sample]), expectedTwo[sample] - 1.5f / 65535.0f, expectedTwo[sample] + 1.5f / 65535.0f);
        }

        for (var sample = 0; sample < 4; sample++)
        {
            Assert.InRange(BitConverter.UInt32BitsToSingle(fourSamples[sample]), expectedFour[sample] - 1.5f / 65535.0f, expectedFour[sample] + 1.5f / 65535.0f);
        }

        // The stencil address created a lightweight association that cannot download.
        var depthBacking = depthImage.Backing.Handle;
        var firstProxy = harness.ProxyAt(stencilAddress, stencilSize);
        Assert.True(firstProxy.IsValid);
        Assert.NotEqual(depthId, firstProxy);
        Assert.Equal(Format.Undefined, harness.Image(firstProxy).Description.PixelFormat);
        Assert.False(harness.Image(firstProxy).Backing.Exists);
        Assert.Equal(depthId, harness.Image(firstProxy).DepthOwner);
        Assert.False(harness.Worker.Run(() => harness.Images.TryDownloadForTest(firstProxy)));

        var exactAlias = depth;
        exactAlias.Role = ImageRole.Texture;
        exactAlias.Description.Stencil = GuestSpan.Empty;
        exactAlias.Description.PixelFormat = Format.R8G8B8A8Unorm;
        exactAlias.Description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UNorm;
        exactAlias.Description.BytesPerBlock = 4;
        exactAlias.View = exactAlias.View with { Format = Format.R8G8B8A8Unorm, Aspect = ImageAspectFlags.ColorBit, Usage = ImageUsageFlags.SampledBit };
        var exactAliasId = harness.Find(ref exactAlias, exactFormat: true);
        Assert.True(exactAliasId.IsValid);
        Assert.NotEqual(depthId, exactAliasId);
        Assert.True(harness.Images.Contains(depthId));
        Assert.Equal(depthBacking, depthImage.Backing.Handle);
        Assert.Equal(depthId, harness.Image(firstProxy).DepthOwner);

        var secondStencil = address + 0xa0000;
        var switched = depth;
        switched.Description.Stencil = new GuestSpan(secondStencil, stencilSize);
        var switchedId = harness.Find(ref switched);
        harness.Worker.Run(() => harness.Images.AssociateStencilForTest(switchedId, switched.Description.Stencil));
        var secondProxy = harness.ProxyAt(secondStencil, stencilSize);
        Assert.Equal(depthId, switchedId);
        Assert.Equal(depthBacking, harness.Image(switchedId).Backing.Handle);
        Assert.Equal(firstProxy, harness.ProxyAt(stencilAddress, stencilSize));
        Assert.Equal(depthId, harness.Image(firstProxy).DepthOwner);
        Assert.True(secondProxy.IsValid);
        Assert.Equal(depthId, harness.Image(secondProxy).DepthOwner);

        var unrelated = LinearRequest(secondStencil, stencilSize, Format.R32Uint, GuestPixelFormat.Bits32UInt, GuestImageType.Color2D, new Extent3D((uint)(stencilSize / 4), 1, 1), 1, 4, 1);
        var unrelatedId = harness.Find(ref unrelated);
        Assert.True(unrelatedId.IsValid);
        Assert.NotEqual(depthId, unrelatedId);
        Assert.False(harness.Image(unrelatedId).DepthOwner.IsValid);

        harness.Worker.Run(() => harness.ImageStore.Unregister(secondStencil, stencilSize));
        Assert.False(harness.ProxyAt(secondStencil, stencilSize).IsValid);
        Assert.Equal(depthBacking, depthImage.Backing.Handle);
        var reassociatedId = harness.Find(ref switched);
        harness.Worker.Run(() => harness.Images.AssociateStencilForTest(reassociatedId, switched.Description.Stencil));
        var reassociatedProxy = harness.ProxyAt(secondStencil, stencilSize);
        Assert.Equal(depthId, reassociatedId);
        Assert.True(reassociatedProxy.IsValid);
        Assert.Equal(depthId, harness.Image(reassociatedProxy).DepthOwner);
        harness.Worker.Run(() => harness.ImageStore.Unregister(depth.Description.Data.Address, depth.Description.Data.Size));
        Assert.False(harness.ProxyAt(secondStencil, stencilSize).IsValid);
        harness.Shutdown();
    }

    [Fact]
    public void DepthOnlyToCombined_RecreatesDepthAndAssociatesStencil()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x100000, ReadWrite);
        var depthAddress = address + 0x60000;
        var stencilAddress = address + 0x70000;
        harness.Write(depthAddress, Bytes(0.75f));
        var depthOnly = LinearRequest(depthAddress, 0x10000, Format.D32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(1, 1, 1), 1, 4, 1);
        depthOnly = AsDepthTarget(depthOnly, Format.D32Sfloat);
        var depthOnlyId = harness.Find(ref depthOnly);

        var combined = depthOnly;
        combined.Description.Stencil = new GuestSpan(stencilAddress, 0x10000);
        combined.Description.PixelFormat = Format.D32SfloatS8Uint;
        combined.View = combined.View with { Format = Format.D32SfloatS8Uint, Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var combinedId = harness.Find(ref combined);
        harness.Worker.Run(() => harness.Images.AssociateStencilForTest(combinedId, combined.Description.Stencil));
        var proxy = harness.ProxyAt(stencilAddress, 0x10000);
        Assert.NotEqual(depthOnlyId, combinedId);
        Assert.True(proxy.IsValid);
        Assert.Equal(combinedId, harness.Image(proxy).DepthOwner);
        Assert.False(harness.Image(proxy).Backing.Exists);

        // GC under pressure defers the depth plane and retires the proxy with the image.
        if (!harness.Image(combinedId).IsGpuModified)
        {
            harness.MarkGpuWritten(combinedId);
        }

        harness.Write(depthAddress, Bytes(0.125f));
        var tick = harness.Scheduler.CurrentTick;
        harness.Worker.Run(() =>
        {
            harness.Images.SetCollectionThresholds(0, 0, ulong.MaxValue, 81);
            harness.Images.ResetRecency(new[] { combinedId }, 81);
            harness.Images.RunGarbageCollector();
        });
        Assert.False(harness.Images.Contains(combinedId));
        Assert.False(harness.ProxyAt(stencilAddress, 0x10000).IsValid);
        Assert.Equal(tick, harness.Scheduler.CurrentTick);
        Assert.Equal(0.125f, BitConverter.ToSingle(harness.Read(depthAddress, 4)));
        harness.Finish();
        Assert.Equal(0.75f, BitConverter.ToSingle(harness.Read(depthAddress, 4)));
        harness.Shutdown();
    }

    [Fact]
    public void LayeredMippedDepthAlias_UsesTheResourceMaximum()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var layeredAddress = address + 0x14000;
        const ulong guestSize = 0x800;
        float[] values = [0.0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1.0f, 0.0625f];
        var guest = new byte[guestSize];
        var layout = TextureTransferLayout.Compute(GuestPixelFormat.Bits32Float, 2, 2, 2, 2, GuestTileMode.Linear, guestSize, true, false, "test");
        var regions = layout.BuildCopies();
        Assert.Equal(4, regions.Count);
        foreach (var region in regions)
        {
            for (uint y = 0; y < region.ImageExtent.Height; y++)
            {
                for (uint x = 0; x < region.ImageExtent.Width; x++)
                {
                    var logical = region.ImageSubresource.MipLevel == 0 ? region.ImageSubresource.BaseArrayLayer * 4 + y * 2 + x : 8 + region.ImageSubresource.BaseArrayLayer;
                    var byteOffset = region.BufferOffset + ((ulong)y * region.BufferRowLength + x) * 4;
                    Assert.True(byteOffset + 4 <= guestSize);
                    BitConverter.TryWriteBytes(guest.AsSpan((int)byteOffset), values[logical]);
                }
            }
        }

        harness.Write(layeredAddress, guest);
        var color = LinearRequest(layeredAddress, guestSize, Format.R32Sfloat, GuestPixelFormat.Bits32Float, GuestImageType.Color2D, new Extent3D(2, 2, 1), 2, 4, 1);
        color.Description.Resources = new SubresourceCount(2, 2);
        color.Description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 32, Pitch = 2, Height = 2 };
        color.Description.MipLayout[1] = new MipLevelLayout { Offset = 32, Size = 8, Pitch = 1, Height = 1 };
        color.View = color.View with { LevelCount = 2 };
        var colorId = harness.Acquire(ref color);
        harness.MarkGpuWritten(colorId);

        var depth = AsDepthTarget(color, Format.D32Sfloat);
        depth.Description.Resources = new SubresourceCount(1, 4);
        depth.View = depth.View with { LevelCount = 1 };
        var depthId = harness.Find(ref depth);
        var native = harness.Image(depthId);
        Assert.NotEqual(colorId, depthId);
        Assert.Equal(2u, native.Backing.Layers);
        Assert.Equal(2u, native.Backing.MipLevels);
        Assert.Equal(color.Description.Resources, native.Description.Resources);
        Assert.True(native.IsGpuModified);
        _ = harness.Read(layeredAddress, 1);
        Assert.True(harness.Image(depthId).IsGpuModified);

        var bytes = harness.Worker.Run(() =>
        {
            using var download = new GpuBuffer(harness.Vulkan.DeviceInfo, harness.Scheduler, GpuBufferUsage.Download, 0, GpuBuffer.AllFlags, 40);
            var copies = new[]
            {
                new BufferImageCopy { BufferOffset = 0, ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 0, 0, 2), ImageExtent = new Extent3D(2, 2, 1) },
                new BufferImageCopy { BufferOffset = 32, ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.DepthBit, 1, 0, 2), ImageExtent = new Extent3D(1, 1, 1) },
            };
            native.DownloadToBuffer(copies, download.Handle, 0, 40);
            harness.Scheduler.Finish();
            download.Invalidate(0, 40);
            return download.Mapped.ToArray();
        });
        for (var index = 0; index < values.Length; index++)
        {
            Assert.Equal(values[index], BitConverter.ToSingle(bytes, index * 4));
        }

        harness.Shutdown();
    }

    [Fact]
    public void D16Fallback_CopiesDepthIntoStorageColor()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x10000, ReadWrite);
        var depthAddress = address + 0xc000;
        var stencilAddress = address + 0xd000;
        harness.Write(depthAddress, Bytes((ushort)0x0000, (ushort)0x2468, (ushort)0xabcd, (ushort)0xffff));
        harness.Write(stencilAddress, [0x6d, 0x6d, 0x6d, 0x6d]);
        var depth = LinearRequest(depthAddress, 8, Format.D24UnormS8Uint, GuestPixelFormat.Bits16UNorm, GuestImageType.Color2D, new Extent3D(4, 1, 1), 1, 2, 1);
        depth = AsDepthTarget(depth, Format.D24UnormS8Uint);
        depth.Description.Stencil = new GuestSpan(stencilAddress, 4);
        depth.View = depth.View with { Aspect = ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit };
        var depthId = harness.Acquire(ref depth);
        harness.MarkGpuWritten(depthId);

        var storage = AsStorage(depth);
        storage.Description.Stencil = GuestSpan.Empty;
        storage.Description.PixelFormat = Format.R16Unorm;
        storage.View = storage.View with { Format = Format.R16Unorm, Aspect = ImageAspectFlags.ColorBit };
        var storageId = harness.Find(ref storage, exactFormat: true);
        Assert.NotEqual(depthId, storageId);
        Assert.True(harness.Image(storageId).IsGpuModified);
        Assert.Equal(Bytes((ushort)0x0000, (ushort)0x2468, (ushort)0xabcd, (ushort)0xffff), harness.ReadImageBytes(harness.Image(storageId)));
        harness.Shutdown();
    }
}
