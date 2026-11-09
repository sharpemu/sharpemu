// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRectListDrawTests
{
    [Fact]
    public void RectListWithoutVertexParametersCannotFeedPixelInputs()
    {
        Assert.True(AgcPrimitiveHelpers.ShouldSkipRectListWithoutParameterExports(
            AgcPrimitiveHelpers.PrimitiveRectList,
            indexed: false,
            vertexBufferCount: 0,
            parameterExportMask: 0,
            pixelInputCount: 1));
    }

    [Theory]
    [InlineData(4u, false, 0, 0u, 1u)]
    [InlineData(AgcPrimitiveHelpers.PrimitiveRectListLegacy, false, 0, 0u, 1u)]
    [InlineData(7u, true, 0, 0u, 1u)]
    [InlineData(7u, false, 1, 0u, 1u)]
    [InlineData(7u, false, 0, 1u, 1u)]
    [InlineData(7u, false, 0, 0u, 0u)]
    public void OtherDrawShapesArePreserved(
        uint primitiveType,
        bool indexed,
        int vertexBufferCount,
        uint parameterExportMask,
        uint pixelInputCount)
    {
        Assert.False(AgcPrimitiveHelpers.ShouldSkipRectListWithoutParameterExports(
            primitiveType,
            indexed,
            vertexBufferCount,
            parameterExportMask,
            pixelInputCount));
    }
}
