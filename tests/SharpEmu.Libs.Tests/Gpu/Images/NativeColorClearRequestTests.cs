// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class NativeColorClearRequestTests
{
    [Fact]
    public void CmaskTargetUsesItsOwnMetadataSizeAndPackedClear()
    {
        var words = RegisterWords.Color(0x100000, 3840, 2160, GuestTileMode.RenderTarget,
            sliceStart: 1, sliceMax: 2);
        words = words with { Info = words.Info | (1u << 13), CmaskAddress = 0x4000000, ClearWord0 = 0xff000000 };
        var request = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value.Request;
        Assert.Equal(MetadataKind.Cmask, request.Description.Metadata.Kind);
        Assert.True(request.Description.Metadata.NativeColorClear);
        Assert.Equal(81920UL * 3, request.Description.Metadata.Range.Size);
        Assert.Equal(0x4000000UL, request.Description.Metadata.Range.Address);
        Assert.Equal(1u, request.Description.Metadata.ColorMetadataBaseLayer);
        Assert.True(request.Description.Metadata.PackedColorClearSupported);
        Assert.Equal(1f, request.Description.Metadata.PackedColorClear.Float32_3);

        words = words with { Info = words.Info | (1u << 28), DccAddress = 0x5000000 };
        Assert.Equal(MetadataKind.Dcc, ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value.Request.Description.Metadata.Kind);
    }

    [Theory]
    [InlineData(1u << 14, 0u, 0u)]
    [InlineData(1u << 28, 0u, 0u)]
    [InlineData(0u, 1u << 31, 0u)]
    [InlineData(0u, 0u, 1u)]
    public void CmaskTargetExcludesCompressionVrsAndMultisampling(uint info, uint attrib3, uint samples)
    {
        var words = RegisterWords.Color(0x100000, 64, 64, GuestTileMode.RenderTarget,
            samplesLog2: samples, fragmentsLog2: samples);
        words = words with { Info = words.Info | (1u << 13) | info, Attrib3 = words.Attrib3 | attrib3, CmaskAddress = 0x200000 };
        Assert.False(ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value.Request.Description.Metadata.NativeColorClear);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VolumeDescriptorIncludesEveryMetadataSlice(bool storage, bool alphaOnMostSignificantBits)
    {
        var words = RegisterWords.Texture(0x100000, GuestPixelFormat.Bits16_16_16_16Float,
            120, 68, GuestImageType.Color3D, GuestTileMode.RenderTarget, layers: 64);
        words[6] = (1u << 21) | (alphaOnMostSignificantBits ? 1u << 22 : 0);
        words[7] = 0x20;
        var shape = new ShaderImageShape(Volume: true, Arrayed: false, Cube: false,
            Storage: storage, DynamicMip: false, NumericClass: TextureNumericClass.Float);
        var request = ImageRequestBuilders.Texture(words, shape).Request;
        Assert.Equal(storage ? ImageRole.StorageImage : ImageRole.Texture, request.Role);
        Assert.Equal(MetadataKind.Dcc, request.Description.Metadata.Kind);
        Assert.True(request.Description.Metadata.NativeColorClear);
        Assert.Equal(0x200000UL, request.Description.Metadata.Range.Address);
        Assert.Equal(64UL * 4096, request.Description.Metadata.Range.Size);
        Assert.Equal(!alphaOnMostSignificantBits, request.Description.Metadata.ColorAlphaOnLeastSignificantBits);
        Assert.False(request.Description.Metadata.PackedColorClearSupported);
        Assert.False(ImageRequestBuilders.Texture(words, shape with { R128 = true }).Request.Description.Metadata.NativeColorClear);
        words[6] &= ~(1u << 21);
        Assert.False(ImageRequestBuilders.Texture(words, shape).Request.Description.Metadata.NativeColorClear);
    }

    [Fact]
    public void VolumeTargetMetadataIncludesDepthOutsideItsView()
    {
        var words = RegisterWords.Color(0x100000, 120, 68, GuestTileMode.RenderTarget,
            dimension: 2, depth: 63, sliceStart: 32, sliceMax: 63,
            layout: ChannelLayout.Bits16_16_16_16, type: ChannelType.Float);
        words = words with { Info = words.Info | (1u << 28), DccAddress = 0x200000 };
        var request = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value.Request;
        Assert.True(request.Description.IsVolume);
        Assert.True(request.Description.Metadata.NativeColorClear);
        Assert.Equal(64UL * 4096, request.Description.Metadata.Range.Size);
        Assert.Equal(32u, request.Description.Metadata.ColorMetadataBaseLayer);
        Assert.Equal(32u, request.View.LayerCount);
    }

    [Theory]
    [InlineData(GuestTileMode.RenderTarget, 0, 0, true)]
    [InlineData(GuestTileMode.Linear, 0, 0, false)]
    [InlineData(GuestTileMode.RenderTarget, 1, 0, false)]
    [InlineData(GuestTileMode.RenderTarget, 0, 1, false)]
    public void SelectsOnlySupportedTargetLayouts(GuestTileMode tile, uint maxMip, uint samplesLog2, bool expected)
    {
        var words = RegisterWords.Color(0x100000, 64, 64, tile, maxMip: maxMip,
            sliceMax: 2, sliceStart: 1, samplesLog2: samplesLog2, fragmentsLog2: samplesLog2);
        words = words with { Info = words.Info | (1u << 28), DccAddress = 0x200000 };
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false);
        Assert.NotNull(resolution);
        var metadata = resolution.Value.Request.Description.Metadata;
        Assert.Equal(expected, metadata.NativeColorClear);
        if (expected)
        {
            Assert.Equal(12288UL, metadata.Range.Size);
            Assert.Equal(1u, metadata.ColorMetadataBaseLayer);
            Assert.Equal(2u, resolution.Value.Request.View.LayerCount);
        }
    }
}
