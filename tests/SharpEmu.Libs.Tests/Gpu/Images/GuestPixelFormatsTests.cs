// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class GuestPixelFormatsTests
{
    [Fact]
    public void ElementSizes_ComeFromTheFormatTable()
    {
        Assert.Equal(4u, GuestPixelFormats.BytesPerElement(GuestPixelFormat.Bits8_8_8_8UNorm));
        Assert.Equal(8u, GuestPixelFormats.BytesPerElement(GuestPixelFormat.Bits16_16_16_16Float));
        Assert.Equal(0u, GuestPixelFormats.BytesPerElement(GuestPixelFormat.Bits8SNorm));
        Assert.Equal(0u, GuestPixelFormats.BytesPerElement(GuestPixelFormat.Invalid));
        Assert.Equal(0u, GuestPixelFormats.BytesPerElement((GuestPixelFormat)200));
        Assert.Equal(0u, GuestPixelFormats.BytesPerElement((GuestPixelFormat)0xFFFF_FFFF));

        Assert.Equal(8u, GuestPixelFormats.BlockCompressedBytes(GuestPixelFormat.Bc1UNorm));
        Assert.Equal(16u, GuestPixelFormats.BlockCompressedBytes(GuestPixelFormat.Bc7UNorm));
        Assert.Equal(0u, GuestPixelFormats.BlockCompressedBytes(GuestPixelFormat.Bits8_8_8_8UNorm));
        Assert.Equal(4u, GuestPixelFormats.RenderTargetBytesPerElement(GuestPixelFormat.Bits8_8_8_8UNorm));
        Assert.Equal(1u, GuestPixelFormats.RenderTargetBytesPerElement(GuestPixelFormat.Bits8SNorm));
    }

    [Fact]
    public void Classes_AndRemaps_FollowTheReferenceRules()
    {
        Assert.True(GuestPixelFormats.IsFmaskFormat(GuestPixelFormat.Fmask8S2F1));
        Assert.True(GuestPixelFormats.IsFmaskFormat(GuestPixelFormat.Fmask64S16F8));
        Assert.False(GuestPixelFormats.IsFmaskFormat(GuestPixelFormat.Bc1UNorm));
        Assert.Equal(TextureNumericClass.Uint, GuestPixelFormats.SampledNumericClass(GuestPixelFormat.Bits8_8_8_8UInt));
        Assert.Equal(TextureNumericClass.Sint, GuestPixelFormats.SampledNumericClass(GuestPixelFormat.Bits8_8_8_8SInt));
        Assert.Equal(TextureNumericClass.Float, GuestPixelFormats.SampledNumericClass(GuestPixelFormat.Bits8_8_8_8UNorm));
        Assert.Equal(TextureNumericClass.Unsupported, GuestPixelFormats.SampledNumericClass(GuestPixelFormat.Bits8SNorm));
        Assert.Equal(GuestPixelFormat.Bits32UInt, GuestPixelFormats.RemapTextureFormat(GuestPixelFormat.Bits11_11_10UInt));
        Assert.Equal(GuestPixelFormat.Bc7Srgb, GuestPixelFormats.RemapTextureFormat(GuestPixelFormat.Bc7Srgb));
        Assert.Equal(Format.R8G8B8A8Unorm, GuestPixelFormats.HostFormat(GuestPixelFormat.Bits8_8_8_8UNorm));
        Assert.Equal(Format.R8G8B8A8Srgb, GuestPixelFormats.HostFormat(GuestPixelFormat.Bits8_8_8_8Srgb));
        Assert.Equal(Format.BC1RgbaUnormBlock, GuestPixelFormats.HostFormat(GuestPixelFormat.Bc1UNorm));
        Assert.Equal(Format.Undefined, GuestPixelFormats.HostFormat(GuestPixelFormat.Invalid));
        Assert.Equal(Format.Undefined, GuestPixelFormats.HostFormat((GuestPixelFormat)0xFFFF_FFFF));
    }

    [Fact]
    public void ComponentMaps_ComposeAndMask()
    {
        Assert.True(ColorComponentMap.Identity.IsIdentity);
        Assert.Equal(new[] { 0u, 1u, 2u, 3u }, Enumerable.Range(0, 4).Select(component => ColorComponentMap.Identity.Map((uint)component)));
        Assert.Equal(new[] { 2u, 1u, 0u, 3u }, Enumerable.Range(0, 4).Select(component => ColorComponentMap.Bgra.Map((uint)component)));
        Assert.Equal(5u, ColorComponentMap.Bgra.Map(5));
        Assert.Equal(ColorComponentMap.Bgra, ColorComponentMap.Identity.Then(ColorComponentMap.Bgra));
        Assert.Equal(ColorComponentMap.Identity, ColorComponentMap.Bgra.Then(ColorComponentMap.Bgra));
        Assert.Equal(0xFu, ColorComponentMap.Identity.ApplyMask(0xF));
        Assert.Equal(0x4u, ColorComponentMap.Bgra.ApplyMask(0x1));
        Assert.Equal(0x1u, ColorComponentMap.Bgra.ApplyMask(0x4));
        Assert.Equal(0xAu, ColorComponentMap.Bgra.ApplyMask(0xA));
    }

    [Fact]
    public void RenderTargetEncodings_ResolveLayoutAndType()
    {
        var unorm = GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits8_8_8_8, ChannelType.UNorm);
        Assert.True(unorm.IsValid);
        Assert.Equal((GuestPixelFormat.Bits8_8_8_8UNorm, (byte)4, ChannelOrderSupport.All), (unorm.Format, unorm.Components, unorm.OrderSupport));
        Assert.Equal(GuestPixelFormat.Bits8_8_8_8Srgb, GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits8_8_8_8, ChannelType.Srgb).Format);
        Assert.Equal(GuestPixelFormat.Bits32Float, GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits32, ChannelType.Float).Format);
        Assert.Equal(GuestPixelFormat.Bits32UInt, GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits32, ChannelType.UInt).Format);
        Assert.False(GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits32, ChannelType.UNorm).IsValid);
        Assert.False(GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits5_6_5, ChannelType.Float).IsValid);
        Assert.False(GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bc1, ChannelType.UNorm).IsValid);

        var packedFloat = GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits10_10_10_2Float, ChannelType.Float);
        Assert.True(packedFloat.IsValid);
        Assert.Equal(ChannelOrderSupport.StandardOnly, packedFloat.OrderSupport);
        Assert.True(packedFloat.SupportsOrder(ChannelOrder.Standard));
        Assert.False(packedFloat.SupportsOrder(ChannelOrder.Alternate));
        Assert.False(GuestPixelFormats.ResolveRenderTargetEncoding(ChannelLayout.Bits10_10_10_2Float, ChannelType.UNorm).IsValid);
    }
}
