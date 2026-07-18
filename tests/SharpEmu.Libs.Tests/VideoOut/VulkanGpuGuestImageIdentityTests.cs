// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGpuGuestImageIdentityTests
{
    private const uint GuestFormat = 56;

    [Fact]
    public void ExactShape_WithContainedMipAndArrayView_Matches()
    {
        var identity = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 1920,
            Height: 1080,
            MipLevels: 5,
            ResourceArrayLayers: 64);

        Assert.True(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 1920,
                height: 1080,
                baseMipLevel: 2,
                mipLevels: 3,
                resourceArrayLayers: 64,
                baseArrayLayer: 32,
                viewArrayLayers: 32));
    }

    [Fact]
    public void ScalarImage_DoesNotMatchArrayResourceAtSameAddress()
    {
        var identity = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 32,
            Height: 32,
            MipLevels: 1,
            ResourceArrayLayers: 1);

        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 32,
                height: 32,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 64,
                baseArrayLayer: 0,
                viewArrayLayers: 64));
    }

    [Fact]
    public void DifferentDimensionsOrFormat_DoNotMatch()
    {
        var identity = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 128,
            Height: 64,
            MipLevels: 1,
            ResourceArrayLayers: 1);

        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 64,
                height: 64,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 1,
                baseArrayLayer: 0,
                viewArrayLayers: 1));
        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                guestFormat: GuestFormat + 1,
                width: 128,
                height: 64,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 1,
                baseArrayLayer: 0,
                viewArrayLayers: 1));
    }

    [Fact]
    public void OutOfRangeOrEmptyViews_DoNotMatch()
    {
        var identity = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 128,
            Height: 64,
            MipLevels: 4,
            ResourceArrayLayers: 8);

        Assert.False(
            Matches(identity, baseMipLevel: 3, mipLevels: 2, baseArrayLayer: 0, viewArrayLayers: 1));
        Assert.False(
            Matches(identity, baseMipLevel: 0, mipLevels: 1, baseArrayLayer: 7, viewArrayLayers: 2));
        Assert.False(
            Matches(identity, baseMipLevel: 0, mipLevels: 0, baseArrayLayer: 0, viewArrayLayers: 1));
        Assert.False(
            Matches(identity, baseMipLevel: 0, mipLevels: 1, baseArrayLayer: 0, viewArrayLayers: 0));
    }

    [Fact]
    public void OneMip2DArrayGrowth_CanPreserveExistingLayers()
    {
        var existing = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 16,
            Height: 16,
            MipLevels: 1,
            ResourceArrayLayers: 3);
        var requested = existing with { ResourceArrayLayers = 6 };

        Assert.True(
            VulkanVideoPresenter.IsContentPreservingArrayGrowth(
                existing,
                requested));
    }

    [Fact]
    public void LargerArrayCapacity_ContainsSmallerRequestedResource()
    {
        var identity = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 16,
            Height: 16,
            MipLevels: 1,
            ResourceArrayLayers: 6);

        Assert.True(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 16,
                height: 16,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 2,
                baseArrayLayer: 1,
                viewArrayLayers: 1));
        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 16,
                height: 16,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 7,
                baseArrayLayer: 0,
                viewArrayLayers: 1));
        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 16,
                height: 16,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 2,
                baseArrayLayer: 2,
                viewArrayLayers: 1));
    }

    [Fact]
    public void ArrayGrowthTailRange_SelectsOnlyNewLayers()
    {
        var range = VulkanVideoPresenter.GetArrayGrowthTailRange(
            bytesPerLayer: 1024,
            existingLayers: 1,
            requestedLayers: 6,
            payloadLength: 6144);

        Assert.Equal((1024, 5120), range);
    }

    [Fact]
    public void ArrayGrowthTailRange_RejectsPartialPayload()
    {
        Assert.Throws<InvalidOperationException>(
            () => VulkanVideoPresenter.GetArrayGrowthTailRange(
                bytesPerLayer: 1024,
                existingLayers: 1,
                requestedLayers: 6,
                payloadLength: 5120));
    }

    [Fact]
    public void ImageKindAndDepth_ArePartOfGpuCacheIdentity()
    {
        var volume = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 15,
            Height: 8,
            MipLevels: 1,
            ResourceArrayLayers: 1,
            Depth: 32,
            ImageKind: GuestImageKind.Type3D);

        Assert.True(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                volume,
                GuestFormat,
                width: 15,
                height: 8,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 1,
                baseArrayLayer: 0,
                viewArrayLayers: 1,
                depth: 32,
                imageKind: GuestImageKind.Type3D));
        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                volume,
                GuestFormat,
                width: 15,
                height: 8,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 1,
                baseArrayLayer: 0,
                viewArrayLayers: 1,
                depth: 1,
                imageKind: GuestImageKind.Type2D));
        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                volume,
                GuestFormat,
                width: 15,
                height: 8,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 1,
                baseArrayLayer: 0,
                viewArrayLayers: 1,
                depth: 16,
                imageKind: GuestImageKind.Type3D));
    }

    [Fact]
    public void Square2DArrayProducer_MatchesAlignedCubeView()
    {
        var identity = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 1024,
            Height: 1024,
            MipLevels: 1,
            ResourceArrayLayers: 12,
            ImageKind: GuestImageKind.Type2D);

        Assert.True(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 1024,
                height: 1024,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 12,
                baseArrayLayer: 6,
                viewArrayLayers: 6,
                imageKind: GuestImageKind.Cube));
        Assert.False(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 1024,
                height: 1024,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 12,
                baseArrayLayer: 1,
                viewArrayLayers: 6,
                imageKind: GuestImageKind.Cube));
    }

    [Fact]
    public void CubeProducer_MatchesContained2DArrayView()
    {
        var identity = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 1024,
            Height: 1024,
            MipLevels: 1,
            ResourceArrayLayers: 6,
            ImageKind: GuestImageKind.Cube);

        Assert.True(
            VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
                identity,
                GuestFormat,
                width: 1024,
                height: 1024,
                baseMipLevel: 0,
                mipLevels: 1,
                resourceArrayLayers: 6,
                baseArrayLayer: 3,
                viewArrayLayers: 1,
                imageKind: GuestImageKind.Type2D));
    }

    [Theory]
    [InlineData((int)GuestImageKind.Type2D, 1024u, 1024u, 6u, true)]
    [InlineData((int)GuestImageKind.Type2D, 1024u, 1024u, 12u, true)]
    [InlineData((int)GuestImageKind.Type2D, 1024u, 512u, 6u, false)]
    [InlineData((int)GuestImageKind.Type2D, 1024u, 1024u, 5u, false)]
    [InlineData((int)GuestImageKind.Cube, 1024u, 1024u, 6u, true)]
    [InlineData((int)GuestImageKind.Type3D, 1024u, 1024u, 6u, false)]
    public void CubeCompatibleFlagPolicy_RequiresSquare2DLayerCapacity(
        int imageKindValue,
        uint width,
        uint height,
        uint resourceArrayLayers,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.RequiresCubeCompatibleImageFlag(
                (GuestImageKind)imageKindValue,
                width,
                height,
                resourceArrayLayers));
    }

    [Theory]
    [InlineData(2u, 1u, 4u, (int)GuestImageKind.Type2D)]
    [InlineData(1u, 2u, 4u, (int)GuestImageKind.Type2D)]
    [InlineData(1u, 1u, 3u, (int)GuestImageKind.Type2D)]
    [InlineData(1u, 1u, 6u, (int)GuestImageKind.Type3D)]
    public void IncompatibleArrayChanges_CannotUseGrowthCopy(
        uint existingMips,
        uint requestedMips,
        uint requestedLayers,
        int requestedKindValue)
    {
        var requestedKind = (GuestImageKind)requestedKindValue;
        var existing = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 16,
            Height: 16,
            MipLevels: existingMips,
            ResourceArrayLayers: 4);
        var requested = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 16,
            Height: 16,
            MipLevels: requestedMips,
            ResourceArrayLayers: requestedLayers,
            ImageKind: requestedKind);

        Assert.False(
            VulkanVideoPresenter.IsContentPreservingArrayGrowth(
                existing,
                requested));
    }

    [Fact]
    public void DifferentFormat_CannotUseGrowthCopy()
    {
        var existing = new VulkanGpuGuestImageIdentity(
            GuestFormat,
            Width: 16,
            Height: 16,
            MipLevels: 1,
            ResourceArrayLayers: 1);
        var requested = existing with
        {
            GuestFormat = GuestFormat + 1,
            ResourceArrayLayers = 2,
        };

        Assert.False(
            VulkanVideoPresenter.IsContentPreservingArrayGrowth(
                existing,
                requested));
    }

    private static bool Matches(
        VulkanGpuGuestImageIdentity identity,
        uint baseMipLevel,
        uint mipLevels,
        uint baseArrayLayer,
        uint viewArrayLayers) =>
        VulkanVideoPresenter.IsCompatibleGpuGuestImageIdentity(
            identity,
            GuestFormat,
            width: identity.Width,
            height: identity.Height,
            baseMipLevel,
            mipLevels,
            resourceArrayLayers: identity.ResourceArrayLayers,
            baseArrayLayer,
            viewArrayLayers);
}
