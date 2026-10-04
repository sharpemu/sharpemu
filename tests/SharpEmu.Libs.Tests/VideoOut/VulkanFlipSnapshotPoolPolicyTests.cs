// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanFlipSnapshotPoolPolicyTests
{
    [Fact]
    public void ReusesOnlyTheSameFormatAndExtent()
    {
        Assert.True(VulkanVideoPresenter.IsCompatibleGuestFlipSnapshot(
            Format.R8G8B8A8Unorm,
            3840,
            2160,
            Format.R8G8B8A8Unorm,
            3840,
            2160));
    }

    [Theory]
    [InlineData(1920u, 2160u, Format.R8G8B8A8Unorm)]
    [InlineData(3840u, 1080u, Format.R8G8B8A8Unorm)]
    [InlineData(3840u, 2160u, Format.B8G8R8A8Unorm)]
    public void RejectsAnyIncompatibleImageProperty(
        uint requestedWidth,
        uint requestedHeight,
        Format requestedFormat)
    {
        Assert.False(VulkanVideoPresenter.IsCompatibleGuestFlipSnapshot(
            Format.R8G8B8A8Unorm,
            3840,
            2160,
            requestedFormat,
            requestedWidth,
            requestedHeight));
    }
}
