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
    // 64x64 BC1: 16x16 blocks of 8 bytes. A compute shader writes the blocks through a 16x16 R16G16B16A16 view of the same memory.
    private const ulong BlockImageBytes = 64 * 64 / 2;

    private static ImageRequest CompressedTexture(ulong address) =>
        LinearRequest(address, BlockImageBytes, Format.BC1RgbaSrgbBlock, GuestPixelFormat.Bc1Srgb, GuestImageType.Color2D, new Extent3D(64, 64, 1), 1, 8, 1);

    private static ImageRequest BlockAliasStorage(ulong address) =>
        AsStorage(LinearRequest(address, BlockImageBytes, Format.R16G16B16A16Uint, GuestPixelFormat.Bits16_16_16_16UInt, GuestImageType.Color2D, new Extent3D(16, 16, 1), 1, 8, 1));

    private const uint BlockArrayLayers = 4;

    private static ImageRequest CompressedArray(ulong address) =>
        LinearRequest(address, BlockImageBytes * BlockArrayLayers, Format.BC1RgbaSrgbBlock, GuestPixelFormat.Bc1Srgb, GuestImageType.Color2D,
            new Extent3D(64, 64, 1), BlockArrayLayers, 8, 1);

    // A shader reads the blocks of the array through an uncompressed view of the given layers (a copy of streamed mips).
    private static ImageRequest BlockAliasArray(ulong address, uint viewLayers)
    {
        var request = LinearRequest(address, BlockImageBytes * BlockArrayLayers, Format.R16G16B16A16Uint, GuestPixelFormat.Bits16_16_16_16UInt,
            GuestImageType.Color2D, new Extent3D(16, 16, 1), BlockArrayLayers, 8, 1);
        request.View = request.View with { LayerCount = viewLayers, Type = viewLayers > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D };
        return request;
    }

    // An image the guest has not touched for more than the removal window, nor since the frame ended, may be deleted by an
    // overlapping request (an image used in each of the two frames before still counts as used this frame).
    private static void AgeBeyondTheRemovalWindow(CacheHarness harness, ResourceSlotIdentifier imageIdentifier, int framesEnded = 2)
    {
        for (var pass = 0; pass < 100 && harness.Scheduler.CurrentTick <= 40; pass++)
        {
            harness.Finish();
        }

        Assert.True(harness.Scheduler.CurrentTick > 40);
        harness.Image(imageIdentifier).LastAccessTick = 0;
        for (var frame = 0; frame < framesEnded; frame++)
        {
            harness.Worker.Run(() => harness.Images.RunGarbageCollector());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindImage_ABlockAliasOfAGpuWrittenCompressedImageKeepsItsContents(bool aged)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var compressed = CompressedTexture(address);
        var compressedId = harness.Acquire(ref compressed);
        harness.MarkGpuWritten(compressedId);
        if (aged)
        {
            AgeBeyondTheRemovalWindow(harness, compressedId);
        }

        var alias = BlockAliasStorage(address);
        var aliasId = harness.Find(ref alias);

        // The GPU-written blocks only live in the image: the alias has to start from them and replace the compressed image.
        Assert.Equal(new[] { aliasId }, harness.Images.FindImagesInRangeForTest(address, BlockImageBytes, pageOverlap: false));
        Assert.True(harness.Image(aliasId).IsGpuModified);
        Assert.False(harness.Image(aliasId).IsCpuDirty);
    }

    // Vulkan views the blocks of a compressed image in an uncompressed format one level and one layer at a time
    // (VK_KHR_maintenance2). A view of several layers is undefined: the driver lays its layers out as a one-level
    // image and the shader reads blocks of other layers. The blocks move to an uncompressed image instead.
    [Fact]
    public void FindImage_AnUncompressedViewOfSeveralLayersOfACompressedArrayIsNotAViewOfIt()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var compressed = CompressedArray(address);
        var compressedId = harness.Acquire(ref compressed);
        harness.MarkGpuWritten(compressedId);

        var alias = BlockAliasArray(address, BlockArrayLayers);
        var aliasId = harness.Find(ref alias);

        Assert.False(harness.Image(aliasId).Description.IsBlock);
        Assert.Equal(new[] { aliasId }, harness.Images.FindImagesInRangeForTest(address, BlockImageBytes * BlockArrayLayers, pageOverlap: false));
        Assert.True(harness.Image(aliasId).IsGpuModified);
    }

    [Fact]
    public void FindImage_AnUncompressedViewOfOneLayerOfACompressedImageViewsIt()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var compressed = CompressedArray(address);
        var compressedId = harness.Acquire(ref compressed);

        var alias = BlockAliasArray(address, 1);
        var aliasId = harness.Find(ref alias);

        Assert.Equal(compressedId, aliasId);
        Assert.True(harness.Image(aliasId).Description.IsBlock);
    }

    // A render target whose pixels are as large as the blocks of a compressed texture the guest later puts in the
    // same memory (8 bytes: R16G16B16A16 pixels, BC1 blocks). The texture grows out of the target and takes its uses,
    // but its guest bytes have the texture layout: planning them as a colour target stopped the game.
    [Fact]
    public void FindImage_ACompressedTextureGrownFromAColorTargetUploadsWithTheTextureLayout()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        // Linear rows are 256 bytes: 32 pixels of the 16x16 target, 32 blocks of the 64x64 texture (16 used).
        const ulong paddedBytes = 0x1000;
        var target = AsColorTarget(LinearRequest(address, paddedBytes, Format.R16G16B16A16Uint, GuestPixelFormat.Bits16_16_16_16UInt,
            GuestImageType.Color2D, new Extent3D(16, 16, 1), 1, 8, 1));
        var targetId = harness.Acquire(ref target);
        Assert.True(harness.Image(targetId).Uses.RenderTarget);

        var texture = LinearRequest(address, paddedBytes, Format.BC1RgbaSrgbBlock, GuestPixelFormat.Bc1Srgb, GuestImageType.Color2D,
            new Extent3D(64, 64, 1), 1, 8, 1);
        var textureId = harness.Acquire(ref texture);

        Assert.Equal(new[] { textureId }, harness.Images.FindImagesInRangeForTest(address, paddedBytes, pageOverlap: false));
        Assert.True(harness.Image(textureId).Description.IsBlock);
        Assert.False(harness.Image(textureId).IsCpuDirty);

        // The CPU streams new blocks into the texture; the refresh uploads them with the texture layout too.
        var blocks = Enumerable.Range(0, (int)paddedBytes).Select(index => (byte)(index * 7 + index / 256)).ToArray();
        Assert.True(harness.WriteFault(address));
        harness.Write(address, blocks);
        harness.Worker.Run(() => harness.Images.RefreshImage(textureId));
        Assert.False(harness.Image(textureId).IsCpuDirty);
        var rows = Enumerable.Range(0, 16).SelectMany(row => blocks.Skip(row * 256).Take(16 * 8)).ToArray();
        Assert.Equal(rows, harness.ReadImageBytes(harness.Image(textureId))[..(int)BlockImageBytes]);
        harness.Shutdown();
    }

    [Fact]
    public void FindImage_ACompressedImageSurvivesABlockAliasRoundTrip()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var compressed = CompressedTexture(address);
        var compressedId = harness.Acquire(ref compressed);
        harness.MarkGpuWritten(compressedId);
        AgeBeyondTheRemovalWindow(harness, compressedId);

        var alias = BlockAliasStorage(address);
        var aliasId = harness.Find(ref alias);
        harness.MarkGpuWritten(aliasId);
        AgeBeyondTheRemovalWindow(harness, aliasId);

        var sampled = CompressedTexture(address);
        var sampledId = harness.Find(ref sampled);

        Assert.Equal(new[] { sampledId }, harness.Images.FindImagesInRangeForTest(address, BlockImageBytes, pageOverlap: false));
        Assert.True(harness.Image(sampledId).Description.IsBlock);
        Assert.True(harness.Image(sampledId).IsGpuModified);
    }

    // 64x64 BC1 with its 32x32 level first, as tiled chains place the smaller levels: level 0 starts after it. Linear rows are
    // 256 bytes (32 blocks, 16 used): level 0 takes 16 of them, level 1 8.
    private const ulong PaddedLevelBytes = 0x1000;
    private const ulong PaddedMipBytes = 0x800;

    private static ImageRequest MippedCompressedTexture(ulong address)
    {
        var request = LinearRequest(address, PaddedMipBytes + PaddedLevelBytes, Format.BC1RgbaSrgbBlock, GuestPixelFormat.Bc1Srgb,
            GuestImageType.Color2D, new Extent3D(64, 64, 1), 1, 8, 1);
        request.Description.Resources = new SubresourceCount(2, 1);
        request.Description.MipLayout[0] = new MipLevelLayout { Offset = PaddedMipBytes, Size = PaddedLevelBytes, Pitch = 64, Height = 64 };
        request.Description.MipLayout[1] = new MipLevelLayout { Offset = 0, Size = PaddedMipBytes, Pitch = 32, Height = 32 };
        request.View = request.View with { LevelCount = 2 };
        return request;
    }

    // The memory of a texture the guest gave up now holds a one-level image whose texels are as large as the texture's blocks
    // (a 16x16 R32G32 state where a 64x64 BC1 chain was). Its bytes start where the chain's smaller level was, not where the
    // chain's level 0 is, so the texture is no view of it: the image is its own, made from guest memory.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindImage_AnImageOfTheOtherBlockClassWithItsLevelsElsewhereIsItsOwn(bool aged)
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var texture = MippedCompressedTexture(address);
        var textureId = harness.Acquire(ref texture);
        harness.MarkGpuWritten(textureId);
        if (aged)
        {
            AgeBeyondTheRemovalWindow(harness, textureId);
        }

        var state = Enumerable.Range(0, (int)BlockImageBytes).Select(index => (byte)(index * 13 + 5)).ToArray();
        Assert.True(harness.WriteFault(address));
        harness.Write(address, state);
        var image = LinearRequest(address, BlockImageBytes, Format.R32G32Uint, GuestPixelFormat.Bits32_32UInt, GuestImageType.Color2D,
            new Extent3D(16, 16, 1), 1, 8, 1);
        var imageId = harness.Acquire(ref image);

        Assert.NotEqual(textureId, imageId);
        Assert.False(harness.Image(imageId).Description.IsBlock);
        Assert.Equal(state, harness.ReadImageBytes(harness.Image(imageId)));
        var owners = harness.Images.FindImagesInRangeForTest(address, BlockImageBytes, pageOverlap: false);
        Assert.Contains(imageId, owners);
        Assert.Equal(!aged, owners.Contains(textureId));
        harness.Shutdown();
    }

    // The other way: a compressed chain requested where a one-level image of its block size lies. The chain's level 0 holds the
    // guest bytes after its smaller level, not the image's texels.
    [Fact]
    public void FindImage_ACompressedChainOverAnImageWithItsLevelsElsewhereIsMadeFromGuestMemory()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var harness = new CacheHarness(_vulkan);
        var address = harness.MapBacked(0x20000, ReadWrite);
        var image = LinearRequest(address, BlockImageBytes, Format.R32G32Uint, GuestPixelFormat.Bits32_32UInt, GuestImageType.Color2D,
            new Extent3D(16, 16, 1), 1, 8, 1);
        var imageId = harness.Acquire(ref image);

        var blocks = Enumerable.Range(0, (int)(PaddedMipBytes + PaddedLevelBytes)).Select(index => (byte)(index * 7 + 3)).ToArray();
        Assert.True(harness.WriteFault(address));
        harness.Write(address, blocks);
        var texture = MippedCompressedTexture(address);
        var textureId = harness.Acquire(ref texture);

        Assert.NotEqual(imageId, textureId);
        Assert.True(harness.Image(textureId).Description.IsBlock);
        var rows = Enumerable.Range(0, 16).SelectMany(row => blocks.Skip((int)PaddedMipBytes + row * 256).Take(16 * 8)).ToArray();
        Assert.Equal(rows, harness.ReadImageBytes(harness.Image(textureId))[..(int)BlockImageBytes]);
        harness.Shutdown();
    }
}
