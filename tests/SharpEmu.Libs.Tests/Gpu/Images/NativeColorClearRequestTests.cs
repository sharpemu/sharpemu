// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class NativeColorClearRequestTests
{
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
