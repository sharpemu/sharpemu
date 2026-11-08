// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Input;
using Avalonia.Media.Transformation;

namespace SharpEmu.GUI;

/// <summary>
/// The page tabs and the library toolbar: a search field that opens out from its icon, and
/// the library menu that holds everything else (view, tile size, title music, adding games).
/// </summary>
public partial class MainWindow
{
    private readonly LibraryMenu _libraryMenu = new();

    private void InitializeLibraryToolbar()
    {
        SearchBox.GotFocus += (_, _) => UpdateSearchExpanded();
        SearchBox.LostFocus += (_, _) => UpdateSearchExpanded();
        SearchBox.TextChanged += (_, _) =>
        {
            SearchClearButton.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);
            UpdateSearchExpanded();
        };
        SearchBox.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape)
            {
                return;
            }

            // Escape clears the search first, then hands focus back to the library.
            args.Handled = true;
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
            }
            else
            {
                FocusLibrarySelection();
            }
        };
        SearchClearButton.Click += (_, _) =>
        {
            SearchBox.Text = string.Empty;
            SearchBox.Focus();
        };

        LibraryMenuButton.Click += (_, _) => OpenLibraryMenu();
        _libraryMenu.CarouselViewButton.Click += (_, _) => ChooseLibraryLayout(grid: false);
        _libraryMenu.GridViewButton.Click += (_, _) => ChooseLibraryLayout(grid: true);
        _libraryMenu.TileSizeSlider.ValueChanged += (_, _) =>
        {
            var scale = _libraryMenu.TileSizeSlider.Value / 100.0;
            if (Math.Abs(_settings.LibraryTileScale - scale) < 0.001)
            {
                return;
            }

            _settings.LibraryTileScale = scale;
            UpdateLibraryLayoutMetrics();
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        };

        // The Options page owns these two settings; the menu drives its controls.
        _libraryMenu.TitleMusicSwitch.IsCheckedChanged += (_, _) =>
            TitleMusicToggle.IsChecked = _libraryMenu.TitleMusicSwitch.IsChecked == true;
        _libraryMenu.VolumeSlider.ValueChanged += (_, _) =>
            TitleMusicVolumeSlider.Value = _libraryMenu.VolumeSlider.Value;

        _libraryMenu.AddFolderButton.Click += (_, _) =>
        {
            _libraryMenu.Close();
            StartAddFolderFromTile();
        };
        _libraryMenu.OpenFileButton.Click += async (_, _) =>
        {
            _libraryMenu.Close();
            await OpenFileAsync();
        };

        PageTabStrip.SizeChanged += (_, _) => MovePageTabThumb(animate: false);
        UpdateSearchExpanded();
    }

    private void OpenLibraryMenu()
    {
        if (_libraryMenu.IsOpen)
        {
            _libraryMenu.Close();
            return;
        }

        if (Sheets.Top is not null || _activePageIndex != 0 || _isGameSettingsOpen)
        {
            return;
        }

        // Hang the menu from the button's bottom-right corner. The sheet host is hidden (so
        // never laid out) until a sheet opens; MainContent shares its row and is, so measure
        // from that and add back its margin.
        var size = LibraryMenuButton.Bounds.Size;
        if (LibraryMenuButton.TranslatePoint(new Point(size.Width, size.Height), MainContent) is { } corner)
        {
            var margin = MainContent.Margin;
            _libraryMenu.PopoverMargin = new Thickness(
                0,
                margin.Top + corner.Y + 10,
                Math.Max(0, MainContent.Bounds.Width + margin.Right - corner.X),
                0);
        }

        _ = Sheets.ShowAsync(_libraryMenu);
    }

    private void UpdateSearchExpanded()
    {
        var expanded = SearchBox.IsFocused || !string.IsNullOrEmpty(SearchBox.Text);
        SetClass(SearchBox, "expanded", expanded);
        SearchBox.PlaceholderText = expanded ? Localization.Instance.Get("Library.SearchWatermark") : null;
    }

    // Slides the thumb under the active tab; without animation when the tabs are first laid
    // out or change width (a new language).
    private void MovePageTabThumb(bool animate)
    {
        var tab = _activePageIndex switch
        {
            1 => OptionsTabButton,
            2 => TrophiesTabButton,
            _ => LibraryTabButton,
        };
        var bounds = tab.Bounds;
        if (bounds.Width <= 0)
        {
            return;
        }

        if (!animate)
        {
            PageTabThumb.Transitions = null;
        }

        PageTabThumb.Width = bounds.Width;
        PageTabThumb.RenderTransform = TransformOperations.Parse(
            FormattableString.Invariant($"translateX({bounds.X}px)"));
        if (!animate)
        {
            PageTabThumb.ClearValue(Animatable.TransitionsProperty);
        }
    }
}
