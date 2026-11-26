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

    // Unpacks a fake .pkg into <chosen folder>/<game title>, then adds that game folder to the library.
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

        await InstallPkgFileAsync(pkgPath);
    }

    // Launching a package that cannot run in place (a PS4 .pkg) offers to install that one package:
    // name and needed disk space first, then the usual folder choice and install sheet.
    private async Task OfferInstallPackageAsync(GameEntry game)
    {
        if (_isRunning || _isClosing || Sheets.Top is not null)
        {
            return;
        }

        var neededBytes = await Task.Run(() =>
        {
            try
            {
                using var package = GamePackage.Open(game.Path);
                return package.GetInstalledSize();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return game.SizeBytes;
            }
        });

        var confirm = new PkgConfirmDialog(
            Localization.Instance.Format(
                "Library.Pkg.PromptMessage",
                game.Name,
                (neededBytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)));
        if (await Sheets.ShowAsync(confirm) is true)
        {
            await InstallPkgFileAsync(game.Path);
        }
    }

    // Installs a package listed in the library: "Install PKG" from its context menu.
    private async Task InstallSelectedPkgAsync()
    {
        if (!_isRunning && GameList.SelectedItem is GameEntry { IsPackage: true } game)
        {
            await InstallPkgFileAsync(game.Path);
        }
    }

    private async Task InstallPkgFileAsync(string pkgPath)
    {
        var loc = Localization.Instance;
        if (!TryReadPackageInfo(pkgPath, out var titleId, out var folderName, out var isGame, out var category))
        {
            return;
        }

        if (!isGame)
        {
            ReportSkippedNotGame(pkgPath, category);
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

        if (!await InstallPackageIntoAsync(pkgPath, parent, folderName))
        {
            return;
        }

        await RescanLibraryAsync(addingFolder: true);
    }

    private void ReportSkippedNotGame(string pkgPath, string? category) =>
        AppendConsoleLine(
            Localization.Instance.Format("Library.Pkg.SkippedNotGame", Path.GetFileName(pkgPath), category ?? string.Empty),
            ErrorLineBrush);

    private bool TryReadPackageInfo(string pkgPath, out string titleId, out string folderName, out bool isGame, out string? category)
    {
        try
        {
            using var package = GamePackage.Open(pkgPath);
            titleId = package.TitleId;
            folderName = package.GetFolderName(Path.GetFileNameWithoutExtension(pkgPath));
            category = package.Category;
            isGame = package.IsGameApplication;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppendConsoleLine(Localization.Instance.Format("Library.Pkg.Failed", exception.Message), ErrorLineBrush);
            titleId = string.Empty;
            folderName = string.Empty;
            category = null;
            isGame = false;
            return false;
        }
    }

    // Shows the install sheet for one package. The caller owns the library rescan.
    private async Task<bool> InstallPackageIntoAsync(string pkgPath, string parent, string folderName)
    {
        var destination = Path.Combine(parent, folderName);
        if (!File.Exists(Path.Combine(destination, "eboot.bin")))
        {
            var dialog = new PkgInstallDialog(Path.GetFileName(pkgPath), pkgPath, destination);
            if (await Sheets.ShowAsync(dialog) is not true)
            {
                return false;
            }
        }

        AddInstalledGameToLibrary(destination);
        return true;
    }

    // Adds only the installed game's folder, not its parent: the parent may hold other games the
    // user never added. Nothing changes when a library folder already covers the game.
    private void AddInstalledGameToLibrary(string gameFolder)
    {
        if (IsUnderLibraryFolder(_settings.GameFolders, gameFolder))
        {
            return;
        }

        _settings.GameFolders.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameFolder)));
        _settings.Save();
    }

    internal static bool IsUnderLibraryFolder(IEnumerable<string> libraryFolders, string path)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var folder in libraryFolders)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (string.Equals(target, root, GameLibraryPath.Comparison) ||
                target.StartsWith(root + Path.DirectorySeparatorChar, GameLibraryPath.Comparison))
            {
                return true;
            }
        }

        return false;
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
