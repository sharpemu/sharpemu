// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5UnifiedFormatTests
{
    [Theory]
    [InlineData(22u, 4u, 7u, 4u, 0)]
    [InlineData(29u, 5u, 7u, 4u, 0)]
    [InlineData(36u, 6u, 7u, 4u, 0)]
    [InlineData(56u, 10u, 0u, 4u, 0)]
    [InlineData(62u, 11u, 4u, 8u, 2)]
    [InlineData(64u, 11u, 7u, 8u, 0)]
    [InlineData(69u, 12u, 4u, 8u, 2)]
    [InlineData(71u, 12u, 7u, 8u, 0)]
    public void DecodesArchitecturalUnifiedFormat(
        uint unifiedFormat,
        uint expectedDataFormat,
        uint expectedNumberFormat,
        uint expectedBytesPerTexel,
        int expectedNumericKind)
    {
        Assert.True(
            Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.Equal(expectedDataFormat, decoded.DataFormat);
        Assert.Equal(expectedNumberFormat, decoded.NumberFormat);
        Assert.Equal(expectedBytesPerTexel, decoded.BytesPerTexel);
        Assert.Equal((Gen5FormatNumericKind)expectedNumericKind, decoded.NumericKind);
        Assert.False(decoded.IsBlockCompressed);
    }

    [Theory]
    [InlineData(169u, 8u)]
    [InlineData(170u, 8u)]
    [InlineData(171u, 16u)]
    [InlineData(175u, 8u)]
    [InlineData(176u, 8u)]
    [InlineData(177u, 16u)]
    [InlineData(182u, 16u)]
    public void DecodesCompressedBlockSize(
        uint unifiedFormat,
        uint expectedBytesPerBlock)
    {
        Assert.True(
            Gen5UnifiedFormat.TryDecode(unifiedFormat, out var decoded));
        Assert.True(decoded.IsBlockCompressed);
        Assert.Equal(4u, decoded.BlockWidth);
        Assert.Equal(4u, decoded.BlockHeight);
        Assert.Equal(expectedBytesPerBlock, decoded.BytesPerBlock);
        Assert.Equal(expectedBytesPerBlock, decoded.GetByteCount(4, 4));
        Assert.Equal(expectedBytesPerBlock * 4UL, decoded.GetByteCount(5, 5));
    }

    [Fact]
    public void R32FloatByteCount_DoesNotUseOverlappingNumberBits()
    {
        Assert.True(Gen5UnifiedFormat.TryDecode(22, out var decoded));

        Assert.Equal(8_294_400UL, decoded.GetByteCount(1920, 1080));
    }

    [Fact]
    public void UnknownFormat_IsRejected()
    {
        Assert.False(Gen5UnifiedFormat.TryDecode(0, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(30, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(46, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(47, out _));
        Assert.False(Gen5UnifiedFormat.TryDecode(127, out _));
    }

}
