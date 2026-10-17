// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Collections.Specialized;

namespace SharpEmu.GUI;

public sealed class ConsoleWindow : Window
{
    private readonly AvaloniaList<LogLine> _sourceLines;
    private readonly AvaloniaList<LogLine> _visibleLines = new();
    private readonly ListBox _list;
    private readonly TextBox _searchBox;
    private readonly CheckBox _autoScrollCheck;
    private readonly GuiSettings _settings;
    private bool _placementRestored;

    public ConsoleWindow(
        AvaloniaList<LogLine> lines,
        Action clear,
        bool autoScroll,
        GuiSettings settings)
    {
        var loc = Localization.Instance;

        _sourceLines = lines;
        _settings = settings;
        Title = loc.Get("Console.WindowTitle");
        Width = settings.ConsoleWindowWidth;
        Height = settings.ConsoleWindowHeight;
        MinWidth = 520;
        MinHeight = 320;
        Background = new SolidColorBrush(Color.Parse("#0D1017"));
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SharpEmu.GUI/Assets/SharpEmu.ico")));

        _searchBox = new TextBox
        {
            PlaceholderText = loc.Get("Console.SearchWatermark"),
            Width = 320,
            Margin = new Thickness(0, 0, 12, 0),
        };
        _autoScrollCheck = new CheckBox
        {
            Content = loc.Get("Console.AutoScroll"),
            IsChecked = autoScroll,
            FontSize = 12,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copyButton = new Button
        {
            Classes = { "ghost" },
            Content = loc.Get("Console.Copy"),
            Padding = new Thickness(10, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var clearButton = new Button
        {
            Classes = { "ghost" },
            Content = loc.Get("Console.Clear"),
            Padding = new Thickness(10, 4),
        };
        copyButton.Click += async (_, _) => await CopyAsync();
        clearButton.Click += (_, _) => clear();
        _searchBox.TextChanged += (_, _) => RefreshVisibleLines();

        _list = new ListBox
        {
            Classes = { "console" },
            ItemsSource = _visibleLines,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.Parse("#232B3A")),
            ItemTemplate = new FuncDataTemplate<LogLine>((_, _) =>
            {
                var text = new TextBlock { TextWrapping = TextWrapping.NoWrap };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(LogLine.Text)));
                text.Bind(TextBlock.ForegroundProperty, new Binding(nameof(LogLine.Brush)));
                return text;
            }),
        };

        Content = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                new Grid
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Classes = { "sectionTitle" },
                            Text = loc.Get("Console.Title"),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        _searchBox.WithGridColumn(1),
                        _autoScrollCheck.WithGridColumn(2),
                        copyButton.WithGridColumn(3),
                        clearButton.WithGridColumn(4),
                    },
                },
                _list.WithGridRow(1),
            },
        };

        lines.CollectionChanged += OnLinesChanged;
        Closed += (_, _) => lines.CollectionChanged -= OnLinesChanged;
        Opened += (_, _) => RestorePlacement();
        PositionChanged += (_, _) => RememberNormalBounds();
        SizeChanged += (_, _) => RememberNormalBounds();
        PropertyChanged += (_, change) =>
        {
            if (_placementRestored && change.Property == WindowStateProperty && WindowState != WindowState.Minimized)
                _settings.ConsoleWindowMaximized = WindowState == WindowState.Maximized;
        };
        Closing += (_, _) =>
        {
            RememberNormalBounds();
            if (WindowState != WindowState.Minimized)
                _settings.ConsoleWindowMaximized = WindowState == WindowState.Maximized;
            _settings.Save();
        };
        RefreshVisibleLines();
    }

    private void RestorePlacement()
    {
        if (_settings.ConsoleWindowLeft is { } left && _settings.ConsoleWindowTop is { } top)
        {
            var savedPosition = new PixelPoint(left, top);
            var screen = Screens.ScreenFromPoint(savedPosition) ?? Screens.ScreenFromWindow(Owner ?? this) ?? Screens.Primary;
            if (screen is not null)
            {
                Width = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling));
                Height = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling));
                Position = ConstrainPosition(savedPosition, screen.WorkingArea, new Size(Width, Height), screen.Scaling);
            }
        }
        _placementRestored = true;
        RememberNormalBounds();
        if (_settings.ConsoleWindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void RememberNormalBounds()
    {
        if (!_placementRestored || !IsVisible || WindowState != WindowState.Normal)
            return;

        _settings.ConsoleWindowLeft = Position.X;
        _settings.ConsoleWindowTop = Position.Y;
        if (ClientSize.Width >= MinWidth && ClientSize.Height >= MinHeight)
        {
            _settings.ConsoleWindowWidth = ClientSize.Width;
            _settings.ConsoleWindowHeight = ClientSize.Height;
        }
    }

    internal static PixelPoint ConstrainPosition(PixelPoint position, PixelRect workingArea, Size size, double scaling)
    {
        var maximumLeft = workingArea.Right - (int)Math.Ceiling(size.Width * scaling);
        var maximumTop = workingArea.Bottom - (int)Math.Ceiling(size.Height * scaling);
        return new PixelPoint(
            Math.Clamp(position.X, workingArea.X, Math.Max(workingArea.X, maximumLeft)),
            Math.Clamp(position.Y, workingArea.Y, Math.Max(workingArea.Y, maximumTop)));
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshVisibleLines();
        if (_autoScrollCheck.IsChecked == true)
        {
            Dispatcher.UIThread.Post(() => (_list.Scroll as ScrollViewer)?.ScrollToEnd());
        }
    }

    private void RefreshVisibleLines()
    {
        var query = _searchBox.Text ?? string.Empty;
        _visibleLines.Clear();
        _visibleLines.AddRange(string.IsNullOrWhiteSpace(query)
            ? _sourceLines
            : _sourceLines.Where(line => line.Text.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task CopyAsync()
    {
        if (_visibleLines.Count == 0 || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(string.Join(Environment.NewLine, _visibleLines.Select(line => line.Text)));
    }
}

file static class GridExtensions
{
    public static T WithGridColumn<T>(this T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    public static T WithGridRow<T>(this T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }
}
