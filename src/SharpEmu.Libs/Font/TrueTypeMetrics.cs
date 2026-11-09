// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using SkiaSharp;

namespace SharpEmu.Libs.Font;

internal sealed class TrueTypeMetrics
{
    private readonly byte[] _locations;
    private readonly byte[] _glyphs;
    private readonly byte[] _horizontalMetrics;
    private readonly bool _longLocations;
    private readonly int _horizontalMetricCount;
    private readonly int _unitsPerEm;

    private TrueTypeMetrics(byte[] locations, byte[] glyphs, byte[] horizontalMetrics, bool longLocations,
        int horizontalMetricCount, int unitsPerEm)
    {
        _locations = locations;
        _glyphs = glyphs;
        _horizontalMetrics = horizontalMetrics;
        _longLocations = longLocations;
        _horizontalMetricCount = horizontalMetricCount;
        _unitsPerEm = unitsPerEm;
    }

    public static TrueTypeMetrics? TryCreate(SKTypeface typeface)
    {
        if (!typeface.TryGetTableData(Tag("head"), out var head) || head.Length < 54 ||
            !typeface.TryGetTableData(Tag("hhea"), out var horizontalHeader) || horizontalHeader.Length < 36 ||
            !typeface.TryGetTableData(Tag("hmtx"), out var horizontalMetrics) ||
            !typeface.TryGetTableData(Tag("loca"), out var locations) ||
            !typeface.TryGetTableData(Tag("glyf"), out var glyphs))
        {
            return null;
        }

        var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(18));
        var horizontalMetricCount = BinaryPrimitives.ReadUInt16BigEndian(horizontalHeader.AsSpan(34));
        if (unitsPerEm == 0 || horizontalMetricCount == 0 || horizontalMetrics.Length < horizontalMetricCount * 4)
        {
            return null;
        }

        var longLocations = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) != 0;
        return new(locations, glyphs, horizontalMetrics, longLocations, horizontalMetricCount, unitsPerEm);
    }

    public bool TryMeasure(ushort glyph, float width, float height, [NotNullWhen(true)] out FontGlyph? metrics)
    {
        metrics = null;
        if (!TryReadLocation(glyph, out var start) || !TryReadLocation(glyph + 1, out var end) ||
            end < start || end > _glyphs.Length || (end > start && end - start < 10))
        {
            return false;
        }

        var scaleX = width / _unitsPerEm;
        var scaleY = height / _unitsPerEm;
        var advanceOffset = Math.Min(glyph, _horizontalMetricCount - 1) * 4;
        var advance = BinaryPrimitives.ReadUInt16BigEndian(_horizontalMetrics.AsSpan(advanceOffset)) * scaleX;
        if (end == start)
        {
            metrics = new(0, 0, 0, 0, advance, []);
            return true;
        }

        var header = _glyphs.AsSpan((int)start);
        var left = (int)MathF.Floor(BinaryPrimitives.ReadInt16BigEndian(header[2..]) * scaleX);
        var top = (int)MathF.Floor(-BinaryPrimitives.ReadInt16BigEndian(header[8..]) * scaleY);
        var right = (int)MathF.Ceiling(BinaryPrimitives.ReadInt16BigEndian(header[6..]) * scaleX);
        var bottom = (int)MathF.Ceiling(-BinaryPrimitives.ReadInt16BigEndian(header[4..]) * scaleY);
        metrics = right <= left || bottom <= top
            ? new(0, 0, left, -top, advance, [])
            : new(right - left, bottom - top, left, -top, advance, []);
        return true;
    }

    private bool TryReadLocation(int glyph, out uint location)
    {
        var size = _longLocations ? 4 : 2;
        var offset = glyph * size;
        location = 0;
        if (offset + size > _locations.Length)
        {
            return false;
        }

        location = _longLocations
            ? BinaryPrimitives.ReadUInt32BigEndian(_locations.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16BigEndian(_locations.AsSpan(offset)) * 2u;
        return true;
    }

    private static uint Tag(string name) => (uint)(name[0] << 24 | name[1] << 16 | name[2] << 8 | name[3]);
}
