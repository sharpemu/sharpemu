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

    // The track is the shared field brush, so it follows the theme; the fill is rebuilt only when the accent changes.
    private static readonly IBrush TrackBrush = LauncherThemes.Brush("FieldBrush");

    // The pens only change with the thickness or the accent, so a count-up animation does not rebuild them every frame.
    private Pen? _trackPen;
    private Pen? _fillPen;
    private Color _fillFrom;
    private Color _fillTo;

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

    private static Color AccentColor(string key) =>
        LauncherThemes.Brush(key) is ISolidColorBrush solid ? solid.Color : Colors.White;

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var radius = (size - Thickness) / 2;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var from = AccentColor("AccentBrush");
        var to = AccentColor("AccentHoverBrush");
        if (_trackPen is null || _trackPen.Thickness != Thickness || from != _fillFrom || to != _fillTo)
        {
            _fillFrom = from;
            _fillTo = to;
            _trackPen = new Pen(TrackBrush, Thickness);
            _fillPen = new Pen(
                new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
                },
                Thickness,
                lineCap: PenLineCap.Round);
        }

        context.DrawEllipse(null, _trackPen, center, radius, radius);
        var fraction = Math.Clamp(Value / 100, 0, 1);
        if (fraction <= 0)
        {
            return;
        }

        var pen = _fillPen!;
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
