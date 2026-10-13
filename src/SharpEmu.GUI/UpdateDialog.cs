// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpEmu.GUI;

/// <summary>Confirmation window for an available application update.</summary>
public sealed class UpdateDialog : Window
{
    public sealed record Result(bool Download, bool SuppressReminder);

    private readonly CheckBox _suppressReminder;

    public UpdateDialog(Updater.UpdateInfo update)
    {
        var loc = Localization.Instance;

        Title = loc.Get("Updater.Dialog.Title");
        Width = 680;
        Height = 620;
        MinWidth = 460;
        MinHeight = 360;
        MaxHeight = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#0D1017"));

        var heading = new TextBlock
        {
            Text = loc.Format("Updater.Dialog.Heading", update.TagName),
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var description = new TextBlock
        {
            Text = loc.Format("Updater.Dialog.Description", update.Sha),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#8B94A7")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        var header = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(20, 20, 20, 12),
            Children = { heading, description },
        };

        var notesLabel = new TextBlock
        {
            Text = loc.Get("Updater.Dialog.Notes"),
            Classes = { "sectionTitle" },
            Margin = new Thickness(20, 0, 20, 8),
        };
        if (update.HistoryTruncated)
        {
            notesLabel.Text += "  " + loc.Get("Updater.Dialog.HistoryLimited");
        }
        var notes = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 12, 0) };
        if (update.Changelog.Count == 0)
        {
            notes.Children.Add(new TextBlock
            {
                Text = loc.Get("Updater.Dialog.NoNotes"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#C7CFDE")),
            });
        }
        else
        {
            foreach (var release in update.Changelog)
            {
                notes.Children.Add(new TextBlock
                {
                    Text = release.TagName,
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse("#8F73FF")),
                });
                notes.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(release.Notes)
                        ? loc.Get("Updater.Dialog.NoNotes")
                        : release.Notes,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#C7CFDE")),
                });
            }
        }
        var noteScroller = new ScrollViewer
        {
            Content = notes,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(20, 0),
        };

        _suppressReminder = new CheckBox
        {
            Content = loc.Get("Updater.Dialog.Suppress"),
            FontSize = 12,
            Margin = new Thickness(20, 12, 20, 0),
        };
        var later = new Button { Content = loc.Get("Updater.Dialog.Later"), Classes = { "ghost" } };
        var download = new Button { Content = loc.Get("Updater.Dialog.Download"), Classes = { "accent" } };
        later.Click += (_, _) => Close(new Result(false, _suppressReminder.IsChecked == true));
        download.Click += (_, _) => Close(new Result(true, false));

        var footer = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#8B94A7")) { Opacity = 0.25 },
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 20, 16),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { later, download },
            },
        };

        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(notesLabel, 1);
        Grid.SetRow(noteScroller, 2);
        Grid.SetRow(_suppressReminder, 3);
        Grid.SetRow(footer, 4);
        content.Children.Add(header);
        content.Children.Add(notesLabel);
        content.Children.Add(noteScroller);
        content.Children.Add(_suppressReminder);
        content.Children.Add(footer);
        Content = content;
    }
}
