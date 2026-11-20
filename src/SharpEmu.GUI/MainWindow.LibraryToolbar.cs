// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using SharpEmu.HLE.FileSystem.Pkg;

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

        _libraryMenu.InstallPkgButton.Click += async (_, _) =>
        {
            _libraryMenu.Close();
            await InstallPkgAsync();
        };

        PageTabStrip.SizeChanged += (_, _) => MovePageTabThumb(animate: false);
        UpdateSearchExpanded();
    }

    // Unpacks a fake .pkg into <chosen folder>/<title id>, then adds the chosen folder to the library.
    private async Task InstallPkgAsync()
    {
        var loc = Localization.Instance;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = loc.Get("Dialog.OpenPkg"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(loc.Get("Dialog.PkgFiles")) { Patterns = ["*.pkg"] },
                FilePickerFileTypes.All,
            ],
        });
        var pkgPath = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(pkgPath))
        {
            return;
        }

        if (!TryReadTitleId(pkgPath, out var titleId))
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = loc.Get("Dialog.ChoosePkgInstallFolder"),
            AllowMultiple = false,
        });
        var parent = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(parent))
        {
            return;
        }

        if (!await InstallPackageIntoAsync(pkgPath, parent, titleId))
        {
            return;
        }

        await RescanLibraryAsync(addingFolder: true);
    }

    // A folder picked by "Add folder" may hold downloaded .pkg files instead of extracted games.
    // Install those packages in place so they end up in the library without a second manual step.
    // Only the chosen folder is considered: recursing into a parent such as Downloads would pull
    // in every package the user has ever downloaded.
    private async Task InstallPackagesInFolderAsync(string folder)
    {
        string[] packages;
        try
        {
            packages = Directory.EnumerateFiles(folder, "*.pkg", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendConsoleLine(Localization.Instance.Format("Library.Pkg.Failed", exception.Message), ErrorLineBrush);
            return;
        }

        foreach (var pkgPath in packages)
        {
            if (!TryReadTitleId(pkgPath, out var titleId))
            {
                continue;
            }

            // Cancelling one package stops the batch; later packages stay untouched.
            if (!await InstallPackageIntoAsync(pkgPath, folder, titleId))
            {
                return;
            }
        }
    }

    private bool TryReadTitleId(string pkgPath, out string titleId)
    {
        try
        {
            using var package = PkgPackage.Open(pkgPath);
            titleId = package.TitleId;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppendConsoleLine(Localization.Instance.Format("Library.Pkg.Failed", exception.Message), ErrorLineBrush);
            titleId = string.Empty;
            return false;
        }
    }

    // Shows the install sheet for one package. The caller owns the library rescan.
    private async Task<bool> InstallPackageIntoAsync(string pkgPath, string parent, string titleId)
    {
        var destination = Path.Combine(parent, string.IsNullOrWhiteSpace(titleId) ? Path.GetFileNameWithoutExtension(pkgPath) : titleId);
        if (File.Exists(Path.Combine(destination, "eboot.bin")))
        {
            return true; // Already installed; the folder scan will pick it up.
        }

        var dialog = new PkgInstallDialog(Path.GetFileName(pkgPath), pkgPath, destination);
        if (await Sheets.ShowAsync(dialog) is not true)
        {
            return false;
        }

        if (!_settings.GameFolders.Contains(parent, GameLibraryPath.Comparer))
        {
            _settings.GameFolders.Add(parent);
            _settings.Save();
        }

        return true;
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
