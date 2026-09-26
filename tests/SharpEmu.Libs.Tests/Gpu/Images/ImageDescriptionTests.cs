// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

[Collection(SchedulingStateCollection.Name)]
public sealed class ImageDescriptionTests
{
    private const ulong Base = 0x10000;

    private static ImageDescription Color(ulong address, ulong size, uint width, uint height, uint levels = 1, uint layers = 1)
    {
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(address, size);
        description.PixelFormat = Format.R8G8B8A8Unorm;
        description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UNorm;
        description.Extent = new Extent3D(width, height, 1);
        description.Resources = new SubresourceCount(levels, layers);
        description.Pitch = width;
        description.BytesPerBlock = 4;
        description.MipLayout[0] = new MipLevelLayout { Size = size, Pitch = width, Height = height };
        return description;
    }

    private static ImageDescription MipContainer()
    {
        var container = Color(Base, 21504, 64, 64, levels: 3);
        container.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 16384, Pitch = 64, Height = 64 };
        container.MipLayout[1] = new MipLevelLayout { Offset = 16384, Size = 4096, Pitch = 32, Height = 32 };
        container.MipLayout[2] = new MipLevelLayout { Offset = 20480, Size = 1024, Pitch = 16, Height = 16 };
        return container;
    }

    [Fact]
    public void FindMatchingMipLevel_FindsTheLevelByAddressAndExtent()
    {
        var container = MipContainer();
        Assert.Equal(1, Color(Base + 16384, 4096, 32, 32).FindMatchingMipLevel(container));
        Assert.Equal(2, Color(Base + 20480, 1024, 16, 16).FindMatchingMipLevel(container));
        Assert.Equal(-1, Color(Base, 16384, 32, 32).FindMatchingMipLevel(container));
        Assert.Equal(-1, Color(Base + 16384, 4096, 32, 32, levels: 2).FindMatchingMipLevel(container));
        Assert.Equal(-1, Color(Base + 16384 + 4, 4096, 32, 32).FindMatchingMipLevel(container));

        var sampled = Color(Base + 16384, 4096, 32, 32);
        sampled.Samples = 2;
        Assert.Equal(-1, sampled.FindMatchingMipLevel(container));
        var tiled = Color(Base + 16384, 4096, 32, 32);
        tiled.TileMode = GuestTileMode.Standard64KB;
        Assert.Equal(-1, tiled.FindMatchingMipLevel(container));
    }

    [Fact]
    public void FindMatchingMipLevel_AcceptsArraySlicesAndVolumeSlices()
    {
        var layered = Color(Base, 32768, 64, 64, layers: 2);
        layered.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 32768, Pitch = 64, Height = 64 };
        Assert.Equal(0, Color(Base + 16384, 16384, 64, 64).FindMatchingMipLevel(layered));
        Assert.Equal(1, Color(Base + 16384, 16384, 64, 64).FindMatchingArraySlice(layered, 0));
        Assert.Equal(0, Color(Base, 16384, 64, 64).FindMatchingArraySlice(layered, 0));
        Assert.Equal(-1, Color(Base + 16384, 16384, 64, 64).FindMatchingArraySlice(layered, 1));
        Assert.Equal(-1, Color(Base + 16384, 12000, 64, 64).FindMatchingArraySlice(layered, 0));
        Assert.Equal(-1, Color(Base - 16384, 16384, 64, 64).FindMatchingArraySlice(layered, 0));

        var volume = Color(Base, 32768, 32, 32);
        volume.Type = GuestImageType.Color3D;
        volume.Extent = new Extent3D(32, 32, 8);
        volume.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 32768, Pitch = 32, Height = 32 };
        Assert.Equal(-1, Color(Base, 32768, 32, 32, layers: 8).FindMatchingMipLevel(volume));
        Assert.Equal(-1, Color(Base, 32768, 32, 32, layers: 4).FindMatchingMipLevel(volume));
    }

    [Fact]
    public void FindMatchingArraySlice_UsesTheFullMipChainStrideForEachArrayLayer()
    {
        const ulong sliceStride = 21504;
        var container = Color(Base, 2 * sliceStride, 64, 64, levels: 3, layers: 2);
        container.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 2 * 16384, Pitch = 64, Height = 64 };
        container.MipLayout[1] = new MipLevelLayout { Offset = 16384, Size = 2 * 4096, Pitch = 32, Height = 32 };
        container.MipLayout[2] = new MipLevelLayout { Offset = 20480, Size = 2 * 1024, Pitch = 16, Height = 16 };

        var secondLayerMip = Color(Base + sliceStride + 16384, 4096, 32, 32);
        Assert.Equal(1, secondLayerMip.FindMatchingMipLevel(container));
        Assert.Equal(1, secondLayerMip.FindMatchingArraySlice(container, 1));
    }

    [Fact]
    public void DepthRules_ResolveAttachmentAndTransferFormats()
    {
        Assert.Equal(Format.D16Unorm, DepthFormatRule.Find(GuestDepthFormat.Z16)!.AttachmentFormat(false));
        Assert.Equal(Format.D16UnormS8Uint, DepthFormatRule.Find(GuestDepthFormat.Z16)!.AttachmentFormat(true));
        Assert.Equal(Format.D32SfloatS8Uint, DepthFormatRule.AttachmentFormat(GuestDepthFormat.Z32Float, GuestStencilFormat.Stencil8UInt));
        Assert.Equal(Format.Undefined, DepthFormatRule.AttachmentFormat(GuestDepthFormat.Z16, (GuestStencilFormat)5));
        Assert.Equal(Format.Undefined, DepthFormatRule.AttachmentFormat(GuestDepthFormat.Invalid, GuestStencilFormat.Invalid));
        Assert.True(DepthFormatRule.Find(GuestDepthFormat.Z16)!.IsStencilAttachmentFormat(Format.D24UnormS8Uint));
        Assert.False(DepthFormatRule.Find(GuestDepthFormat.Z32Float)!.IsStencilAttachmentFormat(Format.D24UnormS8Uint));
        Assert.Equal(GuestPixelFormat.Bits32Float, DepthFormatRule.FindByGuestFormat(GuestPixelFormat.Bits32Float)!.GuestFormat);
        Assert.Equal(Format.X8D24UnormPack32, DepthFormatRule.AspectTransferFormat(Format.D24UnormS8Uint));
        Assert.Equal(Format.Undefined, DepthFormatRule.AspectTransferFormat(Format.R32Sfloat));
        Assert.Equal(2u, DepthFormatRule.AspectTransferBytes(Format.D16UnormS8Uint));
        Assert.Equal(4u, DepthFormatRule.AspectTransferBytes(Format.D32Sfloat));
        Assert.Equal(0u, DepthFormatRule.AspectTransferBytes(Format.R8Unorm));
        Assert.Equal(0xffffffu, DepthFormatRule.EncodeD16AsD24(0xffff));
        Assert.Equal(0u, DepthFormatRule.EncodeD16AsD24(0));
        Assert.Equal(0x3f800000u, DepthFormatRule.EncodeD16AsD32(0xffff));
        Assert.Equal(0u, DepthFormatRule.EncodeD16AsD32(0));
    }

    [Fact]
    public void DisplayFormats_DecodeTheSixPixelFormats()
    {
        Assert.True(DisplayFormatRule.TryDecode(0x8000000022000000, out var rgba));
        Assert.Equal((Format.R8G8B8A8Srgb, GuestPixelFormat.Bits8_8_8_8Srgb, 4u, false), (rgba.HostFormat, rgba.GuestFormat, rgba.BytesPerElement, rgba.Bgra16));
        Assert.True(DisplayFormatRule.TryDecode(0xc001000600000000, out var bgra16));
        Assert.True(bgra16.Bgra16);
        Assert.Equal(8u, bgra16.BytesPerElement);
        Assert.False(DisplayFormatRule.TryDecode(1, out _));

        var description = ImageDescription.Create();
        description.PixelFormat = Format.R16G16B16A16Sfloat;
        description.GuestFormat = GuestPixelFormat.Bits16_16_16_16Float;
        description.BytesPerBlock = 8;
        description.Bgra16 = true;
        Assert.True(DisplayFormatRule.Supports(description));
        description.Bgra16 = false;
        Assert.True(DisplayFormatRule.Supports(description));
        description.PixelFormat = Format.R8G8B8A8Unorm;
        Assert.False(DisplayFormatRule.Supports(description));
        Assert.True(ImageDescription.IsSupportedDisplayRenderTargetTileMode(GuestTileMode.RenderTarget));
        Assert.False(ImageDescription.IsSupportedDisplayRenderTargetTileMode(GuestTileMode.Linear));
    }

    [Fact]
    public void DisplayCompression_ClassifiesTheControlWord()
    {
        Assert.Equal(DisplayCompression.Uncompressed, ImageDescription.ClassifyDisplayCompression(false, 0, 0, 0));
        Assert.Equal(DisplayCompression.Unsupported, ImageDescription.ClassifyDisplayCompression(false, 0x100, 0, 0));
        Assert.Equal(DisplayCompression.Unsupported, ImageDescription.ClassifyDisplayCompression(true, 0, 0x48, 0));
        Assert.Equal(DisplayCompression.Dcc256_256_0, ImageDescription.ClassifyDisplayCompression(true, 0x20000, 0x48, 0));
        Assert.Equal(DisplayCompression.Dcc256_64_64, ImageDescription.ClassifyDisplayCompression(true, 0x20000, 0x208, 0));
        Assert.Equal(DisplayCompression.Unsupported, ImageDescription.ClassifyDisplayCompression(true, 0x20001, 0x48, 0));
        Assert.Equal(DisplayCompression.Unsupported, ImageDescription.ClassifyDisplayCompression(true, 0x20000, 0x49, 0));
        Assert.Equal(DisplayCompression.Unsupported, ImageDescription.ClassifyDisplayCompression(true, 0x20000, 0x48, 1));
        Assert.True(ImageDescription.CanUseDisplayNativeWithoutUpload(DisplayCompression.Dcc256_256_0, renderTarget: false, gpuModified: true, guestModified: false));
        Assert.True(ImageDescription.CanUseDisplayNativeWithoutUpload(DisplayCompression.Dcc256_64_64, renderTarget: true, gpuModified: false, guestModified: false));
        Assert.False(ImageDescription.CanUseDisplayNativeWithoutUpload(DisplayCompression.Dcc256_256_0, renderTarget: false, gpuModified: false, guestModified: false));
        Assert.False(ImageDescription.CanUseDisplayNativeWithoutUpload(DisplayCompression.Dcc256_256_0, renderTarget: true, gpuModified: true, guestModified: true));
        Assert.False(ImageDescription.CanUseDisplayNativeWithoutUpload(DisplayCompression.Uncompressed, renderTarget: true, gpuModified: true, guestModified: false));
        Assert.False(ImageDescription.CanUseDisplayNativeWithoutUpload(DisplayCompression.Unsupported, renderTarget: true, gpuModified: true, guestModified: false));
    }

    [Fact]
    public void PackedClears_DecodePerFormat()
    {
        Assert.True(PackedClearValue.TryDecodeColor(Format.R8G8B8A8Unorm, 0xff804000, out var rgba));
        Assert.Equal((0.0f, 64 / 255.0f, 128 / 255.0f, 1.0f), (rgba.Float32_0, rgba.Float32_1, rgba.Float32_2, rgba.Float32_3));
        Assert.True(PackedClearValue.TryDecodeColor(Format.B8G8R8A8Unorm, 0xff804000, out var bgra));
        Assert.Equal((128 / 255.0f, 64 / 255.0f, 0.0f, 1.0f), (bgra.Float32_0, bgra.Float32_1, bgra.Float32_2, bgra.Float32_3));
        Assert.True(PackedClearValue.TryDecodeColor(Format.R8G8B8A8Srgb, 0x000000ff, out var srgb));
        Assert.Equal(1.0f, srgb.Float32_0);
        Assert.Equal(0.0f, srgb.Float32_3);
        Assert.True(PackedClearValue.TryDecodeColor(Format.R32Uint, 0xdeadbeef, out var raw));
        Assert.Equal(0xdeadbeefu, raw.Uint32_0);
        Assert.True(PackedClearValue.TryDecodeColor(Format.R32Sfloat, BitConverter.SingleToUInt32Bits(0.25f), out var single));
        Assert.Equal(0.25f, single.Float32_0);
        Assert.True(PackedClearValue.TryDecodeColor(Format.A2B10G10R10UnormPack32, 0xC00003ff, out var packed));
        Assert.Equal((1.0f, 0.0f, 0.0f, 1.0f), (packed.Float32_0, packed.Float32_1, packed.Float32_2, packed.Float32_3));
        Assert.False(PackedClearValue.TryDecodeColor(Format.R16G16B16A16Sfloat, 0, out _));

        Assert.True(PackedClearValue.TryDecodeStencil(0x05050505, out var stencil));
        Assert.Equal(5, stencil);
        Assert.False(PackedClearValue.TryDecodeStencil(0x00000005, out _));
        Assert.True(PackedClearValue.TryDecodeDepth(Format.D32Sfloat, BitConverter.SingleToUInt32Bits(0.5f), out var depth));
        Assert.Equal(0.5f, depth);
        Assert.False(PackedClearValue.TryDecodeDepth(Format.D16Unorm, 0, out _));
        Assert.False(PackedClearValue.TryDecodeDepth(Format.D32Sfloat, BitConverter.SingleToUInt32Bits(2.0f), out _));
        Assert.False(PackedClearValue.TryDecodeDepth(Format.D32SfloatS8Uint, BitConverter.SingleToUInt32Bits(float.NaN), out _));
    }

    [Fact]
    public void Overlaps_AreByteExactOrPageBased()
    {
        Assert.True(GuestRangeOverlap.Bytes(0x1000, 0x100, 0x10ff, 1));
        Assert.False(GuestRangeOverlap.Bytes(0x1000, 0x100, 0x1100, 1));
        Assert.True(GuestRangeOverlap.Pages(0x1000, 0x100, 0x1f00, 0x10));
        Assert.False(GuestRangeOverlap.Pages(0x1000, 0x100, 0x2000, 1));
        Assert.True(GuestRangeOverlap.Bytes(new GuestSpan(0x1000, 0x100), new GuestSpan(0x10ff, 1)));

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => GuestRangeOverlap.Bytes(0x1000, 0, 0x1000, 1));
        Assert.Throws<SchedulerFatalException>(() => GuestRangeOverlap.Pages(ulong.MaxValue, 2, 0x1000, 1));
        Assert.All(fatal.Messages, message => Assert.Contains("overlap range is invalid", message));
    }

    [Fact]
    public void Validate_RejectsInconsistentDescriptions()
    {
        using var fatal = new FatalScope();
        var image = Color(Base, 16384, 64, 64);
        image.Validate();

        static void Reject(ImageDescription description, string fragment, FatalScope scope)
        {
            var before = scope.Messages.Count;
            Assert.Throws<SchedulerFatalException>(description.Validate);
            Assert.Contains(fragment, scope.Messages[before]);
        }

        var noBytes = image;
        noBytes.BytesPerBlock = 0;
        Reject(noBytes, "geometry or format is invalid", fatal);
        var deep = image;
        deep.Extent = new Extent3D(64, 64, 2);
        Reject(deep, "2D image must have a depth of one", fatal);
        var flat1D = image;
        flat1D.Type = GuestImageType.Color1D;
        Reject(flat1D, "1D image must have a height and depth of one", fatal);
        var layeredVolume = image;
        layeredVolume.Type = GuestImageType.Color3D;
        layeredVolume.Resources = new SubresourceCount(1, 2);
        Reject(layeredVolume, "3D image cannot have array layers", fatal);
        var cube = image;
        cube.Type = GuestImageType.Cube;
        Reject(cube, "not a base type", fatal);
        var multisampledMips = image;
        multisampledMips.Samples = 4;
        multisampledMips.Resources = new SubresourceCount(2, 1);
        Reject(multisampledMips, "multisampled image cannot have mip levels", fatal);
        var oddSamples = image;
        oddSamples.Samples = 3;
        Reject(oddSamples, "geometry or format is invalid", fatal);
        var strayMetadata = image;
        strayMetadata.Metadata.Control = 1;
        Reject(strayMetadata, "without metadata carries metadata state", fatal);
        var htile = image;
        htile.Metadata.Kind = MetadataKind.Htile;
        Reject(htile, "HTile metadata is invalid", fatal);
        var dcc = image;
        dcc.Metadata.Kind = MetadataKind.Dcc;
        dcc.Metadata.Range = new GuestSpan(0x2000, 0x100);
        dcc.Metadata.Compression = DisplayCompression.Unsupported;
        Reject(dcc, "DCC metadata is invalid", fatal);
        var stencilCompressed = image;
        stencilCompressed.Metadata.StencilCompressed = true;
        Reject(stencilCompressed, "needs a stencil plane", fatal);
        var badRange = image;
        badRange.Data = new GuestSpan(Base, 0);
        Reject(badRange, "data range is invalid", fatal);

        var association = ImageDescription.Create();
        association.Data = new GuestSpan(Base, 0x100);
        association.Validate();
        association.Pitch = 4;
        Reject(association, "stencil-association image", fatal);
    }

    [Fact]
    public void Predicates_ReadTheDescription()
    {
        var image = Color(Base, 16384, 64, 64);
        Assert.False(image.HasStencil);
        Assert.False(image.IsDepth);
        Assert.False(image.IsBlock);
        Assert.False(image.IsTiled);
        Assert.False(image.IsVolume);
        Assert.Equal(1u, image.TransferLayers);
        image.Stencil = new GuestSpan(Base + 0x8000, 0x1000);
        Assert.True(image.HasStencil);
        image.PixelFormat = Format.D32SfloatS8Uint;
        Assert.True(image.IsDepth);
        Assert.Equal(SampleCountFlags.Count4Bit, ImageDescription.VulkanSampleCount(4));
        Assert.Equal((SampleCountFlags)0, ImageDescription.VulkanSampleCount(16));
        Assert.Equal(GuestPixelFormat.Bits32Float, ImageDescription.RenderTargetTransferFormat(4));
        Assert.Equal(GuestPixelFormat.Bits32_32_32_32Float, ImageDescription.RenderTargetTransferFormat(16));
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageDescription.RenderTargetTransferFormat(3));
        Assert.Contains(fatal.Messages, message => message.Contains("bytes=3"));
    }
}
