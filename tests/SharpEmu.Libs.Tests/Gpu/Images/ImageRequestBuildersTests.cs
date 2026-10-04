// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// The image requests the presenter builds from raw target registers, descriptor words and display attributes.
[Collection(SchedulingStateCollection.Name)]
public sealed class ImageRequestBuildersTests : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Base = 0x1_0000_0000;

    [Fact]
    public void EightBitUnsignedScaledTextureUsesUnormBackingWithShaderConversion()
    {
        Assert.Equal(ImageNumericClass.Float, GuestImageFormat.SampledNumericClass(GuestImageFormat.Format8Uscaled));
        Assert.Equal(1u, GuestImageFormat.Remap(GuestImageFormat.Format8Uscaled));

        var request = ImageRequestBuilders.Texture(
            RegisterWords.Texture(Base, GuestPixelFormat.Bits8UScaled, 32, 32),
            new ShaderImageShape(false, false, false, false, TextureNumericClass.Float));
        Assert.Equal(Format.R8Unorm, request.Request.Description.PixelFormat);
        Assert.True(request.ExactFormat);

        var twoChannel = ImageRequestBuilders.Texture(
            RegisterWords.Texture(Base, GuestPixelFormat.Bits8_8UScaled, 32, 32),
            new ShaderImageShape(false, false, false, false, TextureNumericClass.Float));
        Assert.Equal(Format.R8G8Unorm, twoChannel.Request.Description.PixelFormat);
        Assert.True(twoChannel.ExactFormat);
    }

    [Fact]
    public void StorageWithoutMipOperandUsesOnlyTheBaseMip()
    {
        var words = RegisterWords.Texture(0x22AC00000, GuestPixelFormat.Bits8_8_8_8UNorm,
            512, 512, baseLevel: 0, lastLevel: 9, maxMip: 8);
        var shape = new ShaderImageShape(false, false, true, false, TextureNumericClass.Float);
        var request = ImageRequestBuilders.Texture(words, shape).Request;
        Assert.Equal(9u, request.Description.Resources.Levels);
        Assert.Equal(0u, request.View.BaseLevel);
        Assert.Equal(1u, request.View.LevelCount);
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(words, shape with { DynamicMip = true }));
    }

    [Fact]
    public void SampledMipTailViewCanExtendPastThePhysicalLevelCount()
    {
        var physicalWords = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            512,
            512,
            tile: GuestTileMode.Standard64KB,
            baseLevel: 0,
            lastLevel: 8,
            maxMip: 8);
        var viewWords = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            512,
            512,
            tile: GuestTileMode.Standard64KB,
            baseLevel: 9,
            lastLevel: 9,
            maxMip: 8);

        var physical = ImageRequestBuilders.Texture(physicalWords, Sampled2D).Request;
        var view = ImageRequestBuilders.Texture(viewWords, Sampled2D).Request;

        Assert.Equal(physical.Description.Data, view.Description.Data);
        Assert.Equal(10u, view.Description.Resources.Levels);
        Assert.Equal(9u, view.View.BaseLevel);
        Assert.Equal(1u, view.View.LevelCount);
    }

    [Fact]
    public void StandaloneLogicalLastMipRebasesToTheExactPhysicalSubresource()
    {
        var physicalWords = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            1,
            1,
            tile: GuestTileMode.Standard256B);
        var logicalWords = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            1,
            1,
            tile: GuestTileMode.Standard256B,
            baseLevel: 9,
            lastLevel: 9,
            maxMip: 0,
            minLod: 9 * 256);

        var physical = ImageRequestBuilders.Texture(physicalWords, Sampled2D).Request;
        var logical = ImageRequestBuilders.Texture(logicalWords, Sampled2D).Request;
        var storage = ImageRequestBuilders.Texture(
            logicalWords,
            Sampled2D with { Storage = true }).Request;

        Assert.Equal(physical.Description.Data, logical.Description.Data);
        Assert.Equal(physical.Description.Resources, logical.Description.Resources);
        Assert.Equal(physical.Description.MipLayout[0], logical.Description.MipLayout[0]);
        Assert.Equal(0u, logical.View.BaseLevel);
        Assert.Equal(1u, logical.View.LevelCount);
        Assert.Equal(0u, logical.View.MinLod);
        Assert.Equal(physical.Description.Data, storage.Description.Data);
        Assert.Equal(physical.Description.Resources, storage.Description.Resources);
        Assert.Equal(0u, storage.View.BaseLevel);
        Assert.Equal(1u, storage.View.LevelCount);
    }

    [Fact]
    public void StandaloneLogicalMipRebaseRejectsMetadataAndChangedSliceStrides()
    {
        var metadata = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            16,
            1,
            tile: GuestTileMode.Standard256B,
            baseLevel: 9,
            lastLevel: 9,
            maxMip: 4);
        metadata[6] = 1u << 21;

        var layered = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            16,
            1,
            type: GuestImageType.Color2DArray,
            tile: GuestTileMode.Standard256B,
            baseLevel: 9,
            lastLevel: 9,
            maxMip: 4,
            layers: 2);
        var volume = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            1,
            1,
            type: GuestImageType.Color3D,
            tile: GuestTileMode.Standard4KB,
            baseLevel: 9,
            lastLevel: 9,
            maxMip: 4,
            layers: 16);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(metadata, Sampled2D));
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(layered, Sampled2D with { Arrayed = true }));
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(volume, Sampled2D with { Volume = true }));
        Assert.Equal(3, fatal.Messages.Count);
    }

    [Fact]
    public void LinearMipViewCannotExtendPastThePhysicalAllocation()
    {
        var words = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            512,
            512,
            baseLevel: 9,
            lastLevel: 9,
            maxMip: 8);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(words, Sampled2D));
        Assert.Contains("changes the physical layout", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void MipTailViewCannotExceedTheCompleteImageChain()
    {
        var words = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            4,
            4,
            tile: GuestTileMode.Standard64KB,
            baseLevel: 3,
            lastLevel: 3,
            maxMip: 1);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(words, Sampled2D));
        Assert.Contains("exceeds the complete image chain", Assert.Single(fatal.Messages));
    }

    [Theory]
    [InlineData(GuestPixelFormat.Bc1UNorm, 32u, 8u)]
    [InlineData(GuestPixelFormat.Bc3UNorm, 16u, 8u)]
    public void BlockCompressedMipLayoutUsesBlockUnits(
        GuestPixelFormat format,
        uint expectedPitch,
        uint expectedHeight)
    {
        var words = RegisterWords.Texture(
            Base,
            format,
            8,
            8,
            baseLevel: 0,
            lastLevel: 3,
            maxMip: 3);

        var request = ImageRequestBuilders.Texture(words, Sampled2D).Request;

        Assert.Equal(expectedPitch, request.Description.MipLayout[0].Pitch);
        Assert.Equal(expectedHeight, request.Description.MipLayout[0].Height);
    }

    [Fact]
    public void R128TextureIgnoresInactiveDccWords()
    {
        var words = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            64,
            64,
            tile: GuestTileMode.Standard64KB);
        words[6] = 1u << 21;
        words[7] = 1;

        var ordinary = ImageRequestBuilders.Texture(words, Sampled2D).Request;
        var r128 = ImageRequestBuilders.Texture(words, Sampled2D with { R128 = true }).Request;

        Assert.True(ordinary.Description.HasMetadata);
        Assert.False(r128.Description.HasMetadata);
    }
    private static readonly ShaderImageShape Sampled2D = new(false, false, false, false, TextureNumericClass.Float);

    [Theory]
    [InlineData(GuestPixelFormat.Bits16UNorm, Format.D16Unorm, Format.R16Unorm)]
    [InlineData(GuestPixelFormat.Bits32Float, Format.D32Sfloat, Format.R32Sfloat)]
    public void ComparisonTexture_SelectsDepthWithoutChangingTheGuestLayout(
        GuestPixelFormat guestFormat, Format depthFormat, Format colorFormat)
    {
        var words = RegisterWords.Texture(Base, guestFormat, 48, 24);
        var ordinary = ImageRequestBuilders.Texture(words, Sampled2D);
        var comparison = ImageRequestBuilders.Texture(words, Sampled2D with { DepthCompare = true });

        Assert.Equal(colorFormat, ordinary.Request.Description.PixelFormat);
        Assert.False(ordinary.Request.Description.IsDepth);
        Assert.Equal(depthFormat, comparison.Request.Description.PixelFormat);
        Assert.Equal(depthFormat, comparison.Request.View.Format);
        Assert.True(comparison.Request.Description.IsDepth);
        Assert.Equal(ordinary.Request.Description.Data, comparison.Request.Description.Data);
        Assert.Equal(ordinary.Request.Description.Pitch, comparison.Request.Description.Pitch);
        Assert.Equal(ordinary.Request.Description.MipLayout[0], comparison.Request.Description.MipLayout[0]);
        Assert.Equal(ImageRole.Texture, comparison.Request.Role);
    }

    private readonly HeadlessVulkan? _vulkan;

    public ImageRequestBuildersTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    [Fact]
    public void ColorTarget_LinearTargetFromRegisters()
    {
        var resolution = ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 64, 64), 0xF, 0, false);

        Assert.NotNull(resolution);
        var request = resolution.Value.Request;
        Assert.Equal(ImageRole.ColorTarget, request.Role);
        Assert.Equal(Base, request.Description.Data.Address);
        Assert.Equal(16384UL, request.Description.Data.Size);
        Assert.Equal(Format.R8G8B8A8Unorm, request.Description.PixelFormat);
        Assert.Equal(GuestImageType.Color2D, request.Description.Type);
        Assert.Equal(new Extent3D(64, 64, 1), request.Description.Extent);
        Assert.Equal(new SubresourceCount(1, 1), request.Description.Resources);
        Assert.Equal(64u, request.Description.Pitch);
        Assert.Equal(4u, request.Description.BytesPerBlock);
        Assert.Equal(1u, request.Description.Samples);
        Assert.Equal(GuestTileMode.Linear, request.Description.TileMode);
        Assert.Equal(16384UL, request.Description.MipLayout[0].Size);
        Assert.Equal(ImageViewType.Type2D, request.View.Type);
        Assert.Equal(ImageUsageFlags.ColorAttachmentBit, request.View.Usage);
        Assert.Equal(new Extent2D(64, 64), resolution.Value.Extent);
        Assert.Equal(16384UL, resolution.Value.BackingSize);
        Assert.Equal(1u, resolution.Value.Samples);
        Assert.Equal(0u, resolution.Value.BaseMipLevel);
        Assert.False(resolution.Value.MetadataClearSupported);
    }

    [Fact]
    public void ColorTarget_NoTargetWithoutAnAddressOrAWriteMask()
    {
        Assert.Null(ImageRequestBuilders.ColorTarget(RegisterWords.Color(0, 64, 64), 0xF, 0, false));
        Assert.Null(ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 64, 64), 0, 0, false));
        Assert.NotNull(ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 64, 64), 0, 0, ignoreTargetMask: true));
    }

    [Fact]
    public void ColorTarget_MipViewSelectsTheLevelExtent()
    {
        var words = RegisterWords.Color(Base, 64, 64, GuestTileMode.RenderTarget, maxMip: 1, mipLevel: 1);
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false);

        Assert.NotNull(resolution);
        var request = resolution.Value.Request;
        Assert.Equal(2u, request.Description.Resources.Levels);
        Assert.Equal(GuestTileMode.RenderTarget, request.Description.TileMode);
        Assert.True(request.Description.MipLayout[1].Size > 0);
        Assert.Equal(1u, request.View.BaseLevel);
        Assert.Equal(1u, request.View.LevelCount);
        Assert.Equal(new Extent2D(32, 32), resolution.Value.Extent);
        Assert.Equal(1u, resolution.Value.BaseMipLevel);
        Assert.Equal(request.Description.Data.Size, resolution.Value.BackingSize);
    }

    [Fact]
    public void ColorTarget_LayeredViewCoversEveryLayer()
    {
        var resolution = ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 64, 64, sliceMax: 3), 0xF, 0, false);

        Assert.NotNull(resolution);
        var request = resolution.Value.Request;
        Assert.Equal(new SubresourceCount(1, 4), request.Description.Resources);
        Assert.Equal(4 * 16384UL, request.Description.Data.Size);
        Assert.Equal(ImageViewType.Type2DArray, request.View.Type);
        Assert.Equal(0u, request.View.BaseLayer);
        Assert.Equal(4u, request.View.LayerCount);
        Assert.Equal(4 * 16384UL, resolution.Value.BackingSize);
    }

    [Fact]
    public void ColorTarget_VolumeAcceptsExclusiveSliceMax()
    {
        var words = RegisterWords.Color(
            Base,
            64,
            64,
            GuestTileMode.Standard4KB,
            sliceMax: 64,
            dimension: 2,
            depth: 63);

        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false);

        Assert.NotNull(resolution);
        Assert.Equal(64u, resolution.Value.Request.View.LayerCount);
        Assert.Equal(64u, resolution.Value.Request.Description.Extent.Depth);
    }

    [Fact]
    public void ColorTarget_VolumeViewCapsTheExportRangeToTheMipDepth()
    {
        var words = RegisterWords.Color(
            Base, 64, 64, GuestTileMode.Standard4KB,
            sliceMax: 128, dimension: 2, depth: 127);

        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false);

        Assert.NotNull(resolution);
        var request = resolution.Value.Request;
        Assert.Equal(GuestImageType.Color3D, request.Description.Type);
        Assert.Equal(new Extent3D(64, 64, 128), request.Description.Extent);
        Assert.Equal(new SubresourceCount(1, 1), request.Description.Resources);
        Assert.Equal(ImageViewType.Type2DArray, request.View.Type);
        Assert.Equal(0u, request.View.BaseLayer);
        Assert.Equal(128u, request.View.LayerCount);
    }

    [Fact]
    public void ColorTarget_VolumeViewRejectsABaseOutsideTheMipDepth()
    {
        var words = RegisterWords.Color(
            Base, 64, 64, GuestTileMode.Standard4KB,
            sliceStart: 128, sliceMax: 128, dimension: 2, depth: 127);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.ColorTarget(words, 0xF, 0, false));
        Assert.Contains("base=128 last=128", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void ColorTarget_VolumeViewEndingOnePastTheLastSliceIsClamped()
    {
        var resolution = ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 32, 32, GuestTileMode.RenderTarget, sliceMax: 32, dimension: 2, depth: 31), 0xF, 0, false);

        Assert.NotNull(resolution);
        var request = resolution.Value.Request;
        Assert.Equal(32u, request.Description.Extent.Depth);
        Assert.Equal(0u, request.View.BaseLayer);
    }

    [Fact]
    public void ColorTarget_VolumeViewStartingPastTheLastSliceIsFatal()
    {
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.ColorTarget(
            RegisterWords.Color(Base, 32, 32, GuestTileMode.RenderTarget, sliceStart: 32, sliceMax: 32, dimension: 2, depth: 31), 0xF, 0, false));

    }

    [Fact]
    public void ColorTarget_RejectsUnsupportedRegisterStates()
    {
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 64, 64, samplesLog2: 1), 0xF, 0, false));
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 64, 64, maxMip: 1), 0xF, 0, false));
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.ColorTarget(RegisterWords.Color(Base, 64, 64, dimension: 3), 0xF, 0, false));
        Assert.Equal(3, fatal.Messages.Count);
    }

    [Fact]
    public void SampleCount_DecodesTheLog2Encoding()
    {
        Assert.Equal(1u, ImageRequestBuilders.SampleCount(0));
        Assert.Equal(2u, ImageRequestBuilders.SampleCount(1));
        Assert.Equal(8u, ImageRequestBuilders.SampleCount(3));
        Assert.Equal(0u, ImageRequestBuilders.SampleCount(4));
    }

    [Fact]
    public void Texture_LinearTwoDimensionalFromDescriptor()
    {
        var words = RegisterWords.Texture(Base, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64);
        var resolution = ImageRequestBuilders.Texture(words, Sampled2D);

        var request = resolution.Request;
        Assert.Equal(ImageRole.Texture, request.Role);
        Assert.Equal(Base, request.Description.Data.Address);
        Assert.True(request.Description.Data.Size >= 16384UL);
        Assert.Equal(Format.R8G8B8A8Unorm, request.Description.PixelFormat);
        Assert.Equal(GuestPixelFormat.Bits8_8_8_8UNorm, request.Description.GuestFormat);
        Assert.Equal(GuestImageType.Color2D, request.Description.Type);
        Assert.Equal(new Extent3D(64, 64, 1), request.Description.Extent);
        Assert.Equal(new SubresourceCount(1, 1), request.Description.Resources);
        Assert.Equal(4u, request.Description.BytesPerBlock);
        Assert.Equal(1u, request.Description.Samples);
        Assert.Equal(request.Description.Data.Size, request.Description.MipLayout[0].Size);
        Assert.Equal(ImageViewType.Type2D, request.View.Type);
        Assert.Equal(ImageUsageFlags.SampledBit, request.View.Usage);
        Assert.Equal(Format.R8G8B8A8Unorm, request.View.Format);
        Assert.False(resolution.ExactFormat);
        Assert.Equal(Format.R8G8B8A8Unorm, resolution.ViewFormat);
        Assert.Equal(ImageRequestBuilders.DestinationSwizzle(new TextureDescriptorWords(words)), resolution.Swizzle);
    }

    [Fact]
    public void Texture_MipWindowFollowsTheDescriptor()
    {
        var words = RegisterWords.Texture(Base, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, baseLevel: 1, lastLevel: 2, maxMip: 2);
        var request = ImageRequestBuilders.Texture(words, Sampled2D).Request;

        Assert.Equal(3u, request.Description.Resources.Levels);
        Assert.True(request.Description.MipLayout[2].Size > 0);
        Assert.NotEqual(request.Description.MipLayout[1].Offset, request.Description.MipLayout[2].Offset);
        Assert.Equal(1u, request.View.BaseLevel);
        Assert.Equal(2u, request.View.LevelCount);
    }

    [Fact]
    public void Texture_MinimumLodIsRelativeToTheViewAndValidated()
    {
        var words = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            64,
            64,
            baseLevel: 1,
            lastLevel: 3,
            maxMip: 3,
            minLod: 0x180);
        var request = ImageRequestBuilders.Texture(words, Sampled2D).Request;

        Assert.Equal(1u, request.View.BaseLevel);
        Assert.Equal(3u, request.View.LevelCount);
        Assert.Equal(0x80u, request.View.MinLod);

        var belowBase = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            64,
            64,
            baseLevel: 1,
            lastLevel: 3,
            maxMip: 3,
            minLod: 0x80);
        Assert.Equal(0u, ImageRequestBuilders.Texture(belowBase, Sampled2D).Request.View.MinLod);

        using var fatal = new FatalScope();
        var pastLast = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            64,
            64,
            baseLevel: 1,
            lastLevel: 3,
            maxMip: 3,
            minLod: 0x301);
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.Texture(pastLast, Sampled2D));
        Assert.Contains("minLod=769", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void Texture_CubeBecomesALayeredTwoDimensionalImage()
    {
        var words = RegisterWords.Texture(Base, GuestPixelFormat.Bits8_8_8_8UNorm, 32, 32, GuestImageType.Cube, layers: 6, baseArray: 2);
        var arrayed = ImageRequestBuilders.Texture(words, Sampled2D with { Arrayed = true }).Request;
        var single = ImageRequestBuilders.Texture(words, Sampled2D).Request;

        Assert.Equal(GuestImageType.Color2D, arrayed.Description.Type);
        Assert.Equal(new SubresourceCount(1, 6), arrayed.Description.Resources);
        Assert.Equal(ImageViewType.Type2DArray, arrayed.View.Type);
        Assert.Equal(2u, arrayed.View.BaseLayer);
        Assert.Equal(4u, arrayed.View.LayerCount);
        Assert.Equal(ImageViewType.Type2D, single.View.Type);
        Assert.Equal(2u, single.View.BaseLayer);
        Assert.Equal(1u, single.View.LayerCount);
    }

    [Fact]
    public void Texture_OnlyStorageCubeAllowsAPartialFaceWindow()
    {
        var words = RegisterWords.Texture(
            Base,
            GuestPixelFormat.Bits8_8_8_8UNorm,
            32,
            32,
            GuestImageType.Cube,
            layers: 6,
            baseArray: 2);
        var cubeShape = Sampled2D with { Arrayed = true, Cube = true };

        var storage = ImageRequestBuilders.Texture(words, cubeShape with { Storage = true }).Request;
        Assert.Equal(ImageRole.StorageImage, storage.Role);
        Assert.Equal(ImageViewType.Type2DArray, storage.View.Type);
        Assert.Equal(2u, storage.View.BaseLayer);
        Assert.Equal(4u, storage.View.LayerCount);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.Texture(words, cubeShape));
        Assert.Contains("cubemap view is invalid", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void Texture_NullDescriptorBindsAOneTexelImage()
    {
        var words = new uint[8];
        var sampled = ImageRequestBuilders.Texture(words, Sampled2D);
        var storage = ImageRequestBuilders.Texture(words, Sampled2D with { Storage = true, NumericClass = TextureNumericClass.Uint });
        var arrayed = ImageRequestBuilders.Texture(words, Sampled2D with { Arrayed = true });
        var volume = ImageRequestBuilders.Texture(words, Sampled2D with { Volume = true, Storage = true });

        Assert.Equal(ImageRole.Texture, sampled.Request.Role);
        Assert.Equal(new Extent3D(1, 1, 1), sampled.Request.Description.Extent);
        Assert.Equal(Format.R32Sfloat, sampled.Request.Description.PixelFormat);
        Assert.Equal(ImageUsageFlags.SampledBit, sampled.Request.View.Usage);
        Assert.Equal(ImageRole.StorageImage, storage.Request.Role);
        Assert.Equal(Format.R32Uint, storage.Request.Description.PixelFormat);
        Assert.Equal(ImageUsageFlags.StorageBit, storage.Request.View.Usage);
        Assert.Equal(GuestImageType.Color2D, arrayed.Request.Description.Type);
        Assert.Equal(ImageViewType.Type2DArray, arrayed.Request.View.Type);
        Assert.Equal(GuestImageType.Color3D, volume.Request.Description.Type);
        Assert.Equal(ImageViewType.Type3D, volume.Request.View.Type);
        Assert.Equal(ImageUsageFlags.StorageBit, volume.Request.View.Usage);
    }

    [Fact]
    public void Texture_SampledDescriptorMustMatchTheShaderNumericClass()
    {
        var uintWords = RegisterWords.Texture(Base, GuestPixelFormat.Bits8UInt, 64, 64);
        var uintShape = Sampled2D with { NumericClass = TextureNumericClass.Uint };

        Assert.Equal(
            Format.R8Uint,
            ImageRequestBuilders.Texture(uintWords, uintShape).Request.View.Format);

        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(uintWords, Sampled2D));
        Assert.Contains("numeric class", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void Texture_StorageViewOfAnSrgbImageUsesTheLinearFormat()
    {
        var words = RegisterWords.Texture(Base, GuestPixelFormat.Bits8_8_8_8Srgb, 64, 64);
        var resolution = ImageRequestBuilders.Texture(words, Sampled2D with { Storage = true });

        Assert.Equal(ImageRole.StorageImage, resolution.Request.Role);
        Assert.Equal(Format.R8G8B8A8Srgb, resolution.Request.Description.PixelFormat);
        Assert.Equal(Format.R8G8B8A8Unorm, resolution.Request.View.Format);
        Assert.Equal(ImageUsageFlags.StorageBit, resolution.Request.View.Usage);
        Assert.Equal(Format.R8G8B8A8Srgb, resolution.ViewFormat);
    }

    [Fact]
    public void Texture_RejectsAnInvertedMipWindow()
    {
        using var fatal = new FatalScope();
        var words = RegisterWords.Texture(Base, GuestPixelFormat.Bits8_8_8_8UNorm, 64, 64, baseLevel: 2, lastLevel: 1, maxMip: 2);
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.Texture(words, Sampled2D));
        Assert.Contains("baseLevel=2", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void DepthTarget_ResolvesTheDepthOnlyAttachment()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var resolution = ImageRequestBuilders.DepthTarget(RegisterWords.Depth(Base, 64, 64, depthClear: true), _vulkan.DeviceInfo);

        Assert.NotNull(resolution);
        var value = resolution.Value;
        Assert.Equal(Format.D32Sfloat, value.Format);
        Assert.Equal(64u, value.Width);
        Assert.Equal(64u, value.Height);
        Assert.Equal(1u, value.Samples);
        Assert.False(value.HasStencil);
        Assert.False(value.HasHtile);
        Assert.Equal(Base, value.DepthAddress);
        Assert.True(value.DepthSize > 0);
        Assert.Equal(0UL, value.StencilSize);
        Assert.True(value.DepthClearEnabled);
        Assert.False(value.StencilClearEnabled);
        var request = value.Request;
        Assert.Equal(ImageRole.DepthTarget, request.Role);
        Assert.Equal(new GuestSpanCheck(Base, value.DepthSize), new GuestSpanCheck(request.Description.Data.Address, request.Description.Data.Size));
        Assert.Equal(GuestTileMode.Depth, request.Description.TileMode);
        Assert.Equal(value.DepthSize, request.Description.MipLayout[0].Size);
        Assert.Equal(ImageAspectFlags.DepthBit, request.View.Aspect);
        Assert.Equal(ImageUsageFlags.DepthStencilAttachmentBit, request.View.Usage);
    }

    [Fact]
    public void DepthTarget_WithStencilSelectsACombinedFormat()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var words = RegisterWords.Depth(Base, 64, 64, stencilBase: Base + 0x80000);
        var resolution = ImageRequestBuilders.DepthTarget(words, _vulkan.DeviceInfo);

        Assert.NotNull(resolution);
        var value = resolution.Value;
        Assert.Equal(Format.D32SfloatS8Uint, value.Format);
        Assert.True(value.HasStencil);
        Assert.Equal(Base + 0x80000, value.StencilAddress);
        Assert.True(value.StencilSize > 0);
        Assert.Equal(value.StencilSize, value.Request.Description.Stencil.Size);
        Assert.Equal(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, value.Request.View.Aspect);
        Assert.False(value.StencilClearEnabled);
    }

    [Fact]
    public void DepthTarget_CompressedStencilRequiresHtileBacking()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        const ulong stencilBase = Base + 0x80000;
        const ulong htileBase = Base + 0x100000;
        var words = RegisterWords.Depth(Base, 64, 64, stencilBase: stencilBase) with
        {
            ZInfo = (uint)GuestDepthFormat.Z32Float | (1u << 29),
            StencilInfo = 0x00100981,
            HtileBase = htileBase,
        };

        var resolution = ImageRequestBuilders.DepthTarget(words, _vulkan.DeviceInfo);

        Assert.NotNull(resolution);
        var value = resolution.Value;
        Assert.True(value.HasStencil);
        Assert.True(value.HasHtile);
        Assert.Equal(MetadataKind.Htile, value.Request.Description.Metadata.Kind);
        Assert.Equal(htileBase, value.Request.Description.Metadata.Range.Address);
        Assert.True(value.Request.Description.Metadata.StencilCompressed);

        using var fatal = new FatalScope();
        var missingHtile = words with { ZInfo = (uint)GuestDepthFormat.Z32Float, HtileBase = 0 };
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DepthTarget(missingHtile, _vulkan.DeviceInfo));
        Assert.Contains("stencil attachment state", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void DepthTarget_NoAttachmentWhenNothingIsActive()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        Assert.Null(ImageRequestBuilders.DepthTarget(RegisterWords.Depth(Base, 64, 64, depthTest: false, depthWrite: false), _vulkan.DeviceInfo));
        var unbound = new DepthTargetWords(0, 0, 0, 0, false, 0, 0, 2, 0, 0, 0, 0, 0);
        Assert.Null(ImageRequestBuilders.DepthTarget(unbound, _vulkan.DeviceInfo));
    }

    [Fact]
    public void DepthTarget_RejectsAMissingDepthBase()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DepthTarget(RegisterWords.Depth(0, 64, 64), _vulkan.DeviceInfo));
        Assert.Contains("read=0x0000000000000000", Assert.Single(fatal.Messages));
    }

    [Fact]
    public void DisplaySurface_FromRegisteredAttributes()
    {
        var surface = new DisplaySurfaceWords(Base, 0, 0x8000000022000000, 1920, 1080, 0, 0, 0, 0, false);
        var request = ImageRequestBuilders.DisplaySurface(surface);

        Assert.Equal(ImageRole.DisplaySurface, request.Role);
        Assert.Equal(Base, request.Description.Data.Address);
        Assert.Equal(request.Description.MipLayout[0].Size, request.Description.Data.Size);
        Assert.Equal(Format.R8G8B8A8Srgb, request.Description.PixelFormat);
        Assert.Equal(GuestPixelFormat.Bits8_8_8_8Srgb, request.Description.GuestFormat);
        Assert.Equal(new Extent3D(1920, 1080, 1), request.Description.Extent);
        Assert.Equal(GuestTileMode.RenderTarget, request.Description.TileMode);
        Assert.Equal(4u, request.Description.BytesPerBlock);
        Assert.Equal(MetadataKind.None, request.Description.Metadata.Kind);
        Assert.Equal(ImageViewType.Type2D, request.View.Type);
        Assert.Equal(ImageUsageFlags.TransferSrcBit, request.View.Usage);
    }

    [Theory]
    [InlineData(0x8100070422000000UL, Format.A2B10G10R10UnormPack32, 8UL)]
    [InlineData(0x8100070400000000UL, Format.A2R10G10B10UnormPack32, 40UL)]
    [InlineData(0x8100000622000000UL, Format.A2B10G10R10UnormPack32, 32UL)]
    [InlineData(0x8100000600000000UL, Format.A2R10G10B10UnormPack32, 0UL)]
    public void DisplaySurface_Packed10BitFormatsPreserveEncodedValues(ulong pixelFormat, Format expectedFormat, ulong option)
    {
        var surface = new DisplaySurfaceWords(Base, 0, pixelFormat, 3840, 2160, 0, option, 0, 0, false);
        var request = ImageRequestBuilders.DisplaySurface(surface);
        Assert.Equal(expectedFormat, request.Description.PixelFormat);
        Assert.Equal(GuestPixelFormat.Bits10_10_10_2UNorm, request.Description.GuestFormat);
        Assert.Equal(4u, request.Description.BytesPerBlock);
        Assert.Equal(expectedFormat, request.View.Format);
        Assert.True(DisplayFormatRule.Supports(request.Description));
    }

    [Fact]
    public void DisplaySurface_RejectsUnsupportedAttributes()
    {
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DisplaySurface(new DisplaySurfaceWords(Base, 0, 0x8000000022000000, 1920, 1080, 1, 0, 0, 0, false)));
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DisplaySurface(new DisplaySurfaceWords(Base, 0, 0x1234, 1920, 1080, 0, 0, 0, 0, false)));
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DisplaySurface(new DisplaySurfaceWords(Base, 0, 0x8000000022000000, 0, 1080, 0, 0, 0, 0, false)));
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DisplaySurface(new DisplaySurfaceWords(Base, 0, 0x8100070422000000, 1920, 1080, 0, 1, 0, 0, false)));
        Assert.Equal(4, fatal.Messages.Count);
    }

    private readonly record struct GuestSpanCheck(ulong Address, ulong Size);
}
