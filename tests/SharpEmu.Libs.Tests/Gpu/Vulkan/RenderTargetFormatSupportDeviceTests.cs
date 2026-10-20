// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// Optional Vulkan formats the guest can bind as colour targets: the device must offer them as
// colour attachments that can also be sampled, or the target cannot be created.
public sealed class RenderTargetFormatSupportDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData(ChannelLayout.Bits8, ChannelType.SNorm)]
    [InlineData(ChannelLayout.Bits8, ChannelType.SInt)]
    [InlineData(ChannelLayout.Bits8_8, ChannelType.SNorm)]
    [InlineData(ChannelLayout.Bits8_8, ChannelType.UInt)]
    [InlineData(ChannelLayout.Bits8_8, ChannelType.SInt)]
    [InlineData(ChannelLayout.Bits16_16, ChannelType.SNorm)]
    [InlineData(ChannelLayout.Bits8_8_8_8, ChannelType.SNorm)]
    [InlineData(ChannelLayout.Bits16_16_16_16, ChannelType.SNorm)]
    [InlineData(ChannelLayout.Bits11_11_10, ChannelType.UNorm)]
    [InlineData(ChannelLayout.Bits8, ChannelType.Srgb)]
    [InlineData(ChannelLayout.Bits8_8, ChannelType.Srgb)]
    public void TheHostFormatIsASampledColourAttachment(ChannelLayout layout, ChannelType type)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        var host = TextureTransferLayout.RenderTargetFormat(layout, type, ChannelOrder.Standard).HostFormat;

        vulkan.Vk.GetPhysicalDeviceFormatProperties(vulkan.Physical, host, out var properties);

        const FormatFeatureFlags Required = FormatFeatureFlags.ColorAttachmentBit | FormatFeatureFlags.SampledImageBit |
                                            FormatFeatureFlags.TransferSrcBit | FormatFeatureFlags.TransferDstBit;
        Assert.True(
            (properties.OptimalTilingFeatures & Required) == Required,
            $"{host} for layout={layout} type={type} lacks {Required & ~properties.OptimalTilingFeatures} on this device.");
    }
}
