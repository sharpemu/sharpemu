// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
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

    public ConsoleWindow(
        AvaloniaList<LogLine> lines,
        Action clear,
        bool autoScroll)
    {
        _sourceLines = lines;
        Title = "SharpEmu Console";
        Width = 980;
        Height = 620;
        MinWidth = 520;
        MinHeight = 320;
        Background = new SolidColorBrush(Color.Parse("#0D1017"));
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://SharpEmu.GUI/Assets/SharpEmu.ico")));

        _searchBox = new TextBox { Watermark = "Search...", Width = 320, Margin = new Thickness(0, 0, 12, 0) };
        _autoScrollCheck = new CheckBox
        {
            Content = "Auto-scroll",
            IsChecked = autoScroll,
            FontSize = 12,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copyButton = new Button
        {
            Classes = { "ghost" },
            Content = "Copy",
            Padding = new Thickness(10, 4),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var clearButton = new Button { Classes = { "ghost" }, Content = "Clear", Padding = new Thickness(10, 4) };
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
                            Text = "CONSOLE",
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
        RefreshVisibleLines();
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
