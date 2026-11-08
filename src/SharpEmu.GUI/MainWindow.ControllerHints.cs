// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SharpEmu.GUI.Controls;

namespace SharpEmu.GUI;

/// <summary>
/// Controller connection state and the PlayStation-style button-hint bar. Hints are shown
/// only while a pad is connected and list only the buttons that do something right now.
/// </summary>
public partial class MainWindow
{
    private const double HintBarClearance = 56;
    private const double MainContentBottomMargin = 24;

    private bool _gamepadConnected;
    private string? _hintKey;

    private enum HintContext
    {
        Library,
        LibraryEmpty,
        Sheet,
        Menu,
        Content,
        Launch,
        Choices,
        Trophies,
    }

    private void SetGamepadConnected(bool connected)
    {
        if (connected == _gamepadConnected)
        {
            return;
        }

        _gamepadConnected = connected;
        PageHintL1.IsVisible = connected;
        PageHintR1.IsVisible = connected;
        ControllerHintBar.IsVisible = connected;
        _hintKey = null;
        SetPadNavigation(connected);

        // The bar gets its own strip at the bottom: every page, list and the console end
        // above it instead of scrolling underneath. The options surface still reaches the
        // window edge so the strip matches the page behind it.
        var bottom = MainContentBottomMargin + (connected ? HintBarClearance : 0);
        MainContent.Margin = new Thickness(40, 16, 40, bottom);
        OptionsPageSurface.Margin = new Thickness(-40, -16, -40, -bottom);
        OptionsBackdrop.Margin = OptionsPageSurface.Margin;
        TrophyBackdropHost.Margin = OptionsPageSurface.Margin;
        GameOptionsMenuSurface.Margin = new Thickness(-40, 0, -40, -bottom);
        GameOptionsBackdrop.Margin = GameOptionsMenuSurface.Margin;

        if (connected && _activePageIndex == 0 && !_isGameSettingsOpen)
        {
            // Show the D-pad focus ring on the current tile straight away.
            FocusLibrarySelection();
        }
    }

    private HintContext CurrentHintContext()
    {
        var focused = FocusManager?.GetFocusedElement() as Control;
        if (focused is ComboBoxItem or ComboBox { IsDropDownOpen: true })
        {
            return HintContext.Choices;
        }

        if (Sheets.Top is not null)
        {
            return HintContext.Sheet;
        }

        if (_isGameSettingsOpen || _activePageIndex == 1)
        {
            if (_isGameSettingsOpen && ReferenceEquals(focused, GameOptionsLaunchButton))
            {
                return HintContext.Launch;
            }

            return focused is Button button && button.Classes.Contains("optionsNav")
                ? HintContext.Menu
                : HintContext.Content;
        }

        return _activePageIndex == 2
            ? HintContext.Trophies
            : _visibleGames.Count == 0 ? HintContext.LibraryEmpty : HintContext.Library;
    }

    private void RefreshControllerHints()
    {
        if (!_gamepadConnected)
        {
            return;
        }

        var context = CurrentHintContext();
        var key = $"{context}|{_isGameSettingsOpen}|{Localization.Instance.CurrentCode}";
        if (key == _hintKey)
        {
            return;
        }

        _hintKey = key;
        var loc = Localization.Instance;
        string Hint(string name) => loc.Get($"Hint.{name}");

        var back = _isGameSettingsOpen ? Hint("Close") : Hint("Back");
        var items = ControllerHintItems.Children;
        items.Clear();
        switch (context)
        {
            case HintContext.Library:
                items.Add(HintItem(PadSymbol.Cross, Hint("Launch")));
                items.Add(HintItem(PadSymbol.Triangle, Hint("GameOptions")));
                items.Add(HintItem(PadSymbol.Square, Hint("LibraryMenu")));
                break;
            case HintContext.LibraryEmpty:
                items.Add(HintItem(PadSymbol.Cross, loc.Get("Library.AddFolder")));
                break;
            case HintContext.Menu:
                items.Add(HintItem(PadSymbol.Cross, Hint("Select")));
                items.Add(HintItem(PadSymbol.Circle, back));
                break;
            case HintContext.Content:
                items.Add(HintItem(PadSymbol.Cross, Hint("Change")));
                items.Add(HintItem(PadSymbol.Circle, Hint("Back")));
                break;
            case HintContext.Sheet:
                items.Add(HintItem(PadSymbol.Cross, Hint("Select")));
                items.Add(HintItem(PadSymbol.Circle, Hint("Close")));
                break;
            case HintContext.Launch:
                items.Add(HintItem(PadSymbol.Cross, Hint("Launch")));
                items.Add(HintItem(PadSymbol.Circle, Hint("Close")));
                break;
            case HintContext.Choices:
                items.Add(HintItem(PadSymbol.Cross, Hint("Choose")));
                items.Add(HintItem(PadSymbol.Circle, Hint("Cancel")));
                break;
            case HintContext.Trophies:
                items.Add(HintItem(PadSymbol.Cross, Hint("Open")));
                items.Add(HintItem(PadSymbol.Circle, Hint("Back")));
                break;
        }

        if (context is not (HintContext.Choices or HintContext.Sheet))
        {
            items.Add(ShoulderHintItem(Hint("SwitchPage")));
        }
    }

    private static StackPanel HintItem(PadSymbol symbol, string label)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        item.Children.Add(new PadGlyph { Symbol = symbol, VerticalAlignment = VerticalAlignment.Center });
        item.Children.Add(HintLabel(label));
        return item;
    }

    private static StackPanel ShoulderHintItem(string label)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var text in new[] { "L1", "R1" })
        {
            item.Children.Add(new Border
            {
                Classes = { "padHint" },
                Child = new TextBlock { Text = text },
            });
        }

        item.Children.Add(new Border { Width = 4 });
        item.Children.Add(HintLabel(label));
        return item;
    }

    private static TextBlock HintLabel(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeight.Medium,
        Foreground = Brushes.White,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
