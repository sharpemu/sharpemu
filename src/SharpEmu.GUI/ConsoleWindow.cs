// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly ToggleButton _autoScrollCheck;
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
        Background = LauncherThemes.Brush("CardBrush");
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SharpEmu.GUI/Assets/SharpEmu.ico")));

        // Same toolbar and log surface as the console panel docked in the launcher.
        _searchBox = new TextBox
        {
            Classes = { "consoleSearch" },
            PlaceholderText = loc.Get("Console.SearchWatermark"),
            Margin = new Thickness(0, 0, 6, 0),
        };
        _autoScrollCheck = ConsoleControls.Tool(new ToggleButton { IsChecked = autoScroll }, "vertical_align_bottom", loc.Get("Console.AutoScroll"));
        var copyButton = ConsoleControls.Tool(new Button(), "content_copy", loc.Get("Console.Copy"));
        var clearButton = ConsoleControls.Tool(new Button(), "delete_sweep", loc.Get("Console.Clear"));
        copyButton.Click += async (_, _) => await CopyAsync();
        clearButton.Click += (_, _) => clear();
        _searchBox.TextChanged += (_, _) => RefreshVisibleLines();

        _list = new ListBox
        {
            Classes = { "console" },
            ItemsSource = _visibleLines,
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
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                new Grid
                {
                    Margin = new Thickness(18, 10, 10, 10),
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        new TextBlock
                        {
                            Classes = { "consoleTitle" },
                            Text = loc.Get("Console.Title"),
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 4,
                            Children = { _searchBox, _autoScrollCheck, copyButton, clearButton },
                        }.WithGridColumn(1),
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

file static class ConsoleControls
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

    public static T Tool<T>(T button, string symbol, string name)
        where T : Button
    {
        button.Classes.Add("consoleTool");
        button.Content = new TextBlock { Classes = { "materialSymbol", "compact" }, Text = symbol };
        ToolTip.SetTip(button, name);
        AutomationProperties.SetName(button, name);
        return button;
    }
}
