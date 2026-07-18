// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5UnifiedFormatTests
{
    [Theory]
    [InlineData(22u, 4u, 7u, 4u, 0)]
    [InlineData(29u, 5u, 7u, 4u, 0)]
    [InlineData(36u, 6u, 7u, 4u, 0)]
    [InlineData(56u, 10u, 0u, 4u, 0)]
    [InlineData(62u, 11u, 4u, 8u, 2)]
    [InlineData(64u, 11u, 7u, 8u, 0)]
    [InlineData(69u, 12u, 4u, 8u, 2)]
    [InlineData(71u, 12u, 7u, 8u, 0)]
    public void DecodesArchitecturalUnifiedFormat(
        uint unifiedFormat,
        uint expectedDataFormat,
        uint expectedNumberFormat,
        uint expectedBytesPerTexel,
        int expectedNumericKind)
    {
        Assert.True(
            Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.Equal(expectedDataFormat, decoded.DataFormat);
        Assert.Equal(expectedNumberFormat, decoded.NumberFormat);
        Assert.Equal(expectedBytesPerTexel, decoded.BytesPerTexel);
        Assert.Equal((Gen5FormatNumericKind)expectedNumericKind, decoded.NumericKind);
        Assert.False(decoded.IsBlockCompressed);
    }

    [Theory]
    [InlineData(169u, 8u)]
    [InlineData(170u, 8u)]
    [InlineData(171u, 16u)]
    [InlineData(175u, 8u)]
    [InlineData(176u, 8u)]
    [InlineData(177u, 16u)]
    [InlineData(182u, 16u)]
    public void DecodesCompressedBlockSize(
        uint unifiedFormat,
        uint expectedBytesPerBlock)
    {
        Assert.True(
            Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.True(decoded.IsBlockCompressed);
        Assert.Equal(4u, decoded.BlockWidth);
        Assert.Equal(4u, decoded.BlockHeight);
        Assert.Equal(expectedBytesPerBlock, decoded.BytesPerBlock);
        Assert.Equal(expectedBytesPerBlock, decoded.GetByteCount(4, 4));
        Assert.Equal(expectedBytesPerBlock * 4UL, decoded.GetByteCount(5, 5));
    }

    [Fact]
    public void R32FloatByteCount_DoesNotUseOverlappingNumberBits()
    {
        Assert.True(Gen5UnifiedFormat.TryDecode(22, out var decoded));

        Assert.Equal(8_294_400UL, decoded.GetByteCount(1920, 1080));
    }

    [Fact]
    public void UnknownFormat_IsRejected()
    {
        Assert.False(Gen5UnifiedFormat.TryDecode(0, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(30, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(46, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(47, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(127, out _));
    }

    [Theory]
    [InlineData(1u, Format.R8Unorm)]
    [InlineData(13u, Format.R16Sfloat)]
    [InlineData(22u, Format.R32Sfloat)]
    [InlineData(36u, Format.B10G11R11UfloatPack32)]
    [InlineData(50u, Format.A2B10G10R10UnormPack32)]
    [InlineData(51u, Format.A2B10G10R10SNormPack32)]
    [InlineData(52u, Format.A2B10G10R10UscaledPack32)]
    [InlineData(53u, Format.A2B10G10R10SscaledPack32)]
    [InlineData(54u, Format.A2B10G10R10UintPack32)]
    [InlineData(55u, Format.A2B10G10R10SintPack32)]
    [InlineData(56u, Format.R8G8B8A8Unorm)]
    [InlineData(62u, Format.R32G32Uint)]
    [InlineData(69u, Format.R16G16B16A16Uint)]
    [InlineData(72u, Format.R32G32B32Uint)]
    [InlineData(77u, Format.R32G32B32A32Sfloat)]
    [InlineData(128u, Format.R8Srgb)]
    [InlineData(129u, Format.R8G8Srgb)]
    [InlineData(130u, Format.R8G8B8A8Srgb)]
    [InlineData(132u, Format.E5B9G9R9UfloatPack32)]
    [InlineData(133u, Format.B5G6R5UnormPack16)]
    [InlineData(134u, Format.A1R5G5B5UnormPack16)]
    [InlineData(135u, Format.R5G5B5A1UnormPack16)]
    [InlineData(136u, Format.R4G4B4A4UnormPack16)]
    [InlineData(169u, Format.BC1RgbaUnormBlock)]
    [InlineData(170u, Format.BC1RgbaSrgbBlock)]
    [InlineData(171u, Format.BC2UnormBlock)]
    [InlineData(172u, Format.BC2SrgbBlock)]
    [InlineData(173u, Format.BC3UnormBlock)]
    [InlineData(174u, Format.BC3SrgbBlock)]
    [InlineData(175u, Format.BC4UnormBlock)]
    [InlineData(176u, Format.BC4SNormBlock)]
    [InlineData(177u, Format.BC5UnormBlock)]
    [InlineData(178u, Format.BC5SNormBlock)]
    [InlineData(179u, Format.BC6HUfloatBlock)]
    [InlineData(180u, Format.BC6HSfloatBlock)]
    [InlineData(181u, Format.BC7UnormBlock)]
    [InlineData(182u, Format.BC7SrgbBlock)]
    public void SupportedFormat_HasExactVulkanMapping(
        uint unifiedFormat,
        Format expected)
    {
        Assert.True(Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.True(
            VulkanVideoPresenter.TryGetVulkanTextureFormat(
                decoded.DataFormat,
                decoded.NumberFormat,
                out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(43u)]
    [InlineData(44u)]
    [InlineData(45u)]
    [InlineData(48u)]
    [InlineData(49u)]
    public void PackedFormatWithoutExactVulkanLayout_IsRejected(uint unifiedFormat)
    {
        Assert.True(Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.False(
            VulkanVideoPresenter.TryGetVulkanTextureFormat(
                decoded.DataFormat,
                decoded.NumberFormat,
                out _));
    }

    [Theory]
    [InlineData(36u, Format.B10G11R11UfloatPack32)]
    [InlineData(50u, Format.A2B10G10R10UnormPack32)]
    [InlineData(134u, Format.A1R5G5B5UnormPack16)]
    [InlineData(135u, Format.R5G5B5A1UnormPack16)]
    public void SupportedVertexFormat_HasExactVulkanMapping(
        uint unifiedFormat,
        Format expected)
    {
        Assert.True(Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.True(
            VulkanVideoPresenter.TryGetVulkanVertexFormat(
                decoded.DataFormat,
                decoded.NumberFormat,
                componentCount: 4,
                out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(43u)]
    [InlineData(44u)]
    public void VertexFormatWithoutExactVulkanLayout_IsRejected(uint unifiedFormat)
    {
        Assert.True(Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.False(
            VulkanVideoPresenter.TryGetVulkanVertexFormat(
                decoded.DataFormat,
                decoded.NumberFormat,
                componentCount: 4,
                out _));
    }
}
