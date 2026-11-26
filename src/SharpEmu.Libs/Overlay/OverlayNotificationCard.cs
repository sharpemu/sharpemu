// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SkiaSharp;

namespace SharpEmu.Libs.Overlay;

public sealed record OverlayNotification(
    string Overline,
    string Tag,
    SKColor TagLight,
    SKColor TagDeep,
    string Title,
    string Description,
    byte[]? Icon,
    string? Footer = null,
    int Progress = 0,
    int Total = 0);

public readonly record struct OverlayAccent(double Offset, float Pan, float Strength);

public readonly record struct OverlayCelebration(
    double SinceImpact,
    double Charge,
    IReadOnlyList<OverlayAccent> Accents,
    bool Grand)
{
    public static OverlayCelebration Settled { get; } = new(10, 1, [], false);
}

public sealed class OverlayNotificationCard : IDisposable
{
    private const float Pad = 20;
    private const float IconSize = 92;
    private const float IconGap = 18;

    private readonly OverlayNotification _model;
    private readonly CultureInfo _culture;
    private readonly OverlayImages _images = new();
    private IReadOnlyList<string>? _description;
    private float _descriptionWidth = -1;

    public OverlayNotificationCard(OverlayNotification model, CultureInfo culture)
    {
        _model = model;
        _culture = culture;
    }

    public float Height(OverlayPainter p, float width)
    {
        var lines = Description(p, TextWidth(p, width));
        var text = p.U(14) + p.U(27) + lines.Count * p.U(21) + (_model.Total > 0 ? p.U(41) : p.U(8));
        return p.U(Pad) * 2 + Math.Max(p.U(IconSize), text);
    }

    public void Draw(OverlayPainter p, SKRect card, OverlayCelebration celebration, double progress)
    {
        var palette = p.Palette;
        var radius = p.U(OverlayStyle.CardRadius * 1.6f);
        p.Shadow(card, radius, 18, 8, 0x90);
        p.RoundRect(card, radius, palette.Background);
        p.Outline(card, radius, palette.Raised);

        var pad = p.U(Pad);
        var icon = SKRect.Create(card.Left + pad, card.Top + pad, p.U(IconSize), p.U(IconSize));
        var iconRadius = p.U(OverlayStyle.ImageRadius * 1.5f);
        DrawGlow(p, icon, iconRadius, celebration);
        p.Image(_images.FromBytes("icon", _model.Icon), icon, iconRadius, _model.Title);
        DrawImpact(p, icon, iconRadius, celebration);
        DrawBadge(p, icon, BadgeScale(celebration));
        DrawSparkles(p, icon, celebration);

        var textLeft = icon.Right + p.U(IconGap);
        var textWidth = TextWidth(p, card.Width);
        var baseline = card.Top + pad + p.U(14);
        var overline = _model.Overline.ToUpper(_culture);
        p.Text(overline, textLeft, baseline, OverlayStyle.Overline, palette.Muted, textWidth);
        if (!string.IsNullOrEmpty(_model.Tag))
        {
            var tagLeft = textLeft + p.Measure(overline, OverlayStyle.Overline) + p.U(10);
            p.RoundRect(SKRect.Create(tagLeft, baseline - p.U(5.5f), p.U(5), p.U(5)), p.U(2.5f), _model.TagLight);
            p.Text(_model.Tag.ToUpper(_culture), tagLeft + p.U(11), baseline, OverlayStyle.Overline, _model.TagLight,
                textLeft + textWidth - tagLeft - p.U(11));
        }

        baseline += p.U(27);
        p.Text(_model.Title, textLeft, baseline, OverlayStyle.Title, palette.Text, textWidth);
        foreach (var line in Description(p, textWidth))
        {
            baseline += p.U(21);
            p.Text(line, textLeft, baseline, OverlayStyle.Detail, palette.Secondary, textWidth);
        }

        if (_model.Total <= 0)
        {
            return;
        }

        var fill = Math.Clamp(progress, 0, 1);
        var reached = Math.Clamp(_model.Progress, 0, _model.Total);
        var before = Math.Max(0, reached - 1);
        baseline += p.U(28);
        var count = string.Create(_culture, $"{(fill > 0.5 ? reached : before)} / {_model.Total}");
        var countWidth = p.Measure(count, OverlayStyle.CaptionStrong);
        p.Text(_model.Footer, textLeft, baseline, OverlayStyle.Caption, palette.Muted, textWidth - countWidth - p.U(12));
        p.Text(count, textLeft + textWidth, baseline, OverlayStyle.CaptionStrong, palette.Secondary, countWidth + 1,
            SKTextAlign.Right);
        p.Progress(SKRect.Create(textLeft, baseline + p.U(9), textWidth, p.U(4)),
            (float)((before + (reached - before) * fill) / _model.Total));
    }

    public void Dispose() => _images.Dispose();

    private float TextWidth(OverlayPainter p, float cardWidth) =>
        cardWidth - p.U(Pad) * 2 - p.U(IconSize) - p.U(IconGap);

    private IReadOnlyList<string> Description(OverlayPainter p, float width)
    {
        if (_description is null || Math.Abs(_descriptionWidth - width) > 0.5f)
        {
            _description = p.Wrap(_model.Description, OverlayStyle.Detail, width, 2);
            _descriptionWidth = width;
        }

        return _description;
    }

    private static double BadgeScale(OverlayCelebration celebration)
    {
        var since = celebration.SinceImpact;
        return since < 0
            ? 0.35 + 0.25 * celebration.Charge
            : 1 + Math.Exp(-since * 7) * Math.Sin(since * 18) * 0.35;
    }

    private void DrawGlow(OverlayPainter p, SKRect icon, float radius, OverlayCelebration celebration)
    {
        var since = celebration.SinceImpact;
        var strength = since < 0
            ? 0.75 * Math.Pow(celebration.Charge, 1.5)
            : 0.28 + 0.72 * Math.Exp(-since * 2.6);
        if (celebration.Grand && since >= 0)
        {
            strength += 0.12 * (1 + Math.Sin(since * 5));
        }

        strength = Math.Clamp(strength, 0, 1);
        if (strength <= 0.01)
        {
            return;
        }

        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, p.U(10 + 12 * (float)strength));
        using var paint = new SKPaint
        {
            IsAntialias = true,
            MaskFilter = blur,
            Color = _model.TagLight.WithAlpha((byte)(strength * 210)),
        };
        var halo = icon;
        halo.Inflate(p.U(3), p.U(3));
        p.Canvas.DrawRoundRect(halo, radius + p.U(3), radius + p.U(3), paint);
    }

    private void DrawImpact(OverlayPainter p, SKRect icon, float radius, OverlayCelebration celebration)
    {
        var since = celebration.SinceImpact;
        if (since < 0 || since > 1.6)
        {
            return;
        }

        p.Canvas.Save();
        p.Canvas.ClipRoundRect(new SKRoundRect(icon, radius, radius), antialias: true);
        var flash = Math.Exp(-since * 9) * 0.6;
        if (flash > 0.01)
        {
            p.Rect(icon, SKColors.White.WithAlpha((byte)(flash * 255)));
        }

        var sweep = (since - 0.05) / 0.6;
        if (sweep is > 0 and < 1)
        {
            var eased = 1 - Math.Pow(1 - sweep, 3);
            var band = icon.Width * 0.38f;
            var center = icon.Left - band + (float)eased * (icon.Width + band * 2);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(center - band / 2, icon.Top),
                new SKPoint(center + band / 2, icon.Top + band * 0.4f),
                [SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(150), SKColors.White.WithAlpha(0)],
                [0f, 0.5f, 1f],
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { IsAntialias = true, Shader = shader };
            p.Canvas.DrawRect(icon, paint);
        }

        p.Canvas.Restore();
        Ring(p, icon, since);
        if (celebration.Grand)
        {
            Ring(p, icon, since - 0.38);
        }
    }

    private void Ring(OverlayPainter p, SKRect icon, double age)
    {
        const double Life = 0.6;
        if (age < 0 || age > Life)
        {
            return;
        }

        var progress = age / Life;
        var eased = (float)(1 - Math.Pow(1 - progress, 3));
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1, p.U(3) * (1 - eased * 0.6f)),
            Color = _model.TagLight.WithAlpha((byte)((1 - progress) * 230)),
        };
        var radius = icon.Width * 0.5f * (1.02f + 0.4f * eased);
        p.Canvas.DrawRoundRect(
            SKRect.Create(icon.MidX - radius, icon.MidY - radius, radius * 2, radius * 2),
            radius * 0.45f,
            radius * 0.45f,
            paint);
    }

    private void DrawSparkles(OverlayPainter p, SKRect icon, OverlayCelebration celebration)
    {
        const double Life = 0.65;
        var since = celebration.SinceImpact;
        if (since < 0)
        {
            return;
        }

        for (var index = 0; index < celebration.Accents.Count; index++)
        {
            var accent = celebration.Accents[index];
            var age = since - accent.Offset;
            if (age < 0 || age > Life)
            {
                continue;
            }

            var progress = age / Life;
            var jitter = ((index * 37) % 11 - 5) * 0.05;
            var angle = Math.PI * (0.95 + 0.8 * (accent.Pan + 0.8) / 1.6) + jitter;
            var reach = icon.Width * 0.5f * (0.8f + 0.55f * (float)(1 - Math.Pow(1 - progress, 2)));
            var cx = icon.MidX + (float)Math.Cos(angle) * reach;
            var cy = icon.MidY + (float)Math.Sin(angle) * reach;
            var twinkle = (float)Math.Sin(Math.PI * progress);
            var size = p.U(5 + 6 * accent.Strength) * twinkle;
            if (size <= 0.2f)
            {
                continue;
            }

            using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.9f))
            using (var halo = new SKPaint
                   {
                       IsAntialias = true,
                       MaskFilter = blur,
                       Color = _model.TagLight.WithAlpha((byte)(twinkle * 170)),
                   })
            {
                p.Canvas.DrawCircle(cx, cy, size * 0.9f, halo);
            }

            using var star = new SKPath();
            var waist = size * 0.22f;
            star.MoveTo(cx, cy - size);
            star.QuadTo(cx + waist, cy - waist, cx + size, cy);
            star.QuadTo(cx + waist, cy + waist, cx, cy + size);
            star.QuadTo(cx - waist, cy + waist, cx - size, cy);
            star.QuadTo(cx - waist, cy - waist, cx, cy - size);
            star.Close();
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha((byte)(Math.Min(1, twinkle * 1.4f) * 255)),
            };
            p.Canvas.DrawPath(star, fill);
        }
    }

    private void DrawBadge(OverlayPainter p, SKRect icon, double pop)
    {
        var radius = p.U(14) * (float)pop;
        if (radius <= 0)
        {
            return;
        }

        var cx = icon.Right - p.U(2);
        var cy = icon.Bottom - p.U(2);
        var rim = radius + p.U(3);
        p.RoundRect(SKRect.Create(cx - rim, cy - rim, rim * 2, rim * 2), rim, p.Palette.Background);
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(cx - radius, cy - radius),
            new SKPoint(cx + radius, cy + radius),
            [_model.TagLight, _model.TagDeep],
            SKShaderTileMode.Clamp);
        using var fill = new SKPaint { IsAntialias = true, Shader = shader };
        p.Canvas.DrawCircle(cx, cy, radius, fill);
        using var shine = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1, p.U(1.5f)),
            Color = SKColors.White.WithAlpha(0x70),
        };
        p.Canvas.DrawCircle(cx, cy, Math.Max(1, radius - p.U(3.5f)), shine);
    }
}
