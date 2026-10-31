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
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using SharpEmu.GUI.Controls;

namespace SharpEmu.GUI;

internal static class TrophyUi
{
    // Shared token brushes: they are repainted in place when the theme changes, so nothing here copies a colour.
    private static IBrush Token(string key) => LauncherThemes.Brush(key);

    public static CornerRadius CardRadius =>
        Avalonia.Application.Current?.TryGetResource("RadiusLg", null, out var value) == true && value is CornerRadius radius
            ? radius
            : new CornerRadius(14);

    public static BoxShadows GlassShadow =>
        Avalonia.Application.Current?.TryGetResource("GlassShadow", null, out var value) == true && value is BoxShadows shadows
            ? shadows
            : default;

    // The one selected/focused state for a card: a 2px accent ring laid over it, faded in so nothing shifts.
    public static Border SelectionRing() => new()
    {
        IsHitTestVisible = false,
        Opacity = 0,
        CornerRadius = CardRadius,
        BorderThickness = new Thickness(2),
        // The theme's focus ring, as on a selected library cover.
        BorderBrush = Token("FocusRingBrush"),
        Transitions = [new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(140) }],
    };

    // Hover wash: the theme's overlay colour, faded in over the card.
    public static Border HoverWash() => new()
    {
        IsHitTestVisible = false,
        Opacity = 0,
        Background = Token("HoverOverlayBrush"),
        Transitions = [new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }],
    };

    // A .zar game keeps its metadata, trophy package included, in its cache folder.
    public static string? GameDirectory(string gamePath) =>
        SharpEmu.HLE.FileSystem.ZArchive.IsArchivePath(gamePath)
            ? SharpEmu.HLE.FileSystem.ZArchiveCache.GetCacheRoot(gamePath,
                Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "zar-cache"))
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

    public static TextBlock Glyph(string name, double size, IBrush brush) => new()
    {
        Classes = { "materialSymbol" },
        Text = name,
        FontSize = size,
        LineHeight = size + 2,
        Foreground = brush,
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
        chip.Background = Token(selected ? "AccentBrush" : "FieldBrush");
        chip.BorderBrush = Token(selected ? "AccentBrush" : "CardBorderBrush");
        if (chip.Child is TextBlock text)
        {
            text.Foreground = Token(selected ? "AccentForegroundBrush" : "TextBrush");
        }
    }

    public static Border GradeChip(int grade, int earned, int total) => new()
    {
        CornerRadius = new CornerRadius(13),
        Padding = new Thickness(4, 3, 10, 3),
        Margin = new Thickness(0, 0, 8, 4),
        Background = Token("FieldBrush"),
        BorderBrush = Token("CardBorderBrush"),
        BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TrophyMedal { Width = 18, Height = 18, Grade = grade, Opacity = earned == 0 ? 0.7 : 1 },
                Count(earned, total, 12),
            },
        },
    };

    public static TextBlock Count(int earned, int total, double size)
    {
        var text = new TextBlock { FontSize = size, VerticalAlignment = VerticalAlignment.Center, Foreground = Token("TextBrush") };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(earned.ToString()) { FontWeight = FontWeight.Bold });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run($" / {total}") { Foreground = Token("MutedBrush") });
        return text;
    }

    public static Control StatusChip(string glyph, string text, IBrush brush) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 5,
        HorizontalAlignment = HorizontalAlignment.Right,
        Children =
        {
            Glyph(glyph, 14, brush),
            new TextBlock { Text = text, FontSize = 12, Foreground = brush, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    // Hover lifts the card a little and fades its wash in; the selection ring is the owner's job.
    public static void Lift(Border card, Border? wash = null)
    {
        card.PointerEntered += (_, _) =>
        {
            Translate(card, -2, TimeSpan.FromMilliseconds(170));
            if (wash is not null)
            {
                wash.Opacity = 1;
            }
        };
        card.PointerExited += (_, _) =>
        {
            Translate(card, 0, TimeSpan.FromMilliseconds(170));
            if (wash is not null)
            {
                wash.Opacity = 0;
            }
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

    // A control added a moment ago only gets its composition visual once the next frame has been built, so the
    // animation is started from the first frame callback that finds it.
    public static void WithVisual(Control control, Action<CompositionVisual> start, Action? giveUp = null, int attempts = 6)
    {
        if (ElementComposition.GetElementVisual(control) is { } visual)
        {
            start(visual);
        }
        else if (attempts > 0 && TopLevel.GetTopLevel(control) is { } topLevel)
        {
            topLevel.RequestAnimationFrame(_ => WithVisual(control, start, giveUp, attempts - 1));
        }
        else
        {
            giveUp?.Invoke();
        }
    }

    // Squashes the card open around the middle of the size of the one it replaces.
    public static void FlipIn(Control card, Size size)
    {
        card.Opacity = 0;
        WithVisual(card, visual =>
        {
            var duration = TimeSpan.FromMilliseconds(240);
            var easing = new CubicEaseOut();
            visual.CenterPoint = new Vector3D(size.Width / 2, size.Height / 2, 0);
            var scale = visual.Compositor.CreateVector3DKeyFrameAnimation();
            scale.Target = "Scale";
            scale.Duration = duration;
            scale.InsertKeyFrame(0f, new Vector3D(1, 0.04, 1));
            scale.InsertKeyFrame(1f, new Vector3D(1, 1, 1), easing);
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.Target = "Opacity";
            opacity.Duration = duration;
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, easing);
            visual.StartAnimation("Scale", scale);
            visual.StartAnimation("Opacity", opacity);
            card.Opacity = 1;
        }, () => card.Opacity = 1);
    }

    public static void StaggerIn(IReadOnlyList<Control> rows)
    {
        var duration = TimeSpan.FromMilliseconds(260);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var delay = TimeSpan.FromMilliseconds(25 * Math.Min(i, 8));
            row.Opacity = 0;
            WithVisual(row, visual =>
            {
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
                translation.InsertKeyFrame(0f, new Vector3D(0, 14, 0));
                translation.InsertKeyFrame(1f, new Vector3D(0, 0, 0), easing);
                visual.StartAnimation("Opacity", opacity);
                visual.StartAnimation("Translation", translation);
                row.Opacity = 1;
            }, () => row.Opacity = 1);
        }
    }

    // Count-ups and ring fills need per-frame code, so they run on the display's frame callback instead of a timer.
    public static void Animate(Control host, double from, double to, TimeSpan duration, Action<double> apply)
    {
        apply(from);
        if (duration <= TimeSpan.FromMilliseconds(1) || from == to)
        {
            apply(to);
            return;
        }

        if (TopLevel.GetTopLevel(host) is { } topLevel)
        {
            Run(topLevel, from, to, duration, apply);
            return;
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            host.AttachedToVisualTree -= OnAttached;
            Run(TopLevel.GetTopLevel(host), from, to, duration, apply);
        }

        host.AttachedToVisualTree += OnAttached;
    }

    private static void Run(TopLevel? topLevel, double from, double to, TimeSpan duration, Action<double> apply)
    {
        if (topLevel is null)
        {
            apply(to);
            return;
        }

        var started = TimeSpan.MinValue;
        void Step(TimeSpan now)
        {
            if (started == TimeSpan.MinValue)
            {
                started = now;
            }

            var t = Math.Min(1.0, (now - started).TotalMilliseconds / duration.TotalMilliseconds);
            apply(from + (to - from) * (1 - Math.Pow(1 - t, 3)));
            if (t < 1)
            {
                topLevel.RequestAnimationFrame(Step);
            }
        }

        topLevel.RequestAnimationFrame(Step);
    }
}
