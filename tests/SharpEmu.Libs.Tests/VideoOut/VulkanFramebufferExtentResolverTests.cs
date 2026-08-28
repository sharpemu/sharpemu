// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanFramebufferExtentResolverTests
{
    [Fact]
    public void UsesSmallestColorAttachmentExtent()
    {
        Extent2D[] colors =
        [
            new(1920, 1080),
            new(1280, 720),
            new(1600, 900),
        ];

        var extent = VulkanFramebufferExtentResolver.Resolve(colors);

        Assert.Equal(1280u, extent.Width);
        Assert.Equal(720u, extent.Height);
    }

    [Fact]
    public void AlsoClampsToDepthAttachmentExtent()
    {
        Extent2D[] colors =
        [
            new(1920, 1080),
            new(1600, 900),
        ];

        var extent = VulkanFramebufferExtentResolver.Resolve(
            colors,
            new Extent2D(1024, 1024));

        Assert.Equal(1024u, extent.Width);
        Assert.Equal(900u, extent.Height);
    }
}
