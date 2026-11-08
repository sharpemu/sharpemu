// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class CachedImageFormatSupportTests
{
    private const ImageCreateFlags OriginalFlags = ImageCreateFlags.CreateMutableFormatBit |
                                                   ImageCreateFlags.CreateExtendedUsageBit |
                                                   ImageCreateFlags.CreateBlockTexelViewCompatibleBit;
    private const ImageUsageFlags SampledUsage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit;

    private sealed class TestImageFormatSupport(bool supportsBlockViews, bool supportsSampling = true, SampleCountFlags supportedSampleCounts = SampleCountFlags.Count1Bit, bool supportsCompressedStorage = true) : IImageFormatSupport
    {
        public int FormatQueryCount { get; private set; }

        public bool TryGetImageFormatProperties(Format format, ImageType type, ImageTiling tiling, ImageUsageFlags usage, ImageCreateFlags flags, out ImageFormatProperties properties)
        {
            FormatQueryCount++;
            properties = new ImageFormatProperties { SampleCounts = supportedSampleCounts };
            return (flags & ImageCreateFlags.CreateBlockTexelViewCompatibleBit) != 0
                ? supportsBlockViews && (supportsCompressedStorage || (usage & ImageUsageFlags.StorageBit) == 0)
                : supportsSampling && (usage & ImageUsageFlags.StorageBit) == 0;
        }
    }

    private static ImageCreateInfo CreateCompressedImageConfiguration(Format format) => new()
    {
        SType = StructureType.ImageCreateInfo,
        Format = format,
        ImageType = ImageType.Type2D,
        Tiling = ImageTiling.Optimal,
        Extent = new Extent3D(512, 256, 1),
        MipLevels = 10,
        ArrayLayers = 1,
        Samples = SampleCountFlags.Count1Bit,
        Usage = SampledUsage | ImageUsageFlags.StorageBit,
        Flags = OriginalFlags,
    };

    [Fact]
    public void SupportedImageConfigurationRemainsUnchanged()
    {
        var device = new TestImageFormatSupport(supportsBlockViews: true);
        var configuration = CreateCompressedImageConfiguration(Format.BC1RgbaUnormBlock);

        Assert.True(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration));
        Assert.Equal(1, device.FormatQueryCount);
        Assert.Equal(OriginalFlags, configuration.Flags);
        Assert.Equal(SampledUsage | ImageUsageFlags.StorageBit, configuration.Usage);
    }

    [Fact]
    public void UnsupportedBlockViewImageIsNotRetriedWithoutBlockViews()
    {
        var device = new TestImageFormatSupport(supportsBlockViews: false);
        var configuration = CreateCompressedImageConfiguration(Format.BC1RgbaUnormBlock);

        Assert.False(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration));
        Assert.Equal(2, device.FormatQueryCount);
        Assert.Equal(OriginalFlags, configuration.Flags);
        Assert.Equal(SampledUsage | ImageUsageFlags.StorageBit, configuration.Usage);
    }

    [Fact]
    public void CompressedImageDropsOnlyStorageWhenDriverRefusesIt()
    {
        var device = new TestImageFormatSupport(supportsBlockViews: true, supportsCompressedStorage: false);
        var configuration = CreateCompressedImageConfiguration(Format.BC5UnormBlock);

        Assert.True(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration));
        Assert.Equal(2, device.FormatQueryCount);
        Assert.Equal(OriginalFlags, configuration.Flags);
        Assert.Equal(SampledUsage, configuration.Usage);
    }

    [Theory]
    [InlineData(Format.BC1RgbaUnormBlock, Format.BC1RgbaSrgbBlock, true)]
    [InlineData(Format.BC7SrgbBlock, Format.BC7UnormBlock, true)]
    [InlineData(Format.BC1RgbaUnormBlock, Format.R32G32Uint, false)]
    [InlineData(Format.BC7SrgbBlock, Format.R32G32B32A32Uint, false)]
    public void FallbackAllowsCompressedViewsAndRejectsUncompressedBlockViews(Format image, Format view, bool allowedWithoutBlockViews)
    {
        Assert.True(ViewFormatRules.AreImageViewFormatsCompatible(image, view, OriginalFlags));
        Assert.Equal(allowedWithoutBlockViews, ViewFormatRules.AreImageViewFormatsCompatible(image, view, OriginalFlags & ~ImageCreateFlags.CreateBlockTexelViewCompatibleBit));
    }
}
