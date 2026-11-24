// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace SharpEmu.Libs.Overlay;

internal static class OverlayFonts
{
    private const string Ellipsis = "…";

    private static readonly object Gate = new();
    private static readonly Dictionary<int, SKTypeface?> Fallbacks = new();
    private static readonly Dictionary<SKTypeface, SKShaper> Shapers = new();
    private static SKTypeface? _light;
    private static SKTypeface? _regular;
    private static SKTypeface? _bold;

    internal static float Measure(string text, float size, OverlayWeight weight)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        lock (Gate)
        {
            var typeface = Resolve(text, weight);
            using var font = Create(typeface, size);
            return Shaper(typeface).Shape(text, font).Width;
        }
    }

    internal static void Draw(SKCanvas canvas, SKPaint paint, string text, float x, float baseline, float size,
        OverlayWeight weight, float maxWidth, SKTextAlign align)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return;
        }

        lock (Gate)
        {
            var typeface = Resolve(text, weight);
            using var font = Create(typeface, size);
            var shaper = Shaper(typeface);
            var fitted = Fit(shaper, font, text, maxWidth);
            if (fitted.Length != 0)
            {
                canvas.DrawShapedText(shaper, fitted, x, baseline, align, font, paint);
            }
        }
    }

    internal static List<string> Wrap(string text, float size, OverlayWeight weight, float maxWidth, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text) || maxWidth <= 0 || maxLines <= 0)
        {
            return lines;
        }

        lock (Gate)
        {
            var typeface = Resolve(text, weight);
            using var font = Create(typeface, size);
            var shaper = Shaper(typeface);
            foreach (var paragraph in text.Split('\n'))
            {
                var runes = paragraph.Trim().EnumerateRunes().ToArray();
                var start = 0;
                while (start < runes.Length)
                {
                    if (lines.Count == maxLines - 1)
                    {
                        lines.Add(Fit(shaper, font, Join(runes[start..], runes.Length - start), maxWidth));
                        return lines;
                    }

                    var end = start;
                    var lastBreak = -1;
                    while (end < runes.Length &&
                           shaper.Shape(Join(runes[start..], end - start + 1), font).Width <= maxWidth)
                    {
                        if (Rune.IsWhiteSpace(runes[end]))
                        {
                            lastBreak = end;
                        }

                        end++;
                    }

                    if (end < runes.Length && lastBreak > start)
                    {
                        end = lastBreak;
                    }

                    end = Math.Max(end, start + 1);
                    lines.Add(Join(runes[start..], end - start).Trim());
                    start = end;
                    while (start < runes.Length && Rune.IsWhiteSpace(runes[start]))
                    {
                        start++;
                    }
                }

                if (lines.Count == maxLines)
                {
                    break;
                }
            }
        }

        return lines;
    }

    private static string Fit(SKShaper shaper, SKFont font, string text, float maxWidth)
    {
        if (shaper.Shape(text, font).Width <= maxWidth)
        {
            return text;
        }

        var runes = text.EnumerateRunes().ToArray();
        var low = 0;
        var high = runes.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (shaper.Shape(Join(runes, middle) + Ellipsis, font).Width <= maxWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low == 0 ? string.Empty : Join(runes, low).TrimEnd() + Ellipsis;
    }

    private static string Join(Rune[] runes, int count)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            builder.Append(runes[index].ToString());
        }

        return builder.ToString();
    }

    private static SKFont Create(SKTypeface typeface, float size) =>
        new(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };

    private static SKShaper Shaper(SKTypeface typeface)
    {
        if (!Shapers.TryGetValue(typeface, out var shaper))
        {
            shaper = new SKShaper(typeface);
            Shapers[typeface] = shaper;
        }

        return shaper;
    }

    private static SKTypeface Resolve(string text, OverlayWeight weight)
    {
        var primary = weight switch
        {
            OverlayWeight.Light => _light ??= SKTypeface.FromFamilyName(null, SKFontStyleWeight.Light,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default,
            OverlayWeight.Bold => _bold ??= SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default,
            _ => _regular ??= SKTypeface.FromFamilyName(null) ?? SKTypeface.Default,
        };
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 0x20 || primary.ContainsGlyph(rune.Value))
            {
                continue;
            }

            if (!Fallbacks.TryGetValue(rune.Value, out var fallback))
            {
                fallback = SKFontManager.Default.MatchCharacter(rune.Value);
                Fallbacks[rune.Value] = fallback;
            }

            return fallback ?? primary;
        }

        return primary;
    }
}
