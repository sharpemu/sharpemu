// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SharpEmu.Libs.Font;

internal sealed class FontFace(SKTypeface typeface)
{
    private readonly ConcurrentDictionary<(uint Code, float Width, float Height), FontGlyph> _glyphs = new();

    public SKFontMetrics GetMetrics(float width, float height)
    {
        using var font = CreateFont(width, height);
        return font.Metrics;
    }

    public FontGlyph GetGlyph(uint code, float width, float height)
    {
        // ponytail: bounded working set; clearing avoids retaining every glyph at every scale.
        if (_glyphs.Count >= 1024) _glyphs.Clear();
        return _glyphs.GetOrAdd((code, width, height), key => Rasterize(key.Code, key.Width, key.Height));
    }

    private SKFont CreateFont(float width, float height) => new(typeface, height, width / height)
    {
        Edging = SKFontEdging.Antialias,
        Subpixel = false,
    };

    private FontGlyph Rasterize(uint code, float width, float height)
    {
        using var font = CreateFont(width, height);
        var glyph = (code & 0x80000000) != 0 ? (ushort)code : font.GetGlyph((int)code);
        if (glyph == 0 && (code & 0x80000000) == 0)
        {
            // System font sets span multiple faces. Resolve by Unicode coverage, not language.
            var fallback = SKFontManager.Default.MatchCharacter(typeface.FamilyName, typeface.FontStyle, null, (int)code);
            if (fallback is not null)
            {
                font.Typeface = fallback;
                glyph = font.GetGlyph((int)code);
            }
        }

        ReadOnlySpan<ushort> glyphs = [glyph];
        var advance = font.MeasureText(glyphs, out var bounds);
        var left = (int)MathF.Floor(bounds.Left);
        var top = (int)MathF.Floor(bounds.Top);
        var bitmapWidth = (int)MathF.Ceiling(bounds.Right) - left;
        var bitmapHeight = (int)MathF.Ceiling(bounds.Bottom) - top;
        if (bitmapWidth <= 0 || bitmapHeight <= 0)
        {
            return new(0, 0, left, -top, advance, []);
        }

        using var bitmap = new SKBitmap(bitmapWidth, bitmapHeight, SKColorType.Alpha8, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var blob = SKTextBlob.Create(MemoryMarshal.AsBytes(glyphs), SKTextEncoding.GlyphId, font);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawText(blob!, -left, -top, paint);
        var coverage = new byte[bitmapWidth * bitmapHeight];
        for (var row = 0; row < bitmapHeight; row++)
        {
            Marshal.Copy(bitmap.GetPixels() + row * bitmap.RowBytes, coverage, row * bitmapWidth, bitmapWidth);
        }
        return new(bitmapWidth, bitmapHeight, left, -top, advance, coverage);
    }
}

internal sealed record FontGlyph(int Width, int Height, float BearingX, float BearingY, float Advance, byte[] Coverage);
