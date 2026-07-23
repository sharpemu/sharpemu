// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageTypeTests
{
    [Fact]
    public void ThreeDimensionalDescriptorsMapToVolumeImageAndViewTypes()
    {
        Assert.Equal(
            ImageType.Type3D,
            VulkanVideoPresenter.GetGuestTextureImageType(
                VulkanVideoPresenter.Gen5TextureType3D));
        Assert.Equal(
            ImageViewType.Type3D,
            VulkanVideoPresenter.GetGuestTextureViewType(
                VulkanVideoPresenter.Gen5TextureType3D,
                arrayedView: true));
        Assert.Equal(
            7u,
            VulkanVideoPresenter.GetGuestTextureDepth(
                VulkanVideoPresenter.Gen5TextureType3D,
                7));
    }

    [Fact]
    public void TwoDimensionalArraysKeepLayersSeparateFromImageDepth()
    {
        Assert.Equal(
            ImageType.Type2D,
            VulkanVideoPresenter.GetGuestTextureImageType(
                VulkanVideoPresenter.Gen5TextureType2D));
        Assert.Equal(
            ImageViewType.Type2DArray,
            VulkanVideoPresenter.GetGuestTextureViewType(
                VulkanVideoPresenter.Gen5TextureType2D,
                arrayedView: true));
        Assert.Equal(
            1u,
            VulkanVideoPresenter.GetGuestTextureDepth(
                VulkanVideoPresenter.Gen5TextureType2D,
                7));
    }
}
