// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPrimitiveRasterStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RectanglesDisableOnlyFaceCulling(bool cullFront, bool cullBack)
    {
        var raster = new GuestRasterState(
            cullFront, cullBack, true, true, true, 2f, 3f, 4f, -24, false);

        var result = VulkanVideoPresenter.ResolvePrimitiveRasterState(
            AgcPrimitiveHelpers.PrimitiveRectList, raster);

        Assert.Equal(raster with { CullFront = false, CullBack = false }, result);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(AgcPrimitiveHelpers.PrimitiveRectListLegacy)]
    public void OtherPrimitivesPreserveRasterState(uint primitiveType)
    {
        var raster = new GuestRasterState(
            true, true, false, true, true, 2f, 3f, 4f, -24, false);

        var result = VulkanVideoPresenter.ResolvePrimitiveRasterState(primitiveType, raster);

        Assert.Equal(raster, result);
    }
}
