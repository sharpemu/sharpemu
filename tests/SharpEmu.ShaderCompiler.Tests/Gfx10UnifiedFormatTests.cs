// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gfx10UnifiedFormatTests
{
    // 2_10_10_10 is R10G10B10A2: X in the low ten bits and W in the top two, like the
    // A2B10G10R10 host format the image and fixed-function vertex paths use for it.
    [Theory]
    [InlineData(50u)]
    [InlineData(51u)]
    public void TwoTenTenTenReadsXFromTheLowBits(uint unifiedFormat)
    {
        Assert.True(Gfx10UnifiedFormat.TryDecode(unifiedFormat, out var dataFormat, out _));
        Assert.Equal(9u, dataFormat);
        (uint BitOffset, uint BitCount)[] expected = [(0, 10), (10, 10), (20, 10), (30, 2)];
        for (uint component = 0; component < 4; component++)
        {
            Assert.True(Gfx10UnifiedFormat.TryGetComponentLayout(dataFormat, component, out var byteOffset, out var bitOffset, out var bitCount));
            Assert.Equal((0u, expected[component].BitOffset, expected[component].BitCount), (byteOffset, bitOffset, bitCount));
        }

        Assert.Equal(4u, Gfx10UnifiedFormat.GetAccessByteSize(dataFormat, 4));
    }

    // The translator reads a component as a funnel of two aligned dwords and packs each layout
    // into one 24-bit table entry; both rely on these bounds.
    [Fact]
    public void EveryComponentLayoutFitsAFourByteWindowInsideItsElement()
    {
        var elementBytes = new Dictionary<uint, uint>
        {
            [1] = 1, [2] = 2, [3] = 2, [4] = 4, [5] = 4, [6] = 4, [7] = 4,
            [8] = 4, [9] = 4, [10] = 4, [11] = 8, [12] = 8, [13] = 12, [14] = 16,
        };

        foreach (var (component, dataFormat, byteOffset, bitOffset, bitCount) in Gfx10UnifiedFormat.ComponentLayouts)
        {
            Assert.True(component < 4);
            Assert.True(dataFormat < 16, $"data format {dataFormat}");
            Assert.InRange(bitCount, 1u, 32u);
            Assert.True(bitOffset + bitCount <= 32, $"format {dataFormat} component {component}");
            Assert.True(byteOffset <= 0xFF && bitOffset <= 0xFF);
            var end = byteOffset + ((bitOffset + bitCount + 7) / 8);
            Assert.True(end <= elementBytes[dataFormat], $"format {dataFormat} component {component}");
        }
    }
}
