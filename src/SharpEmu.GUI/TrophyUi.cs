// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using SharpEmu.GUI.Controls;

namespace SharpEmu.GUI;

internal static class TrophyUi
{
    private static readonly IBrush ChipBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
    private static readonly IBrush ChipBorderBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

    // A .zar game keeps its metadata, trophy package included, in its cache folder.
    public static string? GameDirectory(string gamePath) =>
        SharpEmu.HLE.FileSystem.ZArchive.IsArchivePath(gamePath)
            ? SharpEmu.HLE.FileSystem.ZArchiveCache.GetCacheRoot(gamePath)
            : Path.GetDirectoryName(gamePath);

    public static string TrophyLanguage =>
        SharpEmu.Libs.Np.TrophyLanguage.FromInterfaceLanguage(Localization.Instance.CurrentCode);

    public static string GradeName(int grade) =>
        Localization.Instance.Get($"Trophies.Grade.{Math.Clamp(grade, 1, 4)}");

    public static Bitmap? LoadIcon(byte[]? png, int width = 192)
    {
        if (png is null || png.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public static TextBlock Glyph(string name, double size, Color color) => new()
    {
        Classes = { "materialSymbol" },
        Text = name,
        FontSize = size,
        LineHeight = size + 2,
        Foreground = new SolidColorBrush(color),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Border Chip(string text)
    {
        var chip = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 5),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold },
        };
        var duration = TimeSpan.FromMilliseconds(160);
        chip.Transitions =
        [
            new BrushTransition { Property = Border.BackgroundProperty, Duration = duration },
            new BrushTransition { Property = Border.BorderBrushProperty, Duration = duration },
        ];
        return chip;
    }

    public static void SetChipSelected(Border chip, bool selected)
    {
        chip.Background = selected ? Brushes.White : ChipBrush;
        chip.BorderBrush = selected ? Brushes.White : ChipBorderBrush;
        if (chip.Child is TextBlock text)
        {
            text.Foreground = selected ? new SolidColorBrush(Color.Parse("#141414")) : Brushes.White;
        }
    }

    public static Border GradeChip(int grade, int earned, int total) => new()
    {
        CornerRadius = new CornerRadius(13),
        Padding = new Thickness(4, 3, 10, 3),
        Margin = new Thickness(0, 0, 8, 4),
        Background = ChipBrush,
        BorderBrush = ChipBorderBrush,
        BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TrophyMedal { Width = 18, Height = 18, Grade = grade, Opacity = earned == 0 ? 0.6 : 1 },
                Count(earned, total, 12),
            },
        },
    };

    public static TextBlock Count(int earned, int total, double size)
    {
        var text = new TextBlock { FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(earned.ToString()) { FontWeight = FontWeight.Bold });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run($" / {total}") { Foreground = new SolidColorBrush(Color.Parse("#8A8F99")) });
        return text;
    }

    public static Control StatusChip(string glyph, string text, Color color) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        HorizontalAlignment = HorizontalAlignment.Right,
        Children =
        {
            Glyph(glyph, 14, color),
            new TextBlock { Text = text, FontSize = 12, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center },
        },
    };

    public static void Lift(Border card)
    {
        IBrush? resting = null;
        card.PointerEntered += (_, _) =>
        {
            resting = card.BorderBrush;
            Translate(card, -2, TimeSpan.FromMilliseconds(170));
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        };
        card.PointerExited += (_, _) =>
        {
            Translate(card, 0, TimeSpan.FromMilliseconds(170));
            card.BorderBrush = resting;
        };
    }

    private static void Translate(Control control, double y, TimeSpan duration)
    {
        if (ElementComposition.GetElementVisual(control) is not { } visual)
        {
            return;
        }

        var animation = visual.Compositor.CreateVector3DKeyFrameAnimation();
        animation.Target = "Translation";
        animation.Duration = duration;
        animation.InsertExpressionKeyFrame(0f, "this.StartingValue");
        animation.InsertKeyFrame(1f, new Vector3D(0, y, 0), new CubicEaseOut());
        visual.StartAnimation("Translation", animation);
    }

    public static void FlipIn(Control card)
    {
        card.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        card.RenderTransform = TransformOperations.Parse("scaleY(0.04)");
        Dispatcher.UIThread.Post(() => card.RenderTransform = TransformOperations.Parse("scaleY(1)"), DispatcherPriority.Background);
    }

    public static void StaggerIn(IReadOnlyList<Control> rows)
    {
        var duration = TimeSpan.FromMilliseconds(320);
        for (var i = 0; i < rows.Count; i++)
        {
            if (ElementComposition.GetElementVisual(rows[i]) is not { } visual)
            {
                continue;
            }

            var delay = TimeSpan.FromMilliseconds(35 * Math.Min(i, 9));
            var easing = new CubicEaseOut();
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.Target = "Opacity";
            opacity.Duration = duration;
            opacity.DelayTime = delay;
            opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, easing);
            var translation = visual.Compositor.CreateVector3DKeyFrameAnimation();
            translation.Target = "Translation";
            translation.Duration = duration;
            translation.DelayTime = delay;
            translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            translation.InsertKeyFrame(0f, new Vector3D(0, 18, 0));
            translation.InsertKeyFrame(1f, new Vector3D(0, 0, 0), easing);
            visual.StartAnimation("Opacity", opacity);
            visual.StartAnimation("Translation", translation);
        }
    }

    private static readonly List<(System.Diagnostics.Stopwatch Clock, double From, double To, TimeSpan Duration, Action<double> Apply)> Animations = [];
    private static DispatcherTimer? _animationTimer;

    public static void Animate(double from, double to, TimeSpan duration, Action<double> apply)
    {
        apply(from);
        if (duration <= TimeSpan.FromMilliseconds(1) || from == to)
        {
            apply(to);
            return;
        }

        Animations.Add((System.Diagnostics.Stopwatch.StartNew(), from, to, duration, apply));
        if (_animationTimer is null)
        {
            _animationTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _animationTimer.Tick += (_, _) => StepAnimations();
        }

        _animationTimer.Start();
    }

    private static void StepAnimations()
    {
        for (var i = Animations.Count - 1; i >= 0; i--)
        {
            var (clock, from, to, duration, apply) = Animations[i];
            var t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            apply(from + (to - from) * (1 - Math.Pow(1 - t, 3)));
            if (t >= 1)
            {
                Animations.RemoveAt(i);
            }
        }

        if (Animations.Count == 0)
        {
            _animationTimer?.Stop();
        }
    }
}
