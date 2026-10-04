// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class MeshIndexAssemblyTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void PointsPreserveShortListsAndRestartBoundaries(uint width)
    {
        var bytes = new byte[5 * width];
        uint[] values = [7, 3, 255, 4, 7];
        for (var index = 0; index < values.Length; index++) bytes[index * width] = (byte)values[index];
        var output = new uint[5];
        var count = MeshIndexAssembly.ExpandPoints(bytes, output, width, -2, true, 255);
        Assert.Equal(new uint[] { 5, 1, 2, 5 }, output.AsSpan(0, count).ToArray());
        Assert.Equal(5, MeshIndexAssembly.ExpandPoints(bytes, output, width, 0, false, 255));
        Assert.Equal(values, output);
        Assert.Equal(1, MeshIndexAssembly.ExpandPoints(bytes.AsSpan(0, (int)width), output, width, 0, false, 0));
    }

    [Fact]
    public void TriangleListPreservesRepeatedIndicesAndSignedBaseVertex()
    {
        Assert.Equal(new uint[] { 5, 1, 5, 1, 2, 5 },
            ExpandTriangles([7, 3, 7, 3, 4, 7], 1, -2, false, false, 0));
    }

    [Fact]
    public void TriangleStripRestartsWindingAtEachSegment()
    {
        Assert.Equal(new uint[] { 1, 2, 3, 3, 2, 4, 5, 6, 7 },
            ExpandTriangles([1, 2, 3, 4, 255, 5, 6, 7], 1, 0, true, true, 255));
    }

    [Fact]
    public void TriangleListDropsIncompleteSegments()
    {
        Assert.Equal(new uint[] { 3, 4, 5 },
            ExpandTriangles([1, 2, 255, 3, 4, 5, 6], 1, 0, false, true, 255));
    }

    [Fact]
    public void TriangleStripPreservesFirstProvokingVertex()
    {
        Assert.Equal(new uint[] { 1, 2, 3, 2, 4, 3 },
            ExpandTriangles([1, 2, 3, 4], 1, 0, true, false, 0, false));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    public void WideIndicesUseLittleEndianValues(uint width)
    {
        var bytes = new byte[3 * width];
        for (var index = 0; index < 3; index++)
        {
            bytes[index * width] = (byte)(index + 1);
            bytes[index * width + 1] = 1;
        }
        Assert.Equal(new uint[] { 257, 258, 259 },
            ExpandTriangles(bytes, width, 0, false, false, 0));
    }

    [Fact]
    public void MisalignedDataIsRejected()
    {
        Assert.Throws<ArgumentException>(() => ExpandTriangles([1], 2, 0, false, false, 0));
    }

    [Fact]
    public void OutputTailIsNotModifiedAndSmallOutputIsRejected()
    {
        uint[] output = [99, 99, 99, 99, 99, 99];
        var written = MeshIndexAssembly.ExpandTriangles([1, 2, 255, 3, 4, 5], output, 1, 0, false, true, 255);
        Assert.Equal(3, written);
        Assert.Equal(new uint[] { 3, 4, 5, 99, 99, 99 }, output);
        Assert.Throws<ArgumentException>(() =>
            MeshIndexAssembly.ExpandTriangles([1, 2, 3], new uint[2], 1, 0, false, false, 0));
    }

    private static uint[] ExpandTriangles(ReadOnlySpan<byte> indices, uint elementSize,
        int baseVertex, bool triangleStrip, bool restartEnabled, uint restartIndex,
        bool provokingVertexLast = true)
    {
        var output = new uint[MeshIndexAssembly.GetMaximumCount(indices.Length, elementSize, triangleStrip)];
        var written = MeshIndexAssembly.ExpandTriangles(indices, output, elementSize, baseVertex,
            triangleStrip, restartEnabled, restartIndex, provokingVertexLast);
        return output.AsSpan(0, written).ToArray();
    }
}
