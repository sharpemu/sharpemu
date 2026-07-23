// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

/// <summary>
/// Regression coverage for the AGC UI path: index8 expansion and rect-list
/// vertex counts / topology selection.
/// </summary>
public sealed class AgcRectListIndexHelpersTests
{
    [Theory]
    [InlineData(0u, 0u, 2)] // Index16
    [InlineData(1u, 1u, 4)] // Index32
    [InlineData(2u, 2u, 1)] // Index8
    [InlineData(0x402u, 2u, 1)] // UC 0x400|size -> Index8
    public void IndexType_DecodeAndStride_MatchProspero(uint raw, uint expected, int stride)
    {
        var decoded = AgcIndexHelpers.Decode(raw);
        Assert.Equal((AgcIndexHelpers.ProsperoIndexType)expected, decoded);
        Assert.Equal(stride, AgcIndexHelpers.GetGuestStrideBytes(decoded));
    }

    [Fact]
    public void ExpandIndex8ToU16_PreservesValues()
    {
        ReadOnlySpan<byte> source = [0x00, 0x01, 0xFF, 0x7F];
        Span<byte> destination = stackalloc byte[8];
        AgcIndexHelpers.ExpandIndex8ToU16(source, destination);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(destination[..2]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(destination.Slice(2, 2)));
        Assert.Equal(255, BinaryPrimitives.ReadUInt16LittleEndian(destination.Slice(4, 2)));
        Assert.Equal(127, BinaryPrimitives.ReadUInt16LittleEndian(destination.Slice(6, 2)));
    }

    [Theory]
    // NGG single-rect UI (DualSense): expand even when VBs are present
    [InlineData(7u, 3u, false, true, 4u)]
    [InlineData(7u, 1u, false, false, 4u)]
    [InlineData(7u, 4u, false, true, 4u)]
    // Indexed / multi-vert auto: keep guest count (loading video)
    [InlineData(7u, 3u, true, false, 3u)]
    [InlineData(7u, 6u, false, true, 6u)]
    [InlineData(7u, 4u, true, true, 4u)]
    [InlineData(0x11u, 3u, false, false, 4u)]
    [InlineData(0x11u, 6u, false, false, 6u)]
    [InlineData(4u, 3u, false, false, 3u)]
    public void RectListDrawVertexCount_MatchesExpansion(
        uint primitiveType,
        uint vertexCount,
        bool indexed,
        bool hasVertexBuffers,
        uint expected)
    {
        Assert.Equal(
            expected,
            AgcPrimitiveHelpers.GetRectListDrawVertexCount(
                primitiveType,
                vertexCount,
                indexed,
                hasVertexBuffers));
    }

    [Theory]
    [InlineData(7u, false, 3u, true, true)]
    [InlineData(7u, false, 6u, true, false)]
    [InlineData(7u, true, 3u, false, false)]
    [InlineData(0x11u, false, 3u, true, true)]
    [InlineData(0x11u, true, 3u, false, false)]
    public void RectListTriangleStrip_MatchesGuards(
        uint primitiveType,
        bool indexed,
        uint vertexCount,
        bool hasVertexBuffers,
        bool expected) =>
        Assert.Equal(
            expected,
            AgcPrimitiveHelpers.ShouldDrawRectListAsTriangleStrip(
                primitiveType,
                indexed,
                vertexCount,
                hasVertexBuffers));

    [Theory]
    [InlineData(7u, (uint)AgcPrimitiveHelpers.GsOutputPrimitiveType.Rectangle2D)]
    [InlineData(0x11u, (uint)AgcPrimitiveHelpers.GsOutputPrimitiveType.RectList)]
    [InlineData(4u, (uint)AgcPrimitiveHelpers.GsOutputPrimitiveType.Triangles)]
    [InlineData(1u, (uint)AgcPrimitiveHelpers.GsOutputPrimitiveType.Points)]
    public void PrimitiveTypeToGsOut_MatchesProspero(uint primitiveType, uint expected) =>
        Assert.Equal(expected, AgcPrimitiveHelpers.PrimitiveTypeToGsOut(primitiveType));
}
