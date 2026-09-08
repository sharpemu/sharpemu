// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcTextureTransportTests
{
    [Fact]
    public void DescriptorResolvedVolumeShapeProducesACompatibleView()
    {
        uint[] words = [0x01488300, 0xC3800000, 0x0000C000, 0x90900FAC, 0, 0x00700000, 0, 0];
        var control = new Gen5ImageControl(7, 7, [7, 8, 9], 0, 0, 20, 2, false, false, false, false, false);
        var shaderBinding = new Gen5ImageBinding(0x100, "ImageSampleLz", control, words, [], null);
        Assert.False(Gen5ShaderTranslator.IsVolumeImageBinding(shaderBinding));
        const BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        object?[] arguments = [words, null];
        Assert.True((bool)typeof(AgcExports).GetMethod("TryDecodeTextureDescriptor", privateStatic)!
            .Invoke(null, arguments)!);
        var bindingType = typeof(AgcExports).GetNestedType("TranslatedImageBinding", BindingFlags.NonPublic)!;
        var binding = Activator.CreateInstance(bindingType,
            [arguments[1], false, 0u, Array.Empty<uint>(), false, words, false,
                Gen5ShaderTranslator.IsVolumeImageBinding(shaderBinding) ? 2u : 1u]);
        var texture = Assert.IsType<GuestDrawTexture>(typeof(AgcExports)
            .GetMethod("CreateDescriptorDrawTexture", privateStatic)!.Invoke(null, [binding]));
        var request = ImageRequestBuilders.Texture(texture.Descriptor!, texture.Shape).Request;
        Assert.False(texture.Shape.Volume);
        Assert.Equal(GuestImageType.Color2D, request.Description.Type);
        Assert.Equal(ImageViewType.Type2D, request.View.Type);
        Assert.Equal(4u, request.Description.Extent.Width);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(7u, false)]
    [InlineData(8u, true)]
    [InlineData(9u, true)]
    [InlineData(15u, true)]
    public void TextureDecoderRejectsNonImageResourceTypes(uint resourceType, bool expected)
    {
        uint[] words = [0xCD606800, 0x00100045, 0x169, 0x4DFAC | (resourceType << 28),
            0x3F800000, 0, 0, 0];
        object?[] arguments = [words, null];
        var decoder = typeof(AgcExports).GetMethod("TryDecodeTextureDescriptor",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, Assert.IsType<bool>(decoder.Invoke(null, arguments)));
    }

    [Fact]
    public void CreateDescriptorDrawTexture_DoesNotForwardRejectedDescriptorWords()
    {
        uint[] words = [0x3E2199EC, 0x3EB013A3, 0xBE8BF388, 0x3ECC176A,
            0x3E174620, 0x3EB4CEAE, 0xBE8301DA, 0x3ED406CD];
        const BindingFlags privateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        var fallback = typeof(AgcExports).GetMethod("CreateFallbackTextureDescriptor", privateStatic)!
            .Invoke(null, [words, 1u]);
        var bindingType = typeof(AgcExports).GetNestedType("TranslatedImageBinding", BindingFlags.NonPublic)!;
        var binding = Activator.CreateInstance(bindingType,
            [fallback, false, 0u, Array.Empty<uint>(), false, words, false, 1u]);
        var texture = Assert.IsType<GuestDrawTexture>(typeof(AgcExports)
            .GetMethod("CreateDescriptorDrawTexture", privateStatic)!.Invoke(null, [binding]));

        Assert.Equal(0UL, texture.Address);
        Assert.NotNull(texture.Descriptor);
        Assert.Empty(texture.Descriptor);
        _ = ImageRequestBuilders.Texture(texture.Descriptor, texture.Shape);
    }

    [Theory]
    [InlineData(10u, 4u, 4u)]
    [InlineData(10u, 0u, 1u)]
    [InlineData(9u, 4u, 1u)]
    [InlineData(13u, 4u, 1u)]
    public void GetTextureVolumeDepth_OnlyUsesDescriptorDepthFor3D(
        uint type,
        uint descriptorDepth,
        uint expectedDepth)
    {
        Assert.Equal(
            expectedDepth,
            AgcExports.GetTextureVolumeDepth(type, descriptorDepth));
    }

    [Fact]
    public void GetTextureByteCount_MultipliesUncompressedVolumeDepth()
    {
        Assert.Equal(
            4UL * 8 * 6 * 5,
            AgcExports.GetTextureByteCount(
                format: 10,
                width: 8,
                height: 6,
                depth: 5));
    }

    [Fact]
    public void GetTextureByteCount_MultipliesBlockCompressedVolumeDepth()
    {
        // Format 169 uses one eight-byte BC block for each 4x4 texel block.
        Assert.Equal(
            2UL * 2 * 8 * 3,
            AgcExports.GetTextureByteCount(
                format: 169,
                width: 7,
                height: 5,
                depth: 3));
    }

    [Fact]
    public void GetTextureByteCount_LeavesTwoDimensionalSizingUnchanged()
    {
        Assert.Equal(
            AgcExports.GetTextureByteCount(10, 8, 6),
            AgcExports.GetTextureByteCount(10, 8, 6, depth: 1));
        Assert.Equal(
            AgcExports.GetTextureByteCount(10, 8, 6),
            AgcExports.GetTextureByteCount(10, 8, 6, depth: 0));
    }

    [Fact]
    public void GuestDrawTexture_CarriesRawTypeAndNormalizedDepth()
    {
        var texture = new GuestDrawTexture(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            Type: 10,
            Depth: 5);

        Assert.Equal(10u, texture.Type);
        Assert.Equal(5u, texture.Depth);
    }

    [Fact]
    public void TextureContentIdentity_DistinguishesTypeAndDepth()
    {
        var twoDimensional = CreateIdentity(type: 9, depth: 1);
        var threeDimensional = CreateIdentity(type: 10, depth: 1);
        var deeperThreeDimensional = CreateIdentity(type: 10, depth: 5);

        Assert.NotEqual(twoDimensional, threeDimensional);
        Assert.NotEqual(threeDimensional, deeperThreeDimensional);
    }

    private static TextureContentIdentity CreateIdentity(uint type, uint depth) =>
        new(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            DstSelect: 0xFAC,
            TileMode: 0,
            Pitch: 8,
            Type: type,
            Depth: depth);

    [Fact]
    public void TextureContentIdentity_DoesNotContainSamplerState()
    {
        var fields = typeof(TextureContentIdentity)
            .GetProperties()
            .Select(static property => property.Name);

        Assert.DoesNotContain(nameof(GuestDrawTexture.Sampler), fields);
    }

    [Fact]
    public void TextureCacheLookupIdentity_DistinguishesSamplerBindings()
    {
        var content = CreateIdentity(type: 9, depth: 1);
        var first = new TextureCacheLookupIdentity(content, new GuestSampler(1, 2, 3, 4));
        var second = new TextureCacheLookupIdentity(content, new GuestSampler(1, 2, 3, 5));

        Assert.NotEqual(first, second);
        Assert.Equal(first.Content, second.Content);
    }
}
