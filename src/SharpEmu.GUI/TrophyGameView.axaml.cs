// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SharpEmu.GUI.Controls;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

public partial class TrophyGameView : UserControl
{
    private enum Filter
    {
        All,
        Earned,
        Locked,
    }

    // 72 dip icons: twice that is sharp on a 2x display and mip-maps down cleanly on a 1x one.
    private const int IconPixels = 144;

    private string _gameDirectory = string.Empty;
    private string _titleId = string.Empty;
    private bool _compatibilityMode;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<int, (Bitmap? Sharp, Bitmap? Soft)> _icons = [];
    private readonly HashSet<int> _revealed = [];
    private readonly List<(Border Chip, Filter Filter)> _filterChips = [];
    private IReadOnlyList<TrophyEntry> _trophies = [];
    private HashSet<int> _earnedBefore = [];
    private string _lastSignature = string.Empty;
    private Filter _filter = Filter.All;
    private long _stamp;
    private double _shownPercent;
    private bool _opened;

    public event Action? BackRequested;

    internal TrophyBackdrop? Backdrop { get; set; }

    public TrophyGameView()
    {
        InitializeComponent();
        BackButton.Click += (_, _) => BackRequested?.Invoke();
        _refreshTimer.Tick += (_, _) =>
        {
            if (IsEffectivelyVisible)
            {
                Reload(force: false);
            }
        };
        _refreshTimer.Start();
    }

    public void Show(GameEntry game, string gameDirectory)
    {
        if (!string.Equals(_gameDirectory, gameDirectory, StringComparison.Ordinal))
        {
            _icons.Clear();
        }

        _gameDirectory = gameDirectory;
        _titleId = game.TitleId ?? string.Empty;
        _compatibilityMode = game.CompatibilityMode;
        GameNameText.Text = game.Name;
        CoverImage.Source = game.Cover;
        CoverFrame.IsVisible = game.Cover is not null;
        _revealed.Clear();
        _earnedBefore = [];
        _trophies = [];
        _lastSignature = string.Empty;
        _filter = Filter.All;
        _shownPercent = 0;
        _opened = false;
        BuildFilters();
        TrophyList.Children.Clear();
        Backdrop?.Show(game);
        Reload(force: true);
    }

    private void BuildFilters()
    {
        FilterBar.Children.Clear();
        _filterChips.Clear();
        foreach (var (filter, key) in new[] { (Filter.All, "Trophies.Filter.All"), (Filter.Earned, "Trophies.Filter.Earned"), (Filter.Locked, "Trophies.Filter.Locked") })
        {
            var chip = TrophyUi.Chip(Localization.Instance.Get(key));
            chip.PointerPressed += (_, _) =>
            {
                _filter = filter;
                UpdateFilterChips();
                RenderList(stagger: true);
            };
            _filterChips.Add((chip, filter));
            FilterBar.Children.Add(chip);
        }

        UpdateFilterChips();
    }

    public void Unload()
    {
        _gameDirectory = string.Empty;
        _titleId = string.Empty;
        _compatibilityMode = false;
        _stamp = 0;
        TrophyList.Children.Clear();
        GradeCounts.Children.Clear();
        CoverImage.Source = null;
        foreach (var (sharp, soft) in _icons.Values)
        {
            sharp?.Dispose();
            soft?.Dispose();
        }

        _icons.Clear();
        _trophies = [];
    }

    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Up or Key.Down:
                var offset = TrophyScroll.Offset.Y + (key == Key.Down ? 110 : -110);
                TrophyScroll.Offset = new Vector(TrophyScroll.Offset.X, Math.Clamp(offset, 0, Math.Max(0, TrophyScroll.Extent.Height - TrophyScroll.Viewport.Height)));
                return true;
            case Key.Left or Key.Right:
                _filter = (Filter)(((int)_filter + (key == Key.Right ? 1 : 2)) % 3);
                UpdateFilterChips();
                RenderList(stagger: true);
                return true;
            default:
                return false;
        }
    }

    private void UpdateFilterChips()
    {
        foreach (var (chip, filter) in _filterChips)
        {
            TrophyUi.SetChipSelected(chip, filter == _filter);
        }
    }

    private async void Reload(bool force)
    {
        var gameDirectory = _gameDirectory;
        var titleId = _titleId;
        if (gameDirectory.Length == 0)
        {
            return;
        }

        var language = TrophyUi.TrophyLanguage;
        var stamp = await Task.Run(() => HashCode.Combine(TrophyCatalog.Stamp(gameDirectory, titleId), language));
        if (gameDirectory != _gameDirectory || (!force && stamp == _stamp))
        {
            return;
        }

        _stamp = stamp;
        var trophies = await Task.Run(() => TrophyCatalog.Load(gameDirectory, titleId, language: language));
        if (gameDirectory != _gameDirectory)
        {
            return;
        }

        var signature = string.Join(',', trophies.Where(t => t.Unlocked).Select(t => t.Id)) + "/" + trophies.Count;
        if (!force && signature == _lastSignature)
        {
            return;
        }

        // Decode before building the rows so they never pop their icons in while the list animates.
        var missing = trophies.Where(t => !_icons.ContainsKey(t.Id)).ToArray();
        if (missing.Length > 0)
        {
            var decoded = await Task.Run(() => DecodeIcons(missing));
            if (gameDirectory != _gameDirectory)
            {
                return;
            }

            foreach (var (id, icon) in decoded)
            {
                _icons[id] = icon;
            }
        }

        _lastSignature = signature;
        var earnedNow = trophies.Where(t => t.Unlocked).Select(t => t.Id).ToHashSet();
        var fresh = force ? new HashSet<int>() : earnedNow.Except(_earnedBefore).ToHashSet();
        _earnedBefore = earnedNow;
        _trophies = trophies;
        if (CoverImage.Source is null)
        {
            CoverFrame.IsVisible = false;
        }

        RenderSummary();
        RenderList(stagger: !_opened, fresh);
        _opened = true;
    }

    private static (int Id, (Bitmap? Sharp, Bitmap? Soft) Icon)[] DecodeIcons(TrophyEntry[] trophies)
    {
        var decoded = new (int Id, (Bitmap? Sharp, Bitmap? Soft) Icon)[trophies.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6) };
        Parallel.For(0, trophies.Length, options, i =>
        {
            var trophy = trophies[i];
            decoded[i] = (trophy.Id, (TrophyUi.LoadIcon(trophy.IconPng, IconPixels), trophy.Hidden ? TrophyUi.LoadIcon(trophy.IconPng, 10) : null));
        });
        return decoded;
    }

    private void RenderSummary()
    {
        var earned = _trophies.Count(t => t.Unlocked);
        var percent = _trophies.Count == 0 ? 0 : earned * 100.0 / _trophies.Count;
        TrophyUi.Animate(ProgressRing, _shownPercent, percent, TimeSpan.FromMilliseconds(_opened ? 350 : 800), value =>
        {
            PercentText.Text = $"{Math.Round(value)}%";
            ProgressRing.Value = value;
        });
        _shownPercent = percent;
        ProgressText.Text = Localization.Instance.Format("Trophies.Progress", earned, _trophies.Count, (int)Math.Round(percent));
        GradeCounts.Children.Clear();
        for (var grade = 1; grade <= 4; grade++)
        {
            var total = _trophies.Count(t => t.Grade == grade);
            if (total > 0)
            {
                GradeCounts.Children.Add(TrophyUi.GradeChip(grade, _trophies.Count(t => t.Grade == grade && t.Unlocked), total));
            }
        }
    }

    private void RenderList(bool stagger, IReadOnlySet<int>? fresh = null)
    {
        TrophyList.Children.Clear();
        var shown = _trophies
            .Where(t => _filter switch { Filter.Earned => t.Unlocked, Filter.Locked => !t.Unlocked, _ => true })
            .OrderByDescending(t => t.Unlocked)
            .ThenByDescending(t => t.UnlockedAtUtc)
            .ThenBy(t => t.Id)
            .ToList();
        if (shown.Count == 0)
        {
            TrophyList.Children.Add(new TextBlock
            {
                Text = _compatibilityMode && _trophies.Count == 0
                    ? Localization.Instance.Format("Trophies.GnmMode.KeyHint",
                        Localization.Instance.Get("Page.Options"), Localization.Instance.Get("Options.GnmMode.TrophyKey.Label"))
                    : Localization.Instance.Get("Trophies.None"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = LauncherThemes.Brush("SecondaryTextBrush"),
                FontSize = 15,
                Margin = new Thickness(4, 12),
            });
            return;
        }

        foreach (var trophy in shown)
        {
            var row = BuildRow(trophy);
            TrophyList.Children.Add(row);
            if (fresh?.Contains(trophy.Id) == true)
            {
                Celebrate(row, trophy.Grade);
            }
        }

        if (stagger)
        {
            TrophyUi.StaggerIn(TrophyList.Children.ToList());
        }
    }

    private Border BuildRow(TrophyEntry trophy)
    {
        var canReveal = trophy.Hidden && !trophy.Unlocked;
        var hidden = canReveal && !_revealed.Contains(trophy.Id);
        var (light, mid, _) = TrophyMedal.Palette(trophy.Grade);

        var row = new Border
        {
            CornerRadius = TrophyUi.CardRadius,
            Padding = new Thickness(0, 14, 14, 14),
            BorderThickness = new Thickness(1),
            BoxShadow = TrophyUi.GlassShadow,
            ClipToBounds = true,
        };
        var stripe = new Border
        {
            Width = 5,
            Margin = new Thickness(0, -14, 0, -14),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(light, 0), new GradientStop(mid, 1) },
            },
            Opacity = trophy.Unlocked ? 1 : 0.28,
        };
        var content = BuildRowContent(trophy, hidden);
        content.Margin = new Thickness(14, 0, 0, 0);
        var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var wash = TrophyUi.HoverWash();
        wash.Margin = new Thickness(0, -14, -14, -14);
        Grid.SetColumnSpan(wash, 2);
        rowGrid.Children.Add(wash);
        rowGrid.Children.Add(stripe);
        Grid.SetColumn(content, 1);
        rowGrid.Children.Add(content);
        row.Child = rowGrid;
        if (trophy.Unlocked)
        {
            // The glass pane shows through where the grade tint fades out, so the row stays on theme.
            rowGrid.Children.Insert(0, new Border
            {
                IsHitTestVisible = false,
                Margin = new Thickness(0, -14, -14, -14),
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(78, mid.R, mid.G, mid.B), 0),
                        new GradientStop(Color.FromArgb(0, mid.R, mid.G, mid.B), 0.6),
                    },
                },
            });
            Grid.SetColumnSpan(rowGrid.Children[0], 2);
            row.Background = LauncherThemes.Brush("PaneGlassBrush");
            row.BorderBrush = new SolidColorBrush(Color.FromArgb(120, light.R, light.G, light.B));
        }
        else
        {
            row.Background = LauncherThemes.Brush("PaneGlassBrush");
            row.BorderBrush = LauncherThemes.Brush("GlassRimBrush");
        }

        TrophyUi.Lift(row, wash);
        if (canReveal)
        {
            row.Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(row, Localization.Instance.Get("Trophies.RevealHint"));
            row.PointerPressed += (_, _) =>
            {
                if (!_revealed.Remove(trophy.Id))
                {
                    _revealed.Add(trophy.Id);
                }

                var index = TrophyList.Children.IndexOf(row);
                if (index >= 0)
                {
                    var replacement = BuildRow(trophy);
                    TrophyList.Children[index] = replacement;
                    TrophyUi.FlipIn(replacement, row.Bounds.Size);
                }
            };
        }

        return row;
    }

    // The game's own names are often all caps; a hidden row follows that style so the list reads evenly.
    private string HiddenTitle()
    {
        var title = Localization.Instance.Get("Trophies.Hidden");
        var names = _trophies.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        return names.Count > 0 && names.All(n => n == n.ToUpperInvariant()) ? title.ToUpperInvariant() : title;
    }

    private Grid BuildRowContent(TrophyEntry trophy, bool hidden)
    {
        _icons.TryGetValue(trophy.Id, out var icon);
        var image = new Image { Source = hidden ? icon.Soft : icon.Sharp, Stretch = Stretch.UniformToFill };

        // The sharp icons are decoded at the displayed size; only the tiny blurred hidden ones need smooth upscaling.
        if (hidden)
        {
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        }

        if (hidden)
        {
            image.Opacity = 0.35;
        }
        else if (!trophy.Unlocked)
        {
            image.Opacity = 0.42;
        }

        var iconFrame = new Grid { Width = 72, Height = 72 };
        if (trophy.Unlocked)
        {
            var (_, glow, _) = TrophyMedal.Palette(trophy.Grade);
            iconFrame.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(140, glow.R, glow.G, glow.B) }),
            });
        }

        iconFrame.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true,
            Background = LauncherThemes.Brush("ElevatedBrush"),
            Child = image,
        });
        if (hidden || !trophy.Unlocked)
        {
            iconFrame.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -4, -4),
                Background = LauncherThemes.Brush("OverlayBrush"),
                BorderBrush = LauncherThemes.Brush("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                Child = TrophyUi.Glyph(hidden ? "question_mark" : "lock", 15, LauncherThemes.Brush("SecondaryTextBrush")),
            });
        }

        var name = new TextBlock
        {
            Text = hidden ? HiddenTitle() : trophy.Name,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Foreground = LauncherThemes.Brush("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var description = new TextBlock
        {
            Text = hidden ? Localization.Instance.Get("Trophies.HiddenDescription") : trophy.Description,
            Foreground = LauncherThemes.Brush("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
        };
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { name, description } };

        var status = trophy.UnlockedAtUtc is { } at
            ? TrophyUi.StatusChip("check_circle", at.ToLocalTime().ToString("g"), LauncherThemes.Brush("SuccessBrush"))
            : TrophyUi.StatusChip("hourglass_empty", Localization.Instance.Get("Trophies.Locked"), LauncherThemes.Brush("SecondaryTextBrush"));
        var medal = new TrophyMedal { Width = 44, Height = 44, Grade = trophy.Grade, Opacity = trophy.Unlocked ? 1 : 0.55 };
        var grade = new TextBlock
        {
            Text = TrophyUi.GradeName(trophy.Grade),
            Foreground = LauncherThemes.Brush("SecondaryTextBrush"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var side = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { grade, status } },
                medal,
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 20 };
        grid.Children.Add(iconFrame);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);
        return grid;
    }

    private static void Celebrate(Border row, int grade)
    {
        if (row.Child is not Grid content)
        {
            return;
        }

        // A highlight layer fades on the render thread; the row's own brushes are never touched.
        var (light, mid, _) = TrophyMedal.Palette(grade);
        var highlight = new Border
        {
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(18),
            Margin = new Thickness(-1, -15, -15, -15),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(mid, 0.37),
            BorderBrush = new SolidColorBrush(light),
        };
        Grid.SetColumnSpan(highlight, 2);
        content.Children.Add(highlight);
        TrophyUi.WithVisual(highlight, visual =>
        {
            const int steps = 30;
            var pulse = visual.Compositor.CreateScalarKeyFrameAnimation();
            pulse.Target = "Opacity";
            pulse.Duration = TimeSpan.FromMilliseconds(1500);
            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;
                var beat = 0.5 + 0.5 * Math.Cos(t * Math.PI * 4);
                pulse.InsertKeyFrame((float)t, (float)((1 - t) * (0.45 + 0.55 * beat)));
            }

            visual.StartAnimation("Opacity", pulse);
        }, () => content.Children.Remove(highlight));

        DispatcherTimer.RunOnce(() => content.Children.Remove(highlight), TimeSpan.FromMilliseconds(1600));
        row.BringIntoView();
    }
}
