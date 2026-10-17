// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SharpEmu.GUI.Controls;

internal sealed class TrophyProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<TrophyProgressRing, double>(nameof(Value));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<TrophyProgressRing, double>(nameof(Thickness), 8);

    static TrophyProgressRing()
    {
        AffectsRender<TrophyProgressRing>(ValueProperty, ThicknessProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var radius = (size - Thickness) / 2;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Thickness), center, radius, radius);
        var fraction = Math.Clamp(Value / 100, 0, 1);
        if (fraction <= 0)
        {
            return;
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#8FE3FF"), 0),
                new GradientStop(Color.Parse("#9B7CFF"), 0.55),
                new GradientStop(Color.Parse("#FF8FD8"), 1),
            },
        };
        var pen = new Pen(brush, Thickness, lineCap: PenLineCap.Round);
        if (fraction >= 0.9999)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var end = -Math.PI / 2 + fraction * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(new Point(center.X, center.Y - radius), false);
            stream.ArcTo(
                new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end)),
                new Size(radius, radius),
                0,
                fraction > 0.5,
                SweepDirection.Clockwise);
            stream.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
