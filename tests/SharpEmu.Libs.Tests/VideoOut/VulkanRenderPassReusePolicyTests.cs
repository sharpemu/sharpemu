// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRenderPassReusePolicyTests
{
    private static readonly VulkanRenderPassReuseKey Key =
        new(1, 2, 3, 4, 1920, 1080);

    [Fact]
    public void ReuseIsEnabledByDefault()
    {
        Assert.True(VulkanRenderPassReusePolicy.IsEnabled(null));
        Assert.True(VulkanRenderPassReusePolicy.IsEnabled("0"));
    }

    [Fact]
    public void ExplicitOptOutDisablesReuse()
    {
        Assert.False(VulkanRenderPassReusePolicy.IsEnabled("1"));
    }

    [Fact]
    public void CompatiblePassCanContinue()
    {
        Assert.True(VulkanRenderPassReusePolicy.CanContinue(
            Key,
            Key,
            VulkanRenderPassReuseHazard.None));
    }

    [Fact]
    public void EveryHazardClosesPass()
    {
        VulkanRenderPassReuseHazard[] hazards =
        [
            VulkanRenderPassReuseHazard.MultipleColorTargets,
            VulkanRenderPassReuseHazard.InitialAttachmentLoad,
            VulkanRenderPassReuseHazard.GlobalBufferBarrier,
            VulkanRenderPassReuseHazard.TextureUpload,
            VulkanRenderPassReuseHazard.FeedbackCopy,
            VulkanRenderPassReuseHazard.StorageImage,
            VulkanRenderPassReuseHazard.SampledImageTransition,
        ];

        foreach (var hazard in hazards)
        {
            Assert.False(VulkanRenderPassReusePolicy.CanContinue(Key, Key, hazard));
        }
    }

    [Fact]
    public void DifferentAttachmentIdentityClosesPass()
    {
        var candidate = Key with { Image = 5 };

        Assert.False(VulkanRenderPassReusePolicy.CanContinue(
            Key,
            candidate,
            VulkanRenderPassReuseHazard.None));
    }

    [Fact]
    public void DifferentDepthIdentityClosesPass()
    {
        Assert.False(VulkanRenderPassReusePolicy.CanContinue(
            Key,
            Key with { DepthImage = 5 },
            VulkanRenderPassReuseHazard.None));
    }

    [Fact]
    public void DifferentPassOrFramebufferClosesPass()
    {
        Assert.False(VulkanRenderPassReusePolicy.CanContinue(
            Key,
            Key with { RenderPass = 6 },
            VulkanRenderPassReuseHazard.None));
        Assert.False(VulkanRenderPassReusePolicy.CanContinue(
            Key,
            Key with { Framebuffer = 7 },
            VulkanRenderPassReuseHazard.None));
    }

    [Fact]
    public void DifferentExtentClosesPass()
    {
        Assert.False(VulkanRenderPassReusePolicy.CanContinue(
            Key,
            Key with { Width = 1280 },
            VulkanRenderPassReuseHazard.None));
    }
}
