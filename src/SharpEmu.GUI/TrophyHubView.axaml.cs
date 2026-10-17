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
    private static readonly TimeSpan PageSettleTime = TimeSpan.FromMilliseconds(320);
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.Parse("#A8ADB7"));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
    private static readonly IBrush CompleteCardBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(56, Gold.R, Gold.G, Gold.B), 0),
            new GradientStop(Color.FromArgb(14, 255, 255, 255), 0.6),
        },
    };

    private IReadOnlyList<GameEntry> _games = [];
    private readonly List<(Border Card, GameSummary Summary)> _cards = [];
    private readonly List<(Border Chip, Sort Sort)> _sortChips = [];
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IReadOnlyList<GameSummary> _summaries = [];
    private Sort _sort = Sort.Recent;
    private int _selected = -1;
    private double _shownAt;
    private bool _wasShown;
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

        _idle.Tick += (_, _) =>
        {
            if (!IsEffectivelyVisible)
            {
                _idle.Stop();
                return;
            }

            if (!_wasShown)
            {
                _wasShown = true;
                _shownAt = _clock.Elapsed.TotalSeconds;
            }

            var t = _clock.Elapsed.TotalSeconds - _shownAt;
            HeroMedal.Spin = t < 1.4 ? (1 - (1 - Math.Pow(1 - t / 1.4, 4))) * Math.PI * 4 : 0;
            var cycle = (t - 1.2) % 4.5;
            HeroMedal.Glint = t > 1.2 && cycle < 0.9 ? cycle / 0.9 : -1;
        };
    }

    public async Task ShowAsync(IReadOnlyList<GameEntry> games)
    {
        _games = games;
        SearchBox.PlaceholderText = Localization.Instance.Get("Trophies.SearchWatermark");
        BuildSortChips();
        _idle.Start();
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
        _idle.Stop();
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
        await Task.Delay(PageSettleTime);
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
            var card = BuildCard(summary);
            _cards.Add((card, summary));
            GameList.Children.Add(card);
        }

        if (_cards.Count == 0)
        {
            GameList.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Get(_summaries.Count == 0 ? "Trophies.None" : "Trophies.NoMatch"),
                Foreground = SecondaryText,
                FontSize = 13,
            });
            return;
        }

        if (stagger)
        {
            TrophyUi.StaggerIn(_cards.Select(c => (Control)c.Card).ToList());
        }

        Select(Math.Clamp(_selected, 0, _cards.Count - 1));
    }

    private void RenderTotals(IReadOnlyList<GameSummary> summaries)
    {
        var earned = summaries.Sum(s => s.Earned);
        var total = summaries.Sum(s => s.Trophies.Count);
        var percent = total == 0 ? 0 : earned * 100.0 / total;
        TotalOfText.Text = Localization.Instance.Format("Trophies.HubSummary", earned, total, summaries.Count);
        var duration = TimeSpan.FromMilliseconds(_animated ? 400 : 1300);
        TrophyUi.Animate(_animated ? earned : 0, earned, duration, value => TotalEarnedText.Text = ((int)Math.Round(value)).ToString());
        TrophyUi.Animate(_animated ? percent : 0, percent, duration, value => TotalRing.Value = value);
        TotalGrades.Children.Clear();
        for (var grade = 1; grade <= 4; grade++)
        {
            TotalGrades.Children.Add(TrophyUi.GradeChip(grade, summaries.Sum(s => s.EarnedOf(grade)), summaries.Sum(s => s.TotalOf(grade))));
        }
    }

    private Border BuildCard(GameSummary summary)
    {
        var cover = new Border
        {
            Width = 72,
            Height = 72,
            CornerRadius = new CornerRadius(16),
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
            CornerRadius = new CornerRadius(16),
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
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
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
                    FontSize = 13.5,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        if (latest is not null)
        {
            details.Children.Add(TrophyUi.Glyph("workspace_premium", 14, TrophyMedal.Palette(latest.Grade).Mid));
            details.Children.Add(new TextBlock
            {
                Text = Localization.Instance.Format("Trophies.Latest", latest.Name, latest.UnlockedAtUtc!.Value.ToLocalTime().ToString("d")),
                Foreground = SecondaryText,
                FontSize = 13.5,
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

        var ring = new Grid { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
        var progressRing = new TrophyProgressRing { Thickness = 4 };
        TrophyUi.Animate(_animated ? summary.Percent : 0, summary.Percent, TimeSpan.FromMilliseconds(_animated ? 1 : 1100), value => progressRing.Value = value);
        ring.Children.Add(progressRing);
        ring.Children.Add(new TextBlock
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
        Grid.SetColumn(ring, 2);
        grid.Children.Add(ring);

        var spotlightBrush = new RadialGradientBrush
        {
            RadiusX = new RelativeScalar(0.3, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(30, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        var spotlight = new Border
        {
            IsHitTestVisible = false,
            Opacity = 0,
            Background = spotlightBrush,
            Transitions = [new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) }],
        };
        var layers = new Grid();
        layers.Children.Add(spotlight);
        layers.Children.Add(grid);

        var card = new Border
        {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Background = summary.Complete ? CompleteCardBrush : CardBrush,
            BorderBrush = RestingBorder(summary, selected: false),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = layers,
        };
        TrophyUi.Lift(card);
        card.PointerEntered += (_, _) =>
        {
            _selected = _cards.FindIndex(c => c.Card == card);
            spotlight.Opacity = 1;
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
        return card;
    }

    private static IBrush RestingBorder(GameSummary summary, bool selected) =>
        new SolidColorBrush(selected
            ? Color.FromArgb(150, 255, 255, 255)
            : summary.Complete ? Color.FromArgb(120, Gold.R, Gold.G, Gold.B) : Color.FromArgb(22, 255, 255, 255));

    private void SetBackdrop(GameEntry game) => Backdrop?.Show(game);

    private void Select(int index)
    {
        if (index < 0 || index >= _cards.Count)
        {
            return;
        }

        _selected = index;
        for (var i = 0; i < _cards.Count; i++)
        {
            _cards[i].Card.BorderBrush = RestingBorder(_cards[i].Summary, i == index);
        }

        _cards[index].Card.BringIntoView();
        SetBackdrop(_cards[index].Summary.Game);
    }

    private void Open(GameSummary summary) => GameChosen?.Invoke(summary.Game, summary.Directory);
}
