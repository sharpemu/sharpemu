// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SkiaSharp;

namespace SharpEmu.Libs.Overlay;

public sealed class OverlayPainter : IDisposable
{
    private static readonly SKColor XboxA = new(0x3F, 0xB9, 0x50);
    private static readonly SKColor XboxB = new(0xE5, 0x48, 0x4D);
    private static readonly SKColor XboxX = new(0x3B, 0x8E, 0xEA);
    private static readonly SKColor XboxY = new(0xF2, 0xB7, 0x05);

    private readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    public OverlayPainter(SKCanvas canvas, OverlayPalette palette, float unit)
    {
        Canvas = canvas;
        Palette = palette;
        Unit = unit;
    }

    public SKCanvas Canvas { get; }

    public OverlayPalette Palette { get; }

    public float Unit { get; }

    public float U(float value) => value * Unit;

    public static unsafe int RenderInto(Span<byte> bgra, int width, int height, Func<SKCanvas, int> render)
    {
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The overlay pixel buffer must match its dimensions.", nameof(bgra));
        }

        fixed (byte* address = bgra)
        {
            using var bitmap = new SKBitmap();
            if (!bitmap.InstallPixels(
                    new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                    (nint)address,
                    width * 4))
            {
                throw new InvalidOperationException("The overlay pixel buffer cannot be drawn.");
            }

            using var canvas = new SKCanvas(bitmap);
            var used = render(canvas);
            canvas.Flush();
            return Math.Clamp(used, 1, height);
        }
    }

    public void Surface(float width, float height)
    {
        Canvas.Clear(Palette.Background);
        Outline(new SKRect(0, 0, width, height), 0, Palette.Raised);
    }

    public void Band(SKRect rect, bool lineAbove)
    {
        Rect(rect, Palette.Menu);
        var line = Math.Max(1, Unit);
        Rect(lineAbove
            ? new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + line)
            : new SKRect(rect.Left, rect.Bottom - line, rect.Right, rect.Bottom), Palette.Raised);
    }

    public void Rect(SKRect rect, SKColor color)
    {
        _paint.Style = SKPaintStyle.Fill;
        _paint.Color = color;
        Canvas.DrawRect(rect, _paint);
    }

    public void RoundRect(SKRect rect, float radius, SKColor color)
    {
        _paint.Style = SKPaintStyle.Fill;
        _paint.Color = color;
        Canvas.DrawRoundRect(rect, radius, radius, _paint);
    }

    public void Outline(SKRect rect, float radius, SKColor color, float width = 1)
    {
        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = Math.Max(1, U(width));
        _paint.Color = color;
        var inner = rect;
        inner.Inflate(-_paint.StrokeWidth / 2, -_paint.StrokeWidth / 2);
        if (radius > 0)
        {
            Canvas.DrawRoundRect(inner, radius, radius, _paint);
        }
        else
        {
            Canvas.DrawRect(inner, _paint);
        }

        _paint.Style = SKPaintStyle.Fill;
    }

    public void Divider(float x, float y, float width) =>
        Rect(new SKRect(x, y, x + width, y + Math.Max(1, Unit)), Palette.Raised.WithAlpha(0xB0));

    public float Measure(string text, OverlayFont font) => OverlayFonts.Measure(text, U(font.Size), font.Weight);

    public void Text(string? text, float x, float baseline, OverlayFont font, SKColor color, float maxWidth,
        SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _paint.Style = SKPaintStyle.Fill;
        _paint.Color = color;
        OverlayFonts.Draw(Canvas, _paint, text, x, baseline, U(font.Size), font.Weight, maxWidth, align);
    }

    public float Paragraph(string? text, float x, float baseline, OverlayFont font, SKColor color, float maxWidth,
        int maxLines, float lineHeight)
    {
        if (string.IsNullOrEmpty(text))
        {
            return baseline - lineHeight;
        }

        var lines = OverlayFonts.Wrap(text, U(font.Size), font.Weight, maxWidth, maxLines);
        for (var index = 0; index < lines.Count; index++)
        {
            Text(lines[index], x, baseline + index * lineHeight, font, color, maxWidth);
        }

        return baseline + (Math.Max(1, lines.Count) - 1) * lineHeight;
    }

    public IReadOnlyList<string> Wrap(string? text, OverlayFont font, float maxWidth, int maxLines) =>
        string.IsNullOrEmpty(text) ? [] : OverlayFonts.Wrap(text, U(font.Size), font.Weight, maxWidth, maxLines);

    public void Shadow(SKRect bounds, float radius, float blur, float offsetY, byte alpha)
    {
        using var shadow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, U(blur));
        _paint.Style = SKPaintStyle.Fill;
        _paint.MaskFilter = shadow;
        _paint.Color = new SKColor(0, 0, 0, alpha);
        var shifted = bounds;
        shifted.Offset(0, U(offsetY));
        Canvas.DrawRoundRect(shifted, radius, radius, _paint);
        _paint.MaskFilter = null;
    }

    public void Progress(SKRect track, float fraction)
    {
        var radius = track.Height / 2;
        RoundRect(track, radius, Palette.Raised);
        var width = track.Width * Math.Clamp(fraction, 0, 1);
        if (width > 0)
        {
            RoundRect(new SKRect(track.Left, track.Top, track.Left + Math.Max(width, track.Height), track.Bottom), radius,
                Palette.Accent);
        }
    }

    public void Card(SKRect bounds, bool focused)
    {
        var radius = U(OverlayStyle.CardRadius);
        if (focused)
        {
            using var glow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, U(OverlayStyle.FocusGlow));
            _paint.MaskFilter = glow;
            _paint.Color = Palette.Accent.WithAlpha(0x55);
            Canvas.DrawRoundRect(bounds, radius, radius, _paint);
            _paint.MaskFilter = null;
        }

        RoundRect(bounds, radius, focused ? Palette.Elevated : Palette.Card.WithAlpha(0xD8));
        if (focused)
        {
            Outline(bounds, radius, Palette.Accent, OverlayStyle.FocusRingWidth);
        }
    }

    public void Image(SKImage? image, SKRect rect, float radius, string? placeholder = null, bool plus = false)
    {
        Canvas.Save();
        Canvas.ClipRoundRect(new SKRoundRect(rect, radius, radius), antialias: true);
        if (image is not null)
        {
            var scale = Math.Max(rect.Width / image.Width, rect.Height / image.Height);
            var sourceWidth = rect.Width / scale;
            var sourceHeight = rect.Height / scale;
            var source = SKRect.Create((image.Width - sourceWidth) / 2, (image.Height - sourceHeight) / 2,
                sourceWidth, sourceHeight);
            Canvas.DrawImage(image, source, rect, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        else
        {
            Rect(rect, Palette.Raised);
            if (plus)
            {
                var arm = Math.Min(rect.Width, rect.Height) * 0.18f;
                _paint.Style = SKPaintStyle.Stroke;
                _paint.StrokeWidth = Math.Max(2, Math.Min(rect.Width, rect.Height) * 0.05f);
                _paint.StrokeCap = SKStrokeCap.Round;
                _paint.Color = Palette.Muted;
                Canvas.DrawLine(rect.MidX - arm, rect.MidY, rect.MidX + arm, rect.MidY, _paint);
                Canvas.DrawLine(rect.MidX, rect.MidY - arm, rect.MidX, rect.MidY + arm, _paint);
                _paint.Style = SKPaintStyle.Fill;
            }
            else if (!string.IsNullOrEmpty(placeholder))
            {
                var initial = placeholder.EnumerateRunes().First().ToString().ToUpperInvariant();
                var size = rect.Height * 0.5f;
                _paint.Color = Palette.Muted;
                OverlayFonts.Draw(Canvas, _paint, initial, rect.MidX, rect.MidY + size * 0.36f, size,
                    OverlayWeight.Light, rect.Width, SKTextAlign.Center);
            }
        }

        Canvas.Restore();
    }

    public void ScrollBar(SKRect track, int first, int visible, int total)
    {
        if (total <= visible)
        {
            return;
        }

        var radius = track.Width / 2;
        RoundRect(track, radius, Palette.Raised.WithAlpha(0xC0));
        var thumbHeight = Math.Max(U(28), track.Height * visible / total);
        var thumbTop = track.Top + (track.Height - thumbHeight) * first / Math.Max(1, total - visible);
        RoundRect(new SKRect(track.Left, thumbTop, track.Right, thumbTop + thumbHeight), radius, Palette.Muted);
    }

    public void Dim(SKRect rect, byte alpha = 0xA8) => Rect(rect, new SKColor(0, 0, 0, alpha));

    public void Modal(SKRect box)
    {
        var radius = U(OverlayStyle.ModalRadius);
        using (var shadow = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, U(18)))
        {
            _paint.MaskFilter = shadow;
            _paint.Color = new SKColor(0, 0, 0, 0x90);
            Canvas.DrawRoundRect(box, radius, radius, _paint);
            _paint.MaskFilter = null;
        }

        RoundRect(box, radius, Palette.Menu);
        Outline(box, radius, Palette.Raised);
    }

    public float PromptsWidth(IReadOnlyList<OverlayPrompt> prompts, OverlayInputScheme scheme)
    {
        var width = 0f;
        foreach (var prompt in prompts)
        {
            width += GlyphWidth(prompt, scheme) + U(9) + Measure(prompt.Label, OverlayStyle.Label);
        }

        return width + Math.Max(0, prompts.Count - 1) * U(OverlayStyle.PromptSpacing);
    }

    public void Prompts(IReadOnlyList<OverlayPrompt> prompts, OverlayInputScheme scheme, float x, float middle)
    {
        foreach (var prompt in prompts)
        {
            x += Glyph(prompt, scheme, x, middle) + U(9);
            var labelWidth = Measure(prompt.Label, OverlayStyle.Label);
            Text(prompt.Label, x, middle + U(6), OverlayStyle.Label, Palette.Secondary, labelWidth + 1);
            x += labelWidth + U(OverlayStyle.PromptSpacing);
        }
    }

    public void Dispose() => _paint.Dispose();

    public float PromptGlyphWidth(OverlayPrompt prompt, OverlayInputScheme scheme, float scale = 1) =>
        GlyphWidth(prompt, scheme, scale);

    public float PromptGlyph(OverlayPrompt prompt, OverlayInputScheme scheme, float x, float middle, float scale = 1) =>
        Glyph(prompt, scheme, x, middle, scale);

    private static string? ShoulderLabel(OverlayButton button, OverlayInputScheme scheme) => (button, scheme) switch
    {
        (OverlayButton.L1, OverlayInputScheme.Xbox) => "LB",
        (OverlayButton.R1, OverlayInputScheme.Xbox) => "RB",
        (OverlayButton.L2, OverlayInputScheme.Xbox) => "LT",
        (OverlayButton.R2, OverlayInputScheme.Xbox) => "RT",
        (OverlayButton.L3, OverlayInputScheme.Xbox) => "LS",
        (OverlayButton.R3, OverlayInputScheme.Xbox) => "RS",
        (OverlayButton.L1, _) => "L1",
        (OverlayButton.R1, _) => "R1",
        (OverlayButton.L2, _) => "L2",
        (OverlayButton.R2, _) => "R2",
        (OverlayButton.L3, _) => "L3",
        (OverlayButton.R3, _) => "R3",
        _ => null,
    };

    private float GlyphWidth(OverlayPrompt prompt, OverlayInputScheme scheme, float scale = 1)
    {
        if (scheme == OverlayInputScheme.Keyboard)
        {
            return Measure(prompt.KeyLabel, Scaled(OverlayStyle.KeyCap, scale)) + U(18 * scale);
        }

        return ShoulderLabel(prompt.Button, scheme) is { } label
            ? Measure(label, Scaled(OverlayStyle.KeyCap, scale)) + U(16 * scale)
            : U(OverlayStyle.PromptGlyphRadius * 2 * scale);
    }

    private static OverlayFont Scaled(OverlayFont font, float scale) => font with { Size = font.Size * scale };

    private float Glyph(OverlayPrompt prompt, OverlayInputScheme scheme, float x, float middle, float scale = 1)
    {
        var width = GlyphWidth(prompt, scheme, scale);
        var radius = U(OverlayStyle.PromptGlyphRadius * scale);
        var cx = x + width / 2;
        if (scheme == OverlayInputScheme.Keyboard)
        {
            var key = SKRect.Create(x, middle - U(14 * scale), width, U(28 * scale));
            RoundRect(key, U(6 * scale), Palette.Elevated);
            Outline(key, U(6 * scale), Palette.Faint);
            Rect(new SKRect(key.Left + U(5 * scale), key.Bottom - U(2.5f * scale), key.Right - U(5 * scale),
                key.Bottom - U(2.5f * scale) + Math.Max(1, Unit)), Palette.Faint);
            Text(prompt.KeyLabel, cx, middle + U(5 * scale), Scaled(OverlayStyle.KeyCap, scale), Palette.Text, width,
                SKTextAlign.Center);
            return width;
        }

        if (ShoulderLabel(prompt.Button, scheme) is { } shoulder)
        {
            var pill = SKRect.Create(x, middle - U(12 * scale), width, U(24 * scale));
            RoundRect(pill, U(12 * scale), Palette.Elevated);
            Outline(pill, U(12 * scale), Palette.Faint);
            Text(shoulder, cx, middle + U(5 * scale), Scaled(OverlayStyle.KeyCap, scale), Palette.Text, width,
                SKTextAlign.Center);
            return width;
        }

        if (scheme == OverlayInputScheme.Xbox && prompt.Button != OverlayButton.DirectionalPad)
        {
            var (fill, letter, ink) = prompt.Button switch
            {
                OverlayButton.Confirm => (XboxA, "A", SKColors.White),
                OverlayButton.Cancel => (XboxB, "B", SKColors.White),
                OverlayButton.Square => (XboxX, "X", SKColors.White),
                _ => (XboxY, "Y", new SKColor(0x1A, 0x1A, 0x1A)),
            };
            _paint.Style = SKPaintStyle.Fill;
            _paint.Color = fill;
            Canvas.DrawCircle(cx, middle, radius, _paint);
            Text(letter, cx, middle + U(6 * scale), new OverlayFont(17 * scale, OverlayWeight.Bold), ink, width, SKTextAlign.Center);
            return width;
        }

        _paint.Style = SKPaintStyle.Fill;
        _paint.Color = Palette.Elevated;
        Canvas.DrawCircle(cx, middle, radius, _paint);
        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = Math.Max(1, U(1.2f * scale));
        _paint.Color = Palette.Faint;
        Canvas.DrawCircle(cx, middle, radius, _paint);
        Symbol(prompt.Button, scheme, cx, middle, radius);
        _paint.Style = SKPaintStyle.Fill;
        return width;
    }

    private void Symbol(OverlayButton button, OverlayInputScheme scheme, float cx, float cy, float radius)
    {
        _paint.Style = SKPaintStyle.Stroke;
        _paint.StrokeWidth = Math.Max(1.2f, radius * 0.12f);
        _paint.StrokeCap = SKStrokeCap.Round;
        _paint.StrokeJoin = SKStrokeJoin.Round;
        _paint.Color = Palette.Text;
        var size = radius * 0.44f;
        using var path = new SKPath();
        switch (button)
        {
            case OverlayButton.Confirm:
                Canvas.DrawLine(cx - size, cy - size, cx + size, cy + size, _paint);
                Canvas.DrawLine(cx - size, cy + size, cx + size, cy - size, _paint);
                break;
            case OverlayButton.Cancel:
                Canvas.DrawCircle(cx, cy, size * 1.05f, _paint);
                break;
            case OverlayButton.Triangle:
                path.MoveTo(cx, cy - size * 1.15f);
                path.LineTo(cx + size * 1.1f, cy + size * 0.8f);
                path.LineTo(cx - size * 1.1f, cy + size * 0.8f);
                path.Close();
                Canvas.DrawPath(path, _paint);
                break;
            case OverlayButton.Square:
                Canvas.DrawRect(SKRect.Create(cx - size * 0.9f, cy - size * 0.9f, size * 1.8f, size * 1.8f), _paint);
                break;
            default:
                _paint.Style = SKPaintStyle.Fill;
                if (scheme == OverlayInputScheme.Xbox)
                {
                    var arm = size * 1.25f;
                    var thick = size * 0.62f;
                    Canvas.DrawRoundRect(SKRect.Create(cx - thick / 2, cy - arm, thick, arm * 2), Unit, Unit, _paint);
                    Canvas.DrawRoundRect(SKRect.Create(cx - arm, cy - thick / 2, arm * 2, thick), Unit, Unit, _paint);
                }
                else
                {
                    var half = size * 0.8f;
                    var gap = size * 0.25f;
                    path.MoveTo(cx, cy - gap - half * 1.5f);
                    path.LineTo(cx - half, cy - gap);
                    path.LineTo(cx + half, cy - gap);
                    path.Close();
                    path.MoveTo(cx, cy + gap + half * 1.5f);
                    path.LineTo(cx - half, cy + gap);
                    path.LineTo(cx + half, cy + gap);
                    path.Close();
                    Canvas.DrawPath(path, _paint);
                }

                break;
        }

        _paint.Style = SKPaintStyle.Fill;
    }
}
