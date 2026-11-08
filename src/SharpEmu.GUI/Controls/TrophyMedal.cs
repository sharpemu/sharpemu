// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SharpEmu.GUI.Controls;

internal sealed class TrophyMedal : Control
{
    public static readonly StyledProperty<int> GradeProperty =
        AvaloniaProperty.Register<TrophyMedal, int>(nameof(Grade), 4);

    public static readonly StyledProperty<double> SpinProperty =
        AvaloniaProperty.Register<TrophyMedal, double>(nameof(Spin));

    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<TrophyMedal, IImage?>(nameof(Icon));

    public static readonly StyledProperty<double> GlintProperty =
        AvaloniaProperty.Register<TrophyMedal, double>(nameof(Glint), -1);

    private static readonly Geometry Cup = Geometry.Parse(
        "M31,24 H69 V40 C69,53 60,61 50,61 C40,61 31,53 31,40 Z " +
        "M31,28 H21 C21,40 26,46 33,47 L32,42 C28,41 26.5,37 26.5,33 H31 Z " +
        "M69,28 H79 C79,40 74,46 67,47 L68,42 C72,41 73.5,37 73.5,33 H69 Z " +
        "M45,60 H55 L56,70 H44 Z M35,70 H65 V79 H35 Z");

    private static readonly Geometry Star = Geometry.Parse(
        "M50,20 L58.8,38 L78.5,40.7 L64.2,54.5 L67.6,74.3 L50,65 L32.4,74.3 L35.8,54.5 L21.5,40.7 L41.2,38 Z");

    static TrophyMedal()
    {
        AffectsRender<TrophyMedal>(GradeProperty, SpinProperty, GlintProperty, IconProperty);
    }

    public int Grade
    {
        get => GetValue(GradeProperty);
        set => SetValue(GradeProperty, value);
    }

    public double Spin
    {
        get => GetValue(SpinProperty);
        set => SetValue(SpinProperty, value);
    }

    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public double Glint
    {
        get => GetValue(GlintProperty);
        set => SetValue(GlintProperty, value);
    }

    public static (Color Light, Color Mid, Color Dark) Palette(int grade) => grade switch
    {
        1 => (Color.Parse("#F2FBFF"), Color.Parse("#9ED8F2"), Color.Parse("#4D6FA8")),
        2 => (Color.Parse("#FFF4C2"), Color.Parse("#F2B93B"), Color.Parse("#94590C")),
        3 => (Color.Parse("#FFFFFF"), Color.Parse("#C3CAD5"), Color.Parse("#6C7584")),
        _ => (Color.Parse("#FFD9B0"), Color.Parse("#D0844A"), Color.Parse("#6E3714")),
    };

    // Brushes and pens depend only on the grade and the size, so a spin or glint animation reuses them every frame.
    private sealed class Look
    {
        public int Grade;
        public double Radius;
        public IBrush Rim = null!, Body = null!, Face = null!, EmblemShadow = null!, EmblemFill = null!, Highlight = null!;
        public Pen Ring = null!, ArtRing = null!, ArtHalo = null!;
    }

    private Look? _look;
    private EllipseGeometry? _artClip;
    private Rect _artClipRect;

    private Look LookFor(int grade, double radius)
    {
        if (_look is { } cached && cached.Grade == grade && cached.Radius == radius)
        {
            return cached;
        }

        var (light, mid, dark) = Palette(grade);
        return _look = new Look
        {
            Grade = grade,
            Radius = radius,
            Rim = new SolidColorBrush(Shade(dark, 0.55)),
            Body = Linear(light, mid, dark, 0.15, 0.1, 0.9, 0.95),
            Face = new RadialGradientBrush
            {
                Center = new RelativePoint(0.36, 0.3, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.36, 0.3, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.85, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.85, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(light, 0),
                    new GradientStop(mid, 0.45),
                    new GradientStop(dark, 1),
                },
            },
            EmblemShadow = new SolidColorBrush(Shade(dark, 0.75)),
            EmblemFill = Linear(light, light, mid, 0.3, 0.2, 0.7, 0.85),
            Highlight = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(150, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
                },
            },
            Ring = new Pen(new SolidColorBrush(Shade(dark, 0.8)), radius * 0.03),
            ArtRing = new Pen(new SolidColorBrush(Shade(dark, 0.8)), radius * 0.035),
            ArtHalo = new Pen(new SolidColorBrush(Color.FromArgb(120, light.R, light.G, light.B)), radius * 0.02),
        };
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2;
        var look = LookFor(Grade, radius);
        var facing = Math.Cos(Spin);
        var squash = Math.Max(0.04, Math.Abs(facing));
        var front = facing >= 0;

        var edgeShift = radius * 0.13 * Math.Sin(Spin);
        using (context.PushTransform(Squash(center, squash, edgeShift)))
        {
            context.DrawEllipse(look.Rim, null, center, radius, radius);
        }

        using (context.PushTransform(Squash(center, squash, 0)))
        {
            context.DrawEllipse(look.Body, null, center, radius, radius);
            context.DrawEllipse(null, look.Ring, center, radius * 0.86, radius * 0.86);

            var face = radius * 0.82;
            context.DrawEllipse(look.Face, null, center, face, face);

            if (front && Icon is { } icon)
            {
                var art = face * 0.9;
                var rect = new Rect(center.X - art, center.Y - art, art * 2, art * 2);
                if (_artClip is null || _artClipRect != rect)
                {
                    _artClip = new EllipseGeometry(rect);
                    _artClipRect = rect;
                }

                using (context.PushGeometryClip(_artClip))
                {
                    context.DrawImage(icon, new Rect(icon.Size), rect);
                }

                context.DrawEllipse(null, look.ArtRing, center, art, art);
                context.DrawEllipse(null, look.ArtHalo, center, art + radius * 0.03, art + radius * 0.03);
            }
            else
            {
                var emblem = front ? Cup : Star;
                var scale = face * 1.32 / 100;
                DrawEmblem(context, emblem, center, scale, new Vector(radius * 0.025, radius * 0.035), look.EmblemShadow);
                DrawEmblem(context, emblem, center, scale, default, look.EmblemFill);
            }

            context.DrawEllipse(look.Highlight, null, new Point(center.X - radius * 0.32, center.Y - radius * 0.4), radius * 0.42, radius * 0.24);

            if (Glint is >= 0 and <= 1)
            {
                var x = -1.2 + Glint * 2.4;
                var band = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), Math.Clamp(0.5 + x / 2 - 0.12, 0, 1)),
                        new GradientStop(Color.FromArgb(170, 255, 255, 255), Math.Clamp(0.5 + x / 2, 0, 1)),
                        new GradientStop(Color.FromArgb(0, 255, 255, 255), Math.Clamp(0.5 + x / 2 + 0.12, 0, 1)),
                    },
                };
                context.DrawEllipse(band, null, center, radius, radius);
            }
        }
    }

    private static void DrawEmblem(DrawingContext context, Geometry emblem, Point center, double scale, Vector offset, IBrush brush)
    {
        var matrix = Matrix.CreateTranslation(-50, -50) * Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(center.X + offset.X, center.Y + offset.Y);
        using (context.PushTransform(matrix))
        {
            context.DrawGeometry(brush, null, emblem);
        }
    }

    private static Matrix Squash(Point center, double squash, double shift) =>
        Matrix.CreateTranslation(-center.X, -center.Y) * Matrix.CreateScale(squash, 1) *
        Matrix.CreateTranslation(center.X + shift, center.Y);

    private static LinearGradientBrush Linear(Color a, Color b, Color c, double x0, double y0, double x1, double y1) => new()
    {
        StartPoint = new RelativePoint(x0, y0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(x1, y1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(a, 0),
            new GradientStop(b, 0.5),
            new GradientStop(c, 1),
        },
    };

    internal static Color Shade(Color color, double factor) =>
        Color.FromArgb(color.A, (byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));
}
