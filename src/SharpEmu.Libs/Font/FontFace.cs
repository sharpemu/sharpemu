// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SharpEmu.Libs.Font;

internal sealed class FontFace(SKTypeface typeface)
{
    private static readonly ConcurrentDictionary<(string Family, int Weight, int Width, SKFontStyleSlant Slant), FontSource> Fallbacks = new();

    private readonly ConcurrentDictionary<(uint Code, float Width, float Height), FontGlyph> _glyphs = new();
    private readonly ConcurrentDictionary<uint, (FontSource Source, ushort Glyph)> _resolved = new();
    private readonly FontSource _primary = new(typeface);

    public SKFontMetrics GetMetrics(float width, float height)
    {
        using var font = CreateFont(typeface, width, height);
        return font.Metrics;
    }

    public FontGlyph GetGlyph(uint code, float width, float height)
    {
        // ponytail: bounded working set; clearing avoids retaining every glyph at every scale.
        if (_glyphs.Count >= 1024) _glyphs.Clear();
        return _glyphs.GetOrAdd((code, width, height), key => Rasterize(key.Code, key.Width, key.Height));
    }

    public FontGlyph GetGlyphMetrics(uint code, float width, float height)
    {
        if (_glyphs.TryGetValue((code, width, height), out var glyph))
        {
            return glyph;
        }

        var (source, glyphId) = Resolve(code);
        if (source.Metrics?.TryMeasure(glyphId, width, height, out var metrics) == true)
        {
            return metrics;
        }

        using var font = CreateFont(source.Typeface, width, height);
        return Measure(font, glyphId, out _, out _);
    }

    private static SKFont CreateFont(SKTypeface face, float width, float height) => new(face, height, width / height)
    {
        Edging = SKFontEdging.Antialias,
        Subpixel = false,
    };

    private (FontSource Source, ushort Glyph) Resolve(uint code) =>
        _resolved.GetOrAdd(code, static (key, face) => face.ResolveUncached(key), this);

    private (FontSource Source, ushort Glyph) ResolveUncached(uint code)
    {
        if ((code & 0x80000000) != 0)
        {
            return (_primary, (ushort)code);
        }

        var glyph = typeface.GetGlyph((int)code);
        if (glyph != 0)
        {
            return (_primary, glyph);
        }

        // System font sets span multiple faces. Resolve by Unicode coverage, not language.
        var fallback = SKFontManager.Default.MatchCharacter(typeface.FamilyName, typeface.FontStyle, null, (int)code);
        if (fallback is null)
        {
            return (_primary, 0);
        }

        var style = fallback.FontStyle;
        var source = Fallbacks.GetOrAdd((fallback.FamilyName, style.Weight, style.Width, style.Slant), static (_, face) => new(face), fallback);
        if (!ReferenceEquals(source.Typeface, fallback))
        {
            fallback.Dispose();
        }
        return (source, source.Typeface.GetGlyph((int)code));
    }

    private static FontGlyph Measure(SKFont font, ushort glyph, out int left, out int top)
    {
        ReadOnlySpan<ushort> glyphs = [glyph];
        var advance = font.MeasureText(glyphs, out var bounds);
        left = (int)MathF.Floor(bounds.Left);
        top = (int)MathF.Floor(bounds.Top);
        var width = (int)MathF.Ceiling(bounds.Right) - left;
        var height = (int)MathF.Ceiling(bounds.Bottom) - top;
        return width <= 0 || height <= 0
            ? new(0, 0, left, -top, advance, [])
            : new(width, height, left, -top, advance, []);
    }

    private FontGlyph Rasterize(uint code, float width, float height)
    {
        var (source, glyphId) = Resolve(code);
        using var font = CreateFont(source.Typeface, width, height);
        var metrics = Measure(font, glyphId, out var left, out var top);
        if (metrics.Width == 0)
        {
            return metrics;
        }

        ReadOnlySpan<ushort> glyphs = [glyphId];
        using var bitmap = new SKBitmap(metrics.Width, metrics.Height, SKColorType.Alpha8, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var blob = SKTextBlob.Create(MemoryMarshal.AsBytes(glyphs), SKTextEncoding.GlyphId, font);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawText(blob!, -left, -top, paint);
        var coverage = new byte[metrics.Width * metrics.Height];
        for (var row = 0; row < metrics.Height; row++)
        {
            Marshal.Copy(bitmap.GetPixels() + row * bitmap.RowBytes, coverage, row * metrics.Width, metrics.Width);
        }
        return metrics with { Coverage = coverage };
    }

    private sealed class FontSource(SKTypeface typeface)
    {
        private readonly Lazy<TrueTypeMetrics?> _metrics = new(() => TrueTypeMetrics.TryCreate(typeface));

        public SKTypeface Typeface => typeface;

        public TrueTypeMetrics? Metrics => _metrics.Value;
    }
}

internal sealed record FontGlyph(int Width, int Height, float BearingX, float BearingY, float Advance, byte[] Coverage);
