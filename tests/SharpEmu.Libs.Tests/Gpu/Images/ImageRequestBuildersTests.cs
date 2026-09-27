// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
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
    public void StorageWithoutMipOperandUsesTheAvailableMipRange()
    {
        var words = RegisterWords.Texture(0x22AC00000, GuestPixelFormat.Bits8_8_8_8UNorm,
            512, 512, baseLevel: 0, lastLevel: 9, maxMip: 8);
        var shape = new ShaderImageShape(false, false, true, false, TextureNumericClass.Float);
        var request = ImageRequestBuilders.Texture(words, shape).Request;
        Assert.Equal(9u, request.Description.Resources.Levels);
        Assert.Equal(0u, request.View.BaseLevel);
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.Texture(words, shape with { DynamicMip = true }));
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
    public void Texture_NullDescriptorBindsAOneTexelImage()
    {
        var words = new uint[8];
        var sampled = ImageRequestBuilders.Texture(words, Sampled2D);
        var storage = ImageRequestBuilders.Texture(words, Sampled2D with { Storage = true, NumericClass = TextureNumericClass.Uint });

        Assert.Equal(ImageRole.Texture, sampled.Request.Role);
        Assert.Equal(new Extent3D(1, 1, 1), sampled.Request.Description.Extent);
        Assert.Equal(Format.R32Sfloat, sampled.Request.Description.PixelFormat);
        Assert.Equal(ImageUsageFlags.SampledBit, sampled.Request.View.Usage);
        Assert.Equal(ImageRole.StorageImage, storage.Request.Role);
        Assert.Equal(Format.R32Uint, storage.Request.Description.PixelFormat);
        Assert.Equal(ImageUsageFlags.StorageBit, storage.Request.View.Usage);
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

    [Fact]
    public void DisplaySurface_RejectsUnsupportedAttributes()
    {
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DisplaySurface(new DisplaySurfaceWords(Base, 0, 0x8000000022000000, 1920, 1080, 1, 0, 0, 0, false)));
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DisplaySurface(new DisplaySurfaceWords(Base, 0, 0x1234, 1920, 1080, 0, 0, 0, 0, false)));
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DisplaySurface(new DisplaySurfaceWords(Base, 0, 0x8000000022000000, 0, 1080, 0, 0, 0, 0, false)));
        Assert.Equal(3, fatal.Messages.Count);
    }

    private readonly record struct GuestSpanCheck(ulong Address, ulong Size);
}
