// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcRenderTargetComponentSwapTests
{
    [Theory]
    [InlineData(0u, 1u, 0xE4)]
    [InlineData(0u, 2u, 0xE4)]
    [InlineData(0u, 3u, 0xE4)]
    [InlineData(0u, 4u, 0xE4)]
    [InlineData(1u, 1u, 0xE1)]
    [InlineData(1u, 2u, 0x6C)]
    [InlineData(1u, 3u, 0xB4)]
    [InlineData(1u, 4u, 0xC6)]
    [InlineData(2u, 1u, 0xC6)]
    [InlineData(2u, 2u, 0xE1)]
    [InlineData(2u, 3u, 0xC6)]
    [InlineData(2u, 4u, 0x1B)]
    [InlineData(3u, 1u, 0x27)]
    [InlineData(3u, 2u, 0x63)]
    [InlineData(3u, 3u, 0x87)]
    [InlineData(3u, 4u, 0x93)]
    public void RenderTargetMappingMatchesGuestComponentOrder(
        uint componentSwap,
        uint componentCount,
        byte expected)
    {
        Assert.True(
            Gen5ColorComponentMapping.TryResolveRenderTarget(
                componentSwap,
                componentCount,
                out var mapping));
        Assert.Equal(expected, mapping.Packed);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(0u, 5u)]
    [InlineData(4u, 4u)]
    public void RenderTargetMappingRejectsInvalidInputs(
        uint componentSwap,
        uint componentCount)
    {
        Assert.False(
            Gen5ColorComponentMapping.TryResolveRenderTarget(
                componentSwap,
                componentCount,
                out _));
    }

    [Fact]
    public void MappingSelectsLogicalComponentsAndMapsWriteMask()
    {
        var bgra = new Gen5ColorComponentMapping(0xC6);

        Assert.Equal([2u, 1u, 0u, 3u],
            Enumerable.Range(0, 4).Select(index => bgra.Map((uint)index)));
        Assert.Equal(0x4u, bgra.ApplyMask(0x1u));
        Assert.Equal(0x5u, bgra.ApplyMask(0x5u));
    }

    [Fact]
    public void MappingCompositionAppliesCurrentThenNext()
    {
        var bgra = new Gen5ColorComponentMapping(0xC6);
        var rabg = new Gen5ColorComponentMapping(0x6C);
        var rgab = new Gen5ColorComponentMapping(0xB4);

        Assert.Equal(
            Gen5ColorComponentMapping.Identity,
            bgra.Then(bgra));
        Assert.Equal(0x78, rabg.Then(rgab).Packed);
        Assert.Equal(0x9C, rgab.Then(rabg).Packed);
    }

    [Fact]
    public void DefaultMappingIsIdentity()
    {
        Assert.Equal(Gen5ColorComponentMapping.Identity, default);
        Assert.Equal(Gen5ColorComponentMapping.IdentityPacked, default(Gen5ColorComponentMapping).Packed);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void ColorInfoExtractsComponentSwapBits(uint componentSwap)
    {
        var unrelatedBits = 0xA5A5_A5A5u & ~(0x3u << 11);

        Assert.Equal(
            componentSwap,
            AgcExports.ExtractRenderTargetComponentSwap(
                unrelatedBits | (componentSwap << 11)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(24u)]
    [InlineData(27u)]
    [InlineData(31u)]
    public void ColorAttrib3ExtractsTileModeBits(uint tileMode)
    {
        var unrelatedBits = 0xA5A5_A5A5u & ~(0x1Fu << 14);

        Assert.Equal(
            tileMode,
            AgcExports.ExtractRenderTargetTileMode(
                unrelatedBits | (tileMode << 14)));
    }

    [Fact]
    public void GraphicsOutputIdentitySeparatesComponentMappings()
    {
        var standard = AgcExports.PackPixelOutputMappings(
            [Gen5ColorComponentMapping.Identity]);
        var alternate = AgcExports.PackPixelOutputMappings(
            [new Gen5ColorComponentMapping(0xC6)]);
        var alternateSecondSlot = AgcExports.PackPixelOutputMappings(
            [Gen5ColorComponentMapping.Identity, new Gen5ColorComponentMapping(0xC6)]);

        Assert.NotEqual(standard, alternate);
        Assert.NotEqual(alternate, alternateSecondSlot);
    }
}
