// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SharpEmu.GUI.Controls;

internal enum PadSymbol
{
    Cross,
    Circle,
    Triangle,
    Square,
}

/// <summary>
/// A monochrome PlayStation face-button glyph (cross, circle, triangle, square) drawn as
/// shapes inside a thin ring, so it needs no font and stays crisp at any scale.
/// </summary>
internal sealed class PadGlyph : Control
{
    public static readonly StyledProperty<PadSymbol> SymbolProperty =
        AvaloniaProperty.Register<PadGlyph, PadSymbol>(nameof(Symbol));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<PadGlyph, IBrush?>(nameof(Foreground), Brushes.White);

    static PadGlyph()
    {
        AffectsRender<PadGlyph>(SymbolProperty, ForegroundProperty);
        AffectsMeasure<PadGlyph>(WidthProperty, HeightProperty);
        WidthProperty.OverrideDefaultValue<PadGlyph>(28);
        HeightProperty.OverrideDefaultValue<PadGlyph>(28);
        IsHitTestVisibleProperty.OverrideDefaultValue<PadGlyph>(false);
    }

    public PadSymbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0 || Foreground is not { } brush)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var ring = new Pen(brush, 1.5) { LineCap = PenLineCap.Round };
        context.DrawEllipse(null, ring, centre, size / 2 - 1, size / 2 - 1);

        // Symbols sit in a box 0.34 of the ring size either side of the centre.
        var r = size * 0.19;
        var pen = new Pen(brush, 2) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (Symbol)
        {
            case PadSymbol.Cross:
                context.DrawLine(pen, new Point(centre.X - r, centre.Y - r), new Point(centre.X + r, centre.Y + r));
                context.DrawLine(pen, new Point(centre.X + r, centre.Y - r), new Point(centre.X - r, centre.Y + r));
                break;
            case PadSymbol.Circle:
                context.DrawEllipse(null, pen, centre, r * 1.15, r * 1.15);
                break;
            case PadSymbol.Square:
                context.DrawRectangle(null, pen, new Rect(centre.X - r, centre.Y - r, r * 2, r * 2));
                break;
            case PadSymbol.Triangle:
                // Equilateral, optically centred (the centroid, not the bounding box, sits on the centre).
                var h = r * 2.1;
                var top = new Point(centre.X, centre.Y - h * 0.62);
                var left = new Point(centre.X - h * 0.58, centre.Y + h * 0.38);
                var right = new Point(centre.X + h * 0.58, centre.Y + h * 0.38);
                var triangle = new StreamGeometry();
                using (var g = triangle.Open())
                {
                    g.BeginFigure(top, false);
                    g.LineTo(right);
                    g.LineTo(left);
                    g.EndFigure(true);
                }

                context.DrawGeometry(null, pen, triangle);
                break;
        }
    }
}
