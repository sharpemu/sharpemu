// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.Rendering;

internal static class MeshIndexAssembly
{
    internal static int ExpandPoints(ReadOnlySpan<byte> indices, Span<uint> output, uint elementSize,
        int baseVertex, bool restartEnabled, uint restartIndex)
    {
        _ = GetMaximumCount(indices.Length, elementSize, false);
        if (output.Length < indices.Length / (int)elementSize)
            throw new ArgumentException("The index output is too small.", nameof(output));
        var mask = elementSize == 1 ? byte.MaxValue : elementSize == 2 ? ushort.MaxValue : uint.MaxValue;
        restartIndex &= mask;
        var written = 0;
        for (var offset = 0; offset < indices.Length; offset += (int)elementSize)
        {
            var index = elementSize switch
            {
                1 => indices[offset],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(indices[offset..]),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(indices[offset..]),
            };
            if (restartEnabled && index == restartIndex) continue;
            output[written++] = unchecked(index + (uint)baseVertex);
        }
        return written;
    }

    internal static int GetMaximumCount(int byteCount, uint elementSize, bool triangleStrip)
    {
        if (elementSize is not (1 or 2 or 4) || byteCount < 0 || byteCount % elementSize != 0)
            throw new ArgumentException("The index data has an invalid element size or length.");
        var elementCount = byteCount / (int)elementSize;
        return triangleStrip ? checked(Math.Max(0, elementCount - 2) * 3) : elementCount / 3 * 3;
    }

    internal static int ExpandTriangles(ReadOnlySpan<byte> indices, Span<uint> output, uint elementSize,
        int baseVertex, bool triangleStrip, bool restartEnabled, uint restartIndex,
        bool provokingVertexLast = true)
    {
        var maximumCount = GetMaximumCount(indices.Length, elementSize, triangleStrip);
        if (output.Length < maximumCount)
            throw new ArgumentException("The index output is too small.", nameof(output));
        var written = 0;
        var mask = elementSize == 1 ? byte.MaxValue : elementSize == 2 ? ushort.MaxValue : uint.MaxValue;
        restartIndex &= mask;
        uint first = 0, second = 0;
        var segmentCount = 0;
        for (var offset = 0; offset < indices.Length; offset += (int)elementSize)
        {
            var index = elementSize switch
            {
                1 => indices[offset],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(indices[offset..]),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(indices[offset..]),
            };
            if (restartEnabled && index == restartIndex)
            {
                segmentCount = 0;
                continue;
            }

            var vertex = unchecked(index + (uint)baseVertex);
            if (segmentCount == 0) first = vertex;
            else if (segmentCount == 1) second = vertex;
            else if (triangleStrip)
            {
                var odd = (segmentCount & 1) != 0;
                output[written++] = odd && provokingVertexLast ? second : first;
                output[written++] = odd ? (provokingVertexLast ? first : vertex) : second;
                output[written++] = odd && !provokingVertexLast ? second : vertex;
                first = second;
                second = vertex;
            }
            else
            {
                output[written++] = first;
                output[written++] = second;
                output[written++] = vertex;
            }
            segmentCount++;
            if (!triangleStrip && segmentCount == 3) segmentCount = 0;
        }
        return written;
    }
}
