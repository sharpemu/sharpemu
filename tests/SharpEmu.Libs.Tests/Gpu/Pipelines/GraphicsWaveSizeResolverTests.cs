// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class GraphicsWaveSizeResolverTests
{
    [Theory]
    [InlineData(0u, 64u)]
    [InlineData(0x0040_0000u, 32u)]
    [InlineData(0x0040_0020u, 32u)]
    public void VertexWaveSizeComesFromShaderStages(uint shaderStages, uint expected) =>
        Assert.Equal(expected, GraphicsWaveSizeResolver.Vertex(shaderStages));

    [Theory]
    [InlineData(0u, 64u)]
    [InlineData(0x0000_8000u, 32u)]
    [InlineData(0x0000_803Fu, 32u)]
    public void PixelWaveSizeComesFromPixelInputControl(uint pixelInputControl, uint expected) =>
        Assert.Equal(expected, GraphicsWaveSizeResolver.Pixel(pixelInputControl));
}
