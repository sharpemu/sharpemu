// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanStorageResourceShapeTests
{
    private const ulong Address = 0x1234_0000;

    [Fact]
    public void SameAddress2DAnd3DImages_AreRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => VulkanVideoPresenter.ValidateStorageResourceShapes(
            [
                StorageTexture(GuestImageKind.Type2D),
                StorageTexture(GuestImageKind.Type3D, depth: 1),
            ]));

        Assert.Contains("incompatible physical images", error.Message);
        Assert.Contains(nameof(GuestImageKind.Type2D), error.Message);
        Assert.Contains(nameof(GuestImageKind.Type3D), error.Message);
    }

    [Fact]
    public void SameAddress3DImagesWithDifferentDepth_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => VulkanVideoPresenter.ValidateStorageResourceShapes(
            [
                StorageTexture(GuestImageKind.Type3D, depth: 16),
                StorageTexture(GuestImageKind.Type3D, depth: 32),
            ]));
    }

    [Fact]
    public void DifferentScalarViewsOfSamePhysicalArray_AreCompatible()
    {
        VulkanVideoPresenter.ValidateStorageResourceShapes(
        [
            StorageTexture(
                GuestImageKind.Type2D,
                resourceArrayLayers: 8,
                baseArrayLayer: 1),
            StorageTexture(
                GuestImageKind.Type2D,
                resourceArrayLayers: 8,
                baseArrayLayer: 6),
        ]);
    }

    [Fact]
    public void SameAddressArrayBindingsWithIncreasingCapacity_AreCompatible()
    {
        VulkanVideoPresenter.ValidateStorageResourceShapes(
        [
            StorageTexture(
                GuestImageKind.Type2D,
                resourceArrayLayers: 2,
                baseArrayLayer: 1),
            StorageTexture(
                GuestImageKind.Type2D,
                resourceArrayLayers: 6,
                baseArrayLayer: 5),
        ]);
    }

    [Fact]
    public void SameAddressCubeBindingsWithDifferentLayerCounts_AreRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => VulkanVideoPresenter.ValidateStorageResourceShapes(
            [
                StorageTexture(
                    GuestImageKind.Cube,
                    resourceArrayLayers: 6),
                StorageTexture(
                    GuestImageKind.Cube,
                    resourceArrayLayers: 12),
            ]));
    }

    private static GuestDrawTexture StorageTexture(
        GuestImageKind imageKind,
        uint depth = 1,
        uint resourceArrayLayers = 1,
        uint baseArrayLayer = 0) =>
        new(
            Address,
            Width: 15,
            Height: 8,
            Format: 10,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: true,
            IsArray: false,
            ResourceArrayLayers: resourceArrayLayers,
            BaseArrayLayer: baseArrayLayer,
            ViewArrayLayers: 1,
            ResourceMipLevels: 1,
            Depth: depth,
            ImageKind: imageKind);
}
