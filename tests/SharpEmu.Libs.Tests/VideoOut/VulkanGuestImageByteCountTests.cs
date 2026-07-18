// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageByteCountTests
{
    [Theory]
    [InlineData(10u, 642u, 362u, 929616UL)]
    [InlineData(12u, 642u, 362u, 1859232UL)]
    [InlineData(13u, 2u, 2u, 48UL)]
    public void UsesGuestSurfaceTexelSize(
        uint format,
        uint width,
        uint height,
        ulong expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GetGuestImageByteCount(format, width, height));
    }

    [Theory]
    [InlineData(169u, 4u, 4u, 8UL)]
    [InlineData(173u, 5u, 5u, 64UL)]
    public void UsesCompressedBlockExtent(
        uint format,
        uint width,
        uint height,
        ulong expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.GetGuestImageByteCount(format, width, height));
    }
}
