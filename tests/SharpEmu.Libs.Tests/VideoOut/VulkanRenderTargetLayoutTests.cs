// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanRenderTargetLayoutTests
{
    [Fact]
    public void MrtRenderPassKeyIncludesLoadAndDepthState()
    {
        Format[] formats = [Format.R8G8B8A8Unorm, Format.R16G16B16A16Sfloat];
        var colorLoad = VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
            formats, [true, false], false, false);

        Assert.Equal(
            colorLoad,
            VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
                formats, [true, false], false, true));
        Assert.NotEqual(
            colorLoad,
            VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
                formats, [false, true], false, false));
        Assert.NotEqual(
            colorLoad,
            VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
                [Format.R16G16B16A16Sfloat, Format.R8G8B8A8Unorm],
                [true, false],
                false,
                false));
        Assert.NotEqual(
            colorLoad,
            VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
                [Format.R8G8B8A8Unorm], [true], false, false));
        Assert.NotEqual(
            colorLoad,
            VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
                formats, [true, false], true, false));
        Assert.NotEqual(
            VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
                formats, [true, false], true, false),
            VulkanVideoPresenter.GetMrtRenderPassLayoutKey(
                formats, [true, false], true, true));
    }
}
