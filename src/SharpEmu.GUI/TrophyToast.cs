// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using SharpEmu.GUI.Controls;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

internal sealed record TrophyToastContent(
    string Name,
    string Description,
    int Grade,
    byte[]? IconPng,
    string? GameName = null,
    int Earned = 0,
    int Total = 0);

internal sealed class TrophyToast : Window
{
    private const double WindowWidth = 840;
    private const double WindowHeight = 340;
    private const double CardWidth = 600;
    private const double CardHeight = 158;
    private const double CardMargin = 28;
    private const double MedalSize = 104;
    private const double MedalColumn = 140;
    private const double PillSize = 56;
    private const double CardRadius = 28;
    private const double PopSeconds = 0.22;
    private const double ExpandAt = 0.22;
    private const double TextAt = 0.55;
    private const double ExitSeconds = 0.45;
    private const double SpinStart = 0.15;
    private const double Impact = SpinStart + TrophySound.ImpactDelaySeconds;

    private static readonly Queue<TrophyToastContent> Pending = new();
    private static bool _showing;

    // A sparkle's whole flight is handed to the compositor as keyframes when it spawns; the clock only retires it.
    private sealed record Sparkle(Control Visual, double ExpiresAt);

    private const double SparkleSampleSeconds = 1.0 / 30;

    private static readonly Geometry SparkleShape = Geometry.Parse(
        "M50,0 C53,35 65,47 100,50 C65,53 53,65 50,100 C47,65 35,53 0,50 C35,47 47,35 50,0 Z");

    private readonly TrophyToastContent _content;
    private readonly Color _light, _mid, _dark;
    private readonly bool _reducedMotion = ReducedMotion();
    private readonly double _leaveAt;
    private readonly Stopwatch _clock = new();
    private readonly Random _random = new();
    private readonly List<Sparkle> _sparkles = [];
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Grid _cardHost = new();
    private readonly Border _cardBorder = new();
    private readonly Border _edgeBorder = new();
    private readonly TranslateTransform _cardShift = new();
    private readonly ScaleTransform _cardScale = new();
    private readonly TranslateTransform _medalShift = new();
    private readonly Border _glow = new();
    private readonly TrophyMedal _medal = new() { Width = MedalSize, Height = MedalSize };
    private readonly ScaleTransform _medalScale = new();
    private readonly Border _burst = new();
    private readonly Canvas _rays = new() { IsHitTestVisible = false };
    private readonly Border _ring = new();
    private readonly ScaleTransform _ringScale = new();
    private readonly Border _shine = new();
    private readonly TranslateTransform _shineShift = new();
    private readonly TranslateTransform _auroraA = new();
    private readonly TranslateTransform _auroraB = new();
    private readonly RotateTransform _edgeSpin = new();
    private readonly Border _progressFill = new();
    private readonly ScaleTransform _progressScale = new(0, 1);
    private readonly TextBlock _progressText = new();
    private readonly List<(Control Line, TranslateTransform Shift, double Delay)> _lines = [];
    private bool _chimed;
    private bool _burstDone;
    private bool _secondBurstDone;
    private bool _closed;

    public static void Show(TrophyToastContent content)
    {
        Pending.Enqueue(content);
        ShowNext();
    }

    private static void ShowNext()
    {
        if (_showing || !Pending.TryDequeue(out var next))
        {
            return;
        }

        _showing = true;
        var toast = new TrophyToast(next);
        toast.Closed += (_, _) =>
        {
            _showing = false;
            ShowNext();
        };
        toast.Opacity = 0;
        toast.Show();
        toast.Place();
        toast.MakeClickThrough();
        toast.Start();
    }

    private TrophyToast(TrophyToastContent content)
    {
        _content = content;
        (_light, _mid, _dark) = TrophyMedal.Palette(content.Grade);
        _leaveAt = content.Grade == 1 ? 4.0 : 3.5;

        Width = WindowWidth;
        Height = WindowHeight;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];

        Content = BuildContent();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private static double CardLeft => WindowWidth - CardMargin - CardWidth;

    private static Point MedalCenter => new(CardLeft + MedalColumn / 2, CardMargin + CardHeight / 2);

    private Control BuildContent()
    {
        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(BuildCard());
        root.Children.Add(_overlay);

        _ring.Width = MedalSize;
        _ring.Height = MedalSize;
        _ring.CornerRadius = new CornerRadius(MedalSize / 2);
        _ring.BorderBrush = new SolidColorBrush(_light);
        _ring.BorderThickness = new Thickness(3);
        _ring.Opacity = 0;
        _ring.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _ring.RenderTransform = _ringScale;
        _ring.BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(200, _mid.R, _mid.G, _mid.B) });
        Canvas.SetLeft(_ring, MedalCenter.X - MedalSize / 2);
        Canvas.SetTop(_ring, MedalCenter.Y - MedalSize / 2);
        _overlay.Children.Add(_ring);
        return root;
    }

    private Control BuildCard()
    {
        var (deep, deeper) = _content.Grade switch
        {
            1 => (Color.Parse("#1F4466"), Color.Parse("#2B1A52")),
            2 => (Color.Parse("#5A3F0C"), Color.Parse("#2A1838")),
            3 => (Color.Parse("#39424F"), Color.Parse("#1B2030")),
            _ => (Color.Parse("#5A2E17"), Color.Parse("#26172E")),
        };

        var aurora = new Canvas { IsHitTestVisible = false };
        aurora.Children.Add(Blob(_mid, 150, 360, -60, -170, _auroraA));
        aurora.Children.Add(Blob(Color.Parse("#8C6CFF"), 110, 320, 330, -60, _auroraB));

        var glass = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(34, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55),
                },
            },
        };

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{MedalColumn},*"),
            Width = CardWidth,
            Height = CardHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        content.Children.Add(BuildMedalArea());
        var text = BuildText();
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        _shine.Width = 140;
        _shine.Height = CardHeight * 2.2;
        _shine.HorizontalAlignment = HorizontalAlignment.Left;
        _shine.VerticalAlignment = VerticalAlignment.Center;
        _shine.IsHitTestVisible = false;
        _shine.Opacity = 0;
        _shine.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(60, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        _shine.RenderTransform = new TransformGroup { Children = { new RotateTransform(20), _shineShift } };

        var layers = new Grid();
        layers.Children.Add(aurora);
        layers.Children.Add(glass);
        layers.Children.Add(content);
        layers.Children.Add(_shine);

        var card = _cardBorder;
        card.CornerRadius = new CornerRadius(CardRadius);
        card.ClipToBounds = true;
        card.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(245, deep.R, deep.G, deep.B), 0),
                new GradientStop(Color.FromArgb(245, deeper.R, deeper.G, deeper.B), 1),
            },
        };
        card.Child = layers;

        var edgeBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            Transform = _edgeSpin,
            TransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        };
        if (_content.Grade == 1)
        {
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#E08FE3FF"), 0));
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#C09B7CFF"), 0.35));
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#B0FF8FD8"), 0.65));
            edgeBrush.GradientStops.Add(new GradientStop(Color.Parse("#E08FE3FF"), 1));
        }
        else
        {
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(220, _light.R, _light.G, _light.B), 0));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(90, _mid.R, _mid.G, _mid.B), 0.45));
            edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(30, 255, 255, 255), 1));
        }

        var edge = _edgeBorder;
        edge.CornerRadius = new CornerRadius(CardRadius);
        edge.BorderBrush = edgeBrush;
        edge.BorderThickness = new Thickness(1.5);
        edge.IsHitTestVisible = false;

        _glow.CornerRadius = new CornerRadius(CardRadius);
        _glow.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        _glow.BoxShadow = new BoxShadows(
            new BoxShadow { OffsetY = 16, Blur = 36, Color = Color.FromArgb(150, 0, 0, 0) },
            [new BoxShadow { Blur = _content.Grade == 1 ? 56 : 38, Spread = 2, Color = Color.FromArgb(_content.Grade == 1 ? (byte)170 : (byte)120, _mid.R, _mid.G, _mid.B) }]);

        _cardHost.Width = CardWidth;
        _cardHost.Height = CardHeight;
        _cardHost.HorizontalAlignment = HorizontalAlignment.Right;
        _cardHost.VerticalAlignment = VerticalAlignment.Top;
        _cardHost.Margin = new Thickness(0, CardMargin, CardMargin, 0);
        _cardHost.RenderTransformOrigin = new RelativePoint(1, 0, RelativeUnit.Relative);
        _cardHost.RenderTransform = new TransformGroup { Children = { _cardScale, _cardShift } };
        _cardHost.Children.Add(_glow);
        _cardHost.Children.Add(card);
        _cardHost.Children.Add(edge);
        return _cardHost;
    }

    private Control BuildMedalArea()
    {
        var area = new Grid { Width = MedalColumn, Height = CardHeight };

        _burst.Width = MedalSize * 1.7;
        _burst.Height = MedalSize * 1.7;
        _burst.CornerRadius = new CornerRadius(MedalSize);
        _burst.HorizontalAlignment = HorizontalAlignment.Center;
        _burst.VerticalAlignment = VerticalAlignment.Center;
        _burst.Background = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(200, _light.R, _light.G, _light.B), 0),
                new GradientStop(Color.FromArgb(70, _mid.R, _mid.G, _mid.B), 0.45),
                new GradientStop(Color.FromArgb(0, _mid.R, _mid.G, _mid.B), 1),
            },
        };
        _burst.Opacity = 0.35;
        area.Children.Add(_burst);

        if (_content.Grade <= 2 && !_reducedMotion)
        {
            BuildRays();
            area.Children.Add(_rays);
        }

        _medal.Grade = _content.Grade;
        _medal.Icon = TrophyUi.LoadIcon(_content.IconPng);
        _medal.HorizontalAlignment = HorizontalAlignment.Center;
        _medal.VerticalAlignment = VerticalAlignment.Center;
        _medal.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _medal.RenderTransform = new TransformGroup { Children = { _medalScale, _medalShift } };
        area.Children.Add(_medal);
        return area;
    }

    private Control BuildText()
    {
        var label = new TextBlock
        {
            Text = Localization.Instance.Get("Trophies.Unlocked").ToUpperInvariant(),
            Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            FontSize = 11.5,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 2, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(_light, 0), new GradientStop(_mid, 1) },
            },
            Child = new TextBlock
            {
                Text = TrophyUi.GradeName(_content.Grade).ToUpperInvariant(),
                Foreground = new SolidColorBrush(TrophyMedal.Shade(_dark, 0.55)),
                FontSize = 10.5,
                FontWeight = FontWeight.Black,
                LetterSpacing = 1.2,
            },
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { label, pill } };
        var name = new TextBlock
        {
            Text = _content.Name,
            Foreground = Brushes.White,
            FontSize = 21,
            FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var description = new TextBlock
        {
            Text = _content.Description,
            Foreground = new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stack = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(0, 16, 24, 14),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { header, name, description },
        };
        if (_content.Total > 0)
        {
            stack.Children.Add(BuildProgress());
        }

        for (var i = 0; i < stack.Children.Count; i++)
        {
            var shift = new TranslateTransform();
            stack.Children[i].RenderTransform = shift;
            _lines.Add((stack.Children[i], shift, TextAt + i * 0.09));
        }

        return stack;
    }

    private Control BuildProgress()
    {
        var game = new TextBlock
        {
            Text = _content.GameName ?? string.Empty,
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _progressText.Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
        _progressText.FontSize = 11.5;
        _progressText.FontWeight = FontWeight.SemiBold;
        _progressText.Text = $"{Math.Max(0, _content.Earned - 1)} / {_content.Total}";
        var labels = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        labels.Children.Add(game);
        Grid.SetColumn(_progressText, 1);
        labels.Children.Add(_progressText);

        // The fill is a full-width bar scaled from its left edge, so progress never triggers a layout pass.
        _progressFill.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        _progressFill.RenderTransform = _progressScale;
        _progressScale.ScaleX = PercentOf(_content.Earned - 1) / 100;
        _progressFill.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops = { new GradientStop(_mid, 0), new GradientStop(_light, 1) },
        };
        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)),
            Child = _progressFill,
        };

        return new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0), Children = { labels, track } };
    }

    private double PercentOf(int earned) => _content.Total == 0 ? 0 : Math.Clamp(earned, 0, _content.Total) * 100.0 / _content.Total;

    private static Control Blob(Color color, byte alpha, double size, double left, double top, TranslateTransform shift)
    {
        var blob = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            },
            RenderTransform = shift,
        };
        Canvas.SetLeft(blob, left);
        Canvas.SetTop(blob, top);
        return blob;
    }

    private void BuildRays()
    {
        const double length = 120;
        _rays.Width = MedalColumn;
        _rays.Height = CardHeight;
        _rays.Opacity = 0;
        var count = _content.Grade == 1 ? 16 : 10;
        for (var i = 0; i < count; i++)
        {
            var ray = new Border
            {
                Width = i % 2 == 0 ? 10 : 5,
                Height = length,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0, _light.R, _light.G, _light.B), 0),
                        new GradientStop(Color.FromArgb(120, _light.R, _light.G, _light.B), 1),
                    },
                },
                RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                RenderTransform = new RotateTransform(i * 360.0 / count),
            };
            Canvas.SetLeft(ray, MedalColumn / 2 - ray.Width / 2);
            Canvas.SetTop(ray, CardHeight / 2 - length);
            _rays.Children.Add(ray);
        }
    }

    private void Start()
    {
        _clock.Start();
        if (!_reducedMotion)
        {
            StartAmbientAnimations();
        }

        Tick();
        this.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_closed && Tick())
        {
            this.RequestAnimationFrame(OnFrame);
        }
    }

    // The endless loops run on the render thread, so they keep their pace whatever the UI thread is doing.
    private void StartAmbientAnimations()
    {
        TrophyUi.WithVisual(_glow, glow =>
        {
            const int steps = 8;
            var pulse = glow.Compositor.CreateScalarKeyFrameAnimation();
            pulse.Target = "Opacity";
            pulse.Duration = TimeSpan.FromSeconds(2 * Math.PI / 2.4);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            for (var i = 0; i <= steps; i++)
            {
                var phase = (double)i / steps;
                pulse.InsertKeyFrame((float)phase, (float)(0.75 + 0.25 * Math.Sin(phase * 2 * Math.PI)));
            }

            glow.StartAnimation("Opacity", pulse);
        });

        if (_rays.Children.Count > 0)
        {
            TrophyUi.WithVisual(_rays, rays =>
            {
                var spin = rays.Compositor.CreateScalarKeyFrameAnimation();
                spin.Target = "RotationAngle";
                spin.Duration = TimeSpan.FromSeconds(20);
                spin.IterationBehavior = AnimationIterationBehavior.Forever;
                spin.InsertKeyFrame(0f, 0f);
                spin.InsertKeyFrame(1f, (float)(2 * Math.PI));
                rays.CenterPoint = new Vector3D(MedalColumn / 2, CardHeight / 2, 0);
                rays.StartAnimation("RotationAngle", spin);
            });
        }
    }

    private bool Tick()
    {
        var t = _clock.Elapsed.TotalSeconds;
        if (t >= _leaveAt + ExitSeconds)
        {
            Close();
            return false;
        }

        var leaving = t > _leaveAt;
        var leave = leaving ? Math.Clamp((t - _leaveAt) / ExitSeconds, 0, 1) : 0;
        if (t >= SpinStart && !_chimed)
        {
            _chimed = true;
            TrophySound.Play(_content.Grade);
        }

        if (_reducedMotion)
        {
            Opacity = leaving ? 1 - leave : Math.Min(1, t / 0.25);
            foreach (var (line, _, _) in _lines)
            {
                line.Opacity = 1;
            }

            _medal.Spin = 0;
            _medalScale.ScaleX = _medalScale.ScaleY = 1;
            if (_content.Total > 0)
            {
                _progressScale.ScaleX = PercentOf(_content.Earned) / 100;
                _progressText.Text = $"{_content.Earned} / {_content.Total}";
            }

            return true;
        }

        Opacity = 1;
        var pop = EaseOutCubic(Math.Clamp(t / PopSeconds, 0, 1));
        var textOut = EaseInOutCubic(Math.Clamp(leave / 0.33, 0, 1));
        var fold = EaseInOutCubic(Math.Clamp((leave - 0.2) / 0.55, 0, 1));
        var melt = EaseInOutCubic(Math.Clamp((leave - 0.65) / 0.35, 0, 1));
        var openW = leaving ? 1 - fold : Math.Max(0, Spring(t - ExpandAt, 11, 0.74));
        var openH = leaving ? 1 - fold : Math.Max(0, Spring(t - ExpandAt - 0.04, 10, 0.8));

        // The pill-to-card morph is the one layout-driven step: it lasts about half a second at each end, and the
        // subtree it re-arranges is fixed-size.
        var width = PillSize + (CardWidth - PillSize) * openW;
        var height = PillSize + (CardHeight - PillSize) * openH;
        _cardHost.Width = width;
        _cardHost.Height = height;
        var radius = new CornerRadius(Math.Min(CardRadius, Math.Min(width, height) / 2));
        _cardBorder.CornerRadius = radius;
        _edgeBorder.CornerRadius = radius;
        _glow.CornerRadius = radius;
        _cardHost.Opacity = Math.Min(1, t / 0.2) * (1 - melt);
        _cardShift.Y = -10 * melt;

        var spin = Math.Clamp((t - SpinStart) / (Impact - SpinStart), 0, 1);
        _medal.Spin = (1 - EaseOutQuart(spin)) * Math.PI * 6;
        _medalShift.X = (PillSize / 2 - MedalColumn / 2) * (1 - openW);
        _medalShift.Y = (PillSize / 2 - CardHeight / 2) * (1 - openH);
        var grow = (PillSize * 0.86 / MedalSize) + (1 - PillSize * 0.86 / MedalSize) * openW;
        var sinceImpact = t - Impact;
        var thump = sinceImpact >= 0 ? Math.Exp(-sinceImpact * 9) * Math.Cos(sinceImpact * 28) * 0.14 : 0;
        _medalScale.ScaleX = _medalScale.ScaleY = grow + thump;
        _medal.Glint = sinceImpact is >= 0.05 and <= 0.75 ? (sinceImpact - 0.05) / 0.7 : -1;

        var landing = sinceImpact >= 0 && !leaving ? 1 + Math.Exp(-sinceImpact * 10) * Math.Sin(sinceImpact * 30) * 0.012 : 1;
        _cardScale.ScaleX = _cardScale.ScaleY = (0.7 + 0.3 * pop) * (1 - 0.25 * melt) * landing;
        _burst.Opacity = sinceImpact >= 0
            ? 0.4 + 0.6 * Math.Exp(-sinceImpact * 3.5) + 0.08 * Math.Sin(t * 3)
            : 0.25 + 0.25 * spin;
        if (sinceImpact >= 0 && !_burstDone)
        {
            _burstDone = true;
            SpawnSparkles(t, _content.Grade switch { 1 => 26, 2 => 18, 3 => 14, _ => 10 }, _content.Grade == 1 ? 1.25 : 1.0);
        }

        if (_content.Grade == 1 && sinceImpact >= 0.55 && !_secondBurstDone)
        {
            _secondBurstDone = true;
            SpawnSparkles(t, 16, 0.8);
        }

        var ring = sinceImpact / 0.7;
        _ring.Opacity = ring is >= 0 and <= 1 ? 0.95 * (1 - ring) : 0;
        if (ring is >= 0 and <= 1)
        {
            _ringScale.ScaleX = _ringScale.ScaleY = 1 + EaseOutCubic(ring) * (_content.Grade == 1 ? 1.9 : 1.2);
        }

        _rays.Opacity = sinceImpact >= 0 ? Math.Min(1, sinceImpact * 3) * (1 - textOut) : 0;

        foreach (var (line, shift, delay) in _lines)
        {
            var progress = EaseOutCubic(Math.Clamp((t - delay) / 0.35, 0, 1));
            shift.Y = (1 - progress) * 10 - textOut * 4;
            line.Opacity = progress * (1 - textOut);
        }

        if (_content.Total > 0)
        {
            var fill = Math.Clamp((sinceImpact - 0.15) / 0.8, 0, 1);
            var before = PercentOf(_content.Earned - 1);
            _progressScale.ScaleX = (before + (PercentOf(_content.Earned) - before) * EaseOutCubic(fill)) / 100;
            _progressText.Text = $"{(fill > 0.5 ? _content.Earned : Math.Max(0, _content.Earned - 1))} / {_content.Total}";
        }

        var sweep = (sinceImpact - 0.25) / 0.85;
        _shine.Opacity = sweep is >= 0 and <= 1 ? 1 : 0;
        _shineShift.X = -180 + EaseInOutCubic(Math.Clamp(sweep, 0, 1)) * (CardWidth + 320);

        _auroraA.X = Math.Sin(t * 0.7) * 60;
        _auroraA.Y = Math.Cos(t * 0.5) * 25;
        _auroraB.X = Math.Cos(t * 0.6) * 70;
        _auroraB.Y = Math.Sin(t * 0.8) * 30;
        if (_content.Grade == 1)
        {
            _edgeSpin.Angle = t * 40;
        }

        RetireSparkles(t);
        return true;
    }

    private void SpawnSparkles(double now, int count, double reach)
    {
        Color[] colors = _content.Grade == 1
            ? [_light, Colors.White, Color.Parse("#C9B8FF"), Color.Parse("#FFC4EC")]
            : [_light, Colors.White, _light, _mid];
        var looks = new Dictionary<Color, (IBrush Fill, IBrush Halo)>();
        var origin = MedalCenter;
        for (var i = 0; i < count; i++)
        {
            var angle = (i + _random.NextDouble() * 0.8) / count * Math.PI * 2;
            var speed = (90 + _random.NextDouble() * 170) * reach;
            var dot = _random.NextDouble() < 0.3;
            var size = dot ? 3 + _random.NextDouble() * 2 : 9 + _random.NextDouble() * (_content.Grade == 1 ? 12 : 8);
            var color = colors[_random.Next(colors.Length)];
            if (!looks.TryGetValue(color, out var look))
            {
                look = (new SolidColorBrush(color), new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(150, color.R, color.G, color.B), 0),
                        new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                    },
                });
                looks[color] = look;
            }

            // The soft halo is a gradient disc rather than a drop shadow, which would need an offscreen pass per frame.
            var box = size * 2.6;
            Control body = dot
                ? new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = look.Fill }
                : new Avalonia.Controls.Shapes.Path { Data = SparkleShape, Width = size, Height = size, Stretch = Stretch.Fill, Fill = look.Fill };
            body.HorizontalAlignment = HorizontalAlignment.Center;
            body.VerticalAlignment = VerticalAlignment.Center;
            var visual = new Grid
            {
                Width = box,
                Height = box,
                IsHitTestVisible = false,
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(box / 2), Background = look.Halo },
                    body,
                },
            };
            Canvas.SetLeft(visual, origin.X - box / 2);
            Canvas.SetTop(visual, origin.Y - box / 2);
            _overlay.Children.Add(visual);

            var life = 0.55 + _random.NextDouble() * 0.5;
            AnimateSparkle(
                visual,
                new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed),
                life,
                _random.NextDouble() * 45,
                (_random.NextDouble() - 0.5) * 240,
                14 + _random.NextDouble() * 12);
            _sparkles.Add(new Sparkle(visual, now + life + 0.1));
        }
    }

    private static void AnimateSparkle(Control visual, Vector velocity, double life, double rotation, double spin, double twinkle)
    {
        // Closed form of the original drag-and-lift integration: velocity decays by dragRate and an upward drift fights it.
        const double dragRate = 5.5;
        const double lift = 30;
        var steps = (int)Math.Ceiling(life / SparkleSampleSeconds);
        TrophyUi.WithVisual(visual, composition =>
        {
            var compositor = composition.Compositor;
            var move = compositor.CreateVector3DKeyFrameAnimation();
            var scale = compositor.CreateVector3DKeyFrameAnimation();
            var fade = compositor.CreateScalarKeyFrameAnimation();
            var turn = compositor.CreateScalarKeyFrameAnimation();
            move.Target = "Translation";
            scale.Target = "Scale";
            fade.Target = "Opacity";
            turn.Target = "RotationAngle";
            move.Duration = scale.Duration = fade.Duration = turn.Duration = TimeSpan.FromSeconds(life);
            for (var i = 0; i <= steps; i++)
            {
                var fraction = (double)i / steps;
                var age = fraction * life;
                var decay = 1 - Math.Exp(-dragRate * age);
                var x = velocity.X / dragRate * decay;
                var y = (velocity.Y + lift / dragRate) / dragRate * decay - lift / dragRate * age;
                var twinkling = 0.8 + 0.2 * Math.Sin(age * twinkle);
                var size = Math.Min(1, fraction * 6) * twinkling * (1 - 0.5 * fraction);
                move.InsertKeyFrame((float)fraction, new Vector3D(x, y, 0));
                scale.InsertKeyFrame((float)fraction, new Vector3D(size, size, 1));
                fade.InsertKeyFrame((float)fraction, (float)(1 - EaseInCubic(fraction)));
            }

            turn.InsertKeyFrame(0f, (float)(rotation * Math.PI / 180));
            turn.InsertKeyFrame(1f, (float)((rotation + spin * life) * Math.PI / 180));
            composition.CenterPoint = new Vector3D(visual.Width / 2, visual.Height / 2, 0);
            composition.StartAnimation("Translation", move);
            composition.StartAnimation("Scale", scale);
            composition.StartAnimation("Opacity", fade);
            composition.StartAnimation("RotationAngle", turn);
        }, () => visual.IsVisible = false);
    }

    private void RetireSparkles(double now)
    {
        for (var i = _sparkles.Count - 1; i >= 0; i--)
        {
            if (now >= _sparkles[i].ExpiresAt)
            {
                _overlay.Children.Remove(_sparkles[i].Visual);
                _sparkles.RemoveAt(i);
            }
        }
    }

    private static double Spring(double t, double omega, double zeta)
    {
        if (t <= 0)
        {
            return 0;
        }

        var damped = omega * Math.Sqrt(1 - zeta * zeta);
        return 1 - Math.Exp(-zeta * omega * t) * (Math.Cos(damped * t) + zeta * omega / damped * Math.Sin(damped * t));
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double EaseOutQuart(double t) => 1 - Math.Pow(1 - t, 4);

    private static double EaseInCubic(double t) => t * t * t;

    private static double EaseInOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private void Place()
    {
        var screen = Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scale = screen.Scaling;
        var area = screen.WorkingArea;
        Position = new PixelPoint(area.Right - (int)(Width * scale), area.Y);
    }

    private void MakeClickThrough()
    {
        if (!OperatingSystem.IsWindows() || TryGetPlatformHandle() is not { } handle)
        {
            return;
        }

        const int gwlExStyle = -20;
        const long wsExTransparent = 0x20;
        const long wsExNoActivate = 0x08000000;
        const long wsExToolWindow = 0x80;
        var style = GetWindowLongPtr(handle.Handle, gwlExStyle).ToInt64();
        SetWindowLongPtr(handle.Handle, gwlExStyle, new IntPtr(style | wsExTransparent | wsExNoActivate | wsExToolWindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    private static bool ReducedMotion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        const uint spiGetClientAreaAnimation = 0x1042;
        var enabled = 1;
        return SystemParametersInfo(spiGetClientAreaAnimation, 0, ref enabled, 0) && enabled == 0;
    }
}
