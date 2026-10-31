// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SharpEmu.GUI.Controls;
using SharpEmu.Libs.Np;

namespace SharpEmu.GUI;

public partial class TrophyHubView : UserControl
{
    private enum Sort
    {
        Recent,
        Progress,
        Name,
    }

    private sealed record GameSummary(GameEntry Game, string Directory, IReadOnlyList<TrophyEntry> Trophies)
    {
        public int Earned => Trophies.Count(t => t.Unlocked);

        public double Percent => Trophies.Count == 0 ? 0 : Earned * 100.0 / Trophies.Count;

        public bool Complete => Trophies.Count > 0 && Earned == Trophies.Count;

        public TrophyEntry? Latest => Trophies.Where(t => t.UnlockedAtUtc is not null).MaxBy(t => t.UnlockedAtUtc);

        public int EarnedOf(int grade) => Trophies.Count(t => t.Grade == grade && t.Unlocked);

        public int TotalOf(int grade) => Trophies.Count(t => t.Grade == grade);
    }

    private static readonly Color Gold = Color.Parse("#F2B93B");
    private const double HeroSpinSeconds = 1.1;
    private static readonly TimeSpan PageSettleTime = TimeSpan.FromMilliseconds(260);
    private static readonly IBrush SecondaryText = LauncherThemes.Brush("SecondaryTextBrush");
    private static readonly IBrush CardBrush = LauncherThemes.Brush("PaneGlassBrush");
    private static readonly IBrush CardRim = LauncherThemes.Brush("GlassRimBrush");
    private static readonly IBrush CompleteRim = new SolidColorBrush(Color.FromArgb(150, Gold.R, Gold.G, Gold.B));

    // A completed game gets a gold wash fading out from its left edge over the glass.
    private static readonly IBrush CompleteWash = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(64, Gold.R, Gold.G, Gold.B), 0),
            new GradientStop(Color.FromArgb(0, Gold.R, Gold.G, Gold.B), 0.6),
        },
    };

    private IReadOnlyList<GameEntry> _games = [];
    private readonly List<(Border Card, GameSummary Summary, Border Ring)> _cards = [];
    private readonly List<(Border Chip, Sort Sort)> _sortChips = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IReadOnlyList<GameSummary> _summaries = [];
    private Sort _sort = Sort.Recent;
    private int _selected = -1;
    private double _shownAt;
    private bool _wasShown;
    private bool _active;
    private bool _heroPending;
    private bool _animated;
    private string _signature = string.Empty;
    private long _stamp;

    public event Action<GameEntry, string>? GameChosen;

    internal TrophyBackdrop? Backdrop { get; set; }

    public TrophyHubView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) =>
        {
            SearchClear.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
            RenderCards(stagger: false);
        };
        SearchClear.Click += (_, _) =>
        {
            SearchBox.Text = string.Empty;
            SearchBox.Focus();
        };
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
                e.Handled = true;
            }
        };
    }

    private void RequestHeroFrame()
    {
        if (_heroPending || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        _heroPending = true;
        topLevel.RequestAnimationFrame(HeroFrame);
    }

    // The medal only needs frames while it spins or glints; between glints a timer wakes it up.
    private void HeroFrame(TimeSpan now)
    {
        _heroPending = false;
        if (!_active || !IsEffectivelyVisible)
        {
            return;
        }

        if (!_wasShown)
        {
            _wasShown = true;
            _shownAt = _clock.Elapsed.TotalSeconds;
        }

        var t = _clock.Elapsed.TotalSeconds - _shownAt;
        HeroMedal.Spin = t < HeroSpinSeconds ? Math.Pow(1 - t / HeroSpinSeconds, 4) * Math.PI * 4 : 0;
        var cycle = (t - 1.0) % 4.5;
        HeroMedal.Glint = t > 1.0 && cycle < 0.9 ? cycle / 0.9 : -1;
        if (t < HeroSpinSeconds || HeroMedal.Glint >= 0)
        {
            RequestHeroFrame();
            return;
        }

        var wait = 1.0 + 4.5 * Math.Ceiling((t - 1.0) / 4.5) - t;
        DispatcherTimer.RunOnce(RequestHeroFrame, TimeSpan.FromSeconds(Math.Max(0.05, wait)));
    }

    public async Task ShowAsync(IReadOnlyList<GameEntry> games)
    {
        _games = games;
        SearchBox.PlaceholderText = Localization.Instance.Get("Trophies.SearchWatermark");
        BuildSortChips();
        _active = true;
        RequestHeroFrame();
        await LoadAsync();
    }

    public void Unload()
    {
        GameList.Children.Clear();
        _cards.Clear();
        _summaries = [];
        _signature = string.Empty;
        _stamp = 0;
        _selected = -1;
        _animated = false;
        _active = false;
    }

    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Down:
                Select(Math.Min(_cards.Count - 1, _selected + 1));
                return true;
            case Key.Up:
                Select(Math.Max(0, _selected - 1));
                return true;
            case Key.Enter when _selected >= 0 && _selected < _cards.Count:
                Open(_cards[_selected].Summary);
                return true;
            case Key.Left or Key.Right:
                _sort = (Sort)(((int)_sort + (key == Key.Right ? 1 : 2)) % 3);
                UpdateSortChips();
                RenderCards(stagger: true);
                return true;
            default:
                return false;
        }
    }

    private void BuildSortChips()
    {
        SortBar.Children.Clear();
        _sortChips.Clear();
        foreach (var (sort, key) in new[] { (Sort.Recent, "Trophies.Sort.Recent"), (Sort.Progress, "Trophies.Sort.Progress"), (Sort.Name, "Trophies.Sort.Name") })
        {
            var chip = TrophyUi.Chip(Localization.Instance.Get(key));
            chip.PointerPressed += (_, _) =>
            {
                _sort = sort;
                UpdateSortChips();
                RenderCards(stagger: true);
            };
            _sortChips.Add((chip, sort));
            SortBar.Children.Add(chip);
        }

        UpdateSortChips();
    }

    private void UpdateSortChips()
    {
        foreach (var (chip, sort) in _sortChips)
        {
            TrophyUi.SetChipSelected(chip, sort == _sort);
        }
    }

    private async Task LoadAsync()
    {
        var games = _games;
        var language = TrophyUi.TrophyLanguage;
        var stamp = await Task.Run(() => HashCode.Combine(Stamp(games), language));
        if (_animated && stamp == _stamp)
        {
            return;
        }

        LoadingState.IsVisible = _summaries.Count == 0;

        // Load while the page slides in; the cards are only built once the slide has settled.
        var settle = Task.Delay(PageSettleTime);
        var summaries = await Task.Run(() =>
        {
            var loaded = games
                .Select(game => (Game: game, Directory: TrophyUi.GameDirectory(game.Path)))
                .Where(entry => entry.Directory is not null)
                .Select(entry => new GameSummary(
                    entry.Game,
                    entry.Directory!,
                    TrophyCache.Load(entry.Directory!, entry.Game.TitleId!, language)))
                .Where(summary => summary.Trophies.Count > 0)
                .ToList();
            TrophyCache.Save();
            return loaded;
        });
        await settle;

        _stamp = stamp;
        LoadingState.IsVisible = false;
        var signature = Signature(summaries);
        if (_animated && signature == _signature)
        {
            return;
        }

        _signature = signature;
        _summaries = summaries;
        RenderTotals(_summaries);
        RenderCards(stagger: !_animated);
        _animated = true;
    }

    private static long Stamp(IReadOnlyList<GameEntry> games)
    {
        var hash = new HashCode();
        foreach (var game in games)
        {
            hash.Add(game.Path);
            if (TrophyUi.GameDirectory(game.Path) is { } directory)
            {
                hash.Add(TrophyCatalog.Stamp(directory, game.TitleId!));
            }
        }

        return hash.ToHashCode();
    }

    private static string Signature(IReadOnlyList<GameSummary> summaries) =>
        string.Join('|', summaries.Select(s => $"{s.Game.Path}:{s.Earned}/{s.Trophies.Count}:{s.Latest?.UnlockedAtUtc?.Ticks}"));

    private IEnumerable<GameSummary> Sorted()
    {
        var query = SearchBox.Text?.Trim();
        var matching = string.IsNullOrEmpty(query)
            ? _summaries
            : _summaries.Where(s => s.Game.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        return _sort switch
        {
            Sort.Progress => matching
                .OrderByDescending(s => s.Percent)
                .ThenBy(s => s.Game.Name, StringComparer.CurrentCultureIgnoreCase),
            Sort.Name => matching.OrderBy(s => s.Game.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => matching
                .OrderByDescending(s => s.Latest?.UnlockedAtUtc ?? DateTime.MinValue)
                .ThenBy(s => s.Game.Name, StringComparer.CurrentCultureIgnoreCase),
        };
    }

    private void RenderCards(bool stagger)
    {
        GameList.Children.Clear();
        _cards.Clear();
        foreach (var summary in Sorted())
        {
            var (card, ring) = BuildCard(summary);
            _cards.Add((card, summary, ring));
            GameList.Children.Add(card);
        }

        if (_cards.Count == 0)
        {
            GameList.Children.Add(BuildEmptyState(noGames: _summaries.Count == 0));
            Backdrop?.Show(null);
            return;
        }

        // The page always has art behind it, not only once a card is hovered.
        SetBackdrop(_cards[Math.Clamp(_selected, 0, _cards.Count - 1)].Summary.Game);

        if (stagger)
        {
            TrophyUi.StaggerIn(_cards.Select(c => (Control)c.Card).ToList());
        }

        Select(Math.Clamp(_selected, 0, _cards.Count - 1));
    }

    /// <summary>Centred empty state: no trophy games yet, or nothing matching the search.</summary>
    private static StackPanel BuildEmptyState(bool noGames)
    {
        var panel = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 72, 0, 0),
        };
        panel.Children.Add(new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(44),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
            Background = LauncherThemes.Brush("FieldBrush"),
            Child = new TextBlock
            {
                Classes = { "materialSymbol" },
                Text = "emoji_events",
                FontSize = 40,
                LineHeight = 48,
                Foreground = SecondaryText,
            },
        });
        panel.Children.Add(new TextBlock
        {
            Text = Localization.Instance.Get(noGames ? "Trophies.HubEmpty.Title" : "Trophies.NoMatch"),
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (noGames)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Get("Trophies.HubEmpty.Hint"),
                FontSize = 16,
                Foreground = SecondaryText,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        return panel;
    }

    private void RenderTotals(IReadOnlyList<GameSummary> summaries)
    {
        var earned = summaries.Sum(s => s.Earned);
        var total = summaries.Sum(s => s.Trophies.Count);
        var percent = total == 0 ? 0 : earned * 100.0 / total;
        TotalOfText.Text = Localization.Instance.Format("Trophies.HubSummary", earned, total, summaries.Count);
        var duration = TimeSpan.FromMilliseconds(_animated ? 300 : 800);
        TrophyUi.Animate(TotalEarnedText, _animated ? earned : 0, earned, duration, value => TotalEarnedText.Text = ((int)Math.Round(value)).ToString());
        TrophyUi.Animate(TotalRing, _animated ? percent : 0, percent, duration, value => TotalRing.Value = value);
        TotalGrades.Children.Clear();
        for (var grade = 1; grade <= 4; grade++)
        {
            TotalGrades.Children.Add(TrophyUi.GradeChip(grade, summaries.Sum(s => s.EarnedOf(grade)), summaries.Sum(s => s.TotalOf(grade))));
        }
    }

    private (Border Card, Border Ring) BuildCard(GameSummary summary)
    {
        var cover = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Background = summary.Game.PlaceholderBrush,
        };
        var initials = new TextBlock
        {
            Text = summary.Game.Initials,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var coverImage = new Image { Stretch = Stretch.UniformToFill };
        cover.Child = new Grid { Children = { initials, coverImage } };
        void ShowCover()
        {
            coverImage.Source = summary.Game.Cover;
            initials.IsVisible = summary.Game.Cover is null;
        }

        void OnGameChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GameEntry.Cover))
            {
                ShowCover();
            }
        }

        ShowCover();
        cover.AttachedToVisualTree += (_, _) =>
        {
            summary.Game.PropertyChanged -= OnGameChanged;
            summary.Game.PropertyChanged += OnGameChanged;
            ShowCover();
        };
        cover.DetachedFromVisualTree += (_, _) => summary.Game.PropertyChanged -= OnGameChanged;
        var coverFrame = new Grid { Width = 72, Height = 72, VerticalAlignment = VerticalAlignment.Center };
        coverFrame.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 3, Blur = 8, Color = Color.FromArgb(120, 0, 0, 0) }),
            Child = cover,
        });
        if (summary.Complete)
        {
            coverFrame.Children.Add(new TrophyMedal
            {
                Width = 28,
                Height = 28,
                Grade = 1,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -6, -6),
            });
        }

        var name = new TextBlock
        {
            Text = summary.Game.Name,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = LauncherThemes.Brush("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var latest = summary.Latest;
        var details = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                new TextBlock
                {
                    Text = Localization.Instance.Format("Trophies.Progress", summary.Earned, summary.Trophies.Count, (int)Math.Round(summary.Percent)),
                    Foreground = SecondaryText,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        if (latest is not null)
        {
            details.Children.Add(TrophyUi.Glyph("workspace_premium", 14, new SolidColorBrush(TrophyMedal.Palette(latest.Grade).Mid)));
            details.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Format("Trophies.Latest", latest.Name, latest.UnlockedAtUtc!.Value.ToLocalTime().ToString("d")),
                Foreground = SecondaryText,
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var medals = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        for (var grade = 1; grade <= 4; grade++)
        {
            var total = summary.TotalOf(grade);
            if (total == 0)
            {
                continue;
            }

            var earned = summary.EarnedOf(grade);
            medals.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TrophyMedal { Width = 18, Height = 18, Grade = grade, Opacity = earned == 0 ? 0.5 : 1 },
                    TrophyUi.Count(earned, total, 12.5),
                },
            });
        }

        var middle = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { name, details, medals },
        };

        var percentRing = new Grid { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        var progressRing = new TrophyProgressRing { Thickness = 4 };
        TrophyUi.Animate(progressRing, _animated ? summary.Percent : 0, summary.Percent, TimeSpan.FromMilliseconds(_animated ? 1 : 700), value => progressRing.Value = value);
        percentRing.Children.Add(progressRing);
        percentRing.Children.Add(new TextBlock
        {
            Text = $"{Math.Round(summary.Percent)}%",
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 20, Margin = new Thickness(14) };
        grid.Children.Add(coverFrame);
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);
        Grid.SetColumn(percentRing, 2);
        grid.Children.Add(percentRing);

        // The spotlight is the theme's text colour seen through a soft radial mask that follows the pointer.
        var spotlightBrush = new RadialGradientBrush
        {
            RadiusX = new RelativeScalar(0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.White, 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        var spotlight = new Border
        {
            IsHitTestVisible = false,
            Opacity = 0,
            Background = LauncherThemes.Brush("TextBrush"),
            OpacityMask = spotlightBrush,
            Transitions = [new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }],
        };
        var wash = TrophyUi.HoverWash();
        var ring = TrophyUi.SelectionRing();
        var layers = new Grid();
        if (summary.Complete)
        {
            layers.Children.Add(new Border { IsHitTestVisible = false, Background = CompleteWash });
        }

        layers.Children.Add(wash);
        layers.Children.Add(spotlight);
        layers.Children.Add(grid);
        layers.Children.Add(ring);

        var card = new Border
        {
            CornerRadius = TrophyUi.CardRadius,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Background = CardBrush,
            BorderBrush = summary.Complete ? CompleteRim : CardRim,
            BoxShadow = TrophyUi.GlassShadow,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = layers,
        };
        TrophyUi.Lift(card, wash);
        card.PointerEntered += (_, _) =>
        {
            var index = _cards.FindIndex(c => c.Card == card);
            if (index >= 0)
            {
                ShowSelection(index);
            }

            spotlight.Opacity = 0.14;
            SetBackdrop(summary.Game);
        };
        card.PointerExited += (_, _) => spotlight.Opacity = 0;
        card.PointerMoved += (_, e) =>
        {
            var position = e.GetPosition(card);
            var width = Math.Max(1, card.Bounds.Width);
            var height = Math.Max(1, card.Bounds.Height);
            var center = new RelativePoint(position.X / width, position.Y / height, RelativeUnit.Relative);
            spotlightBrush.Center = center;
            spotlightBrush.GradientOrigin = center;
            spotlightBrush.RadiusY = new RelativeScalar(0.3 * width / height, RelativeUnit.Relative);
        };
        card.PointerPressed += (_, _) => Open(summary);
        return (card, ring);
    }

    // Keyboard, pad and pointer share one selected state: the accent ring on exactly one card.
    private void ShowSelection(int index)
    {
        _selected = index;
        for (var i = 0; i < _cards.Count; i++)
        {
            _cards[i].Ring.Opacity = i == index ? 1 : 0;
        }
    }

    private void SetBackdrop(GameEntry game) => Backdrop?.Show(game);

    private void Select(int index)
    {
        if (index < 0 || index >= _cards.Count)
        {
            return;
        }

        ShowSelection(index);
        _cards[index].Card.BringIntoView();
        SetBackdrop(_cards[index].Summary.Game);
    }

    private void Open(GameSummary summary) => GameChosen?.Invoke(summary.Game, summary.Directory);
}
