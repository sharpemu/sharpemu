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

    // A folder picked by "Add folder" may hold downloaded .pkg files instead of extracted games.
    // Walk it recursively with the same depth limit as the library scan, install every package
    // under the chosen root (named after the game's title) and let the caller's rescan pick them all
    // up. Packages whose game is already extracted anywhere under the root are left alone.
    private async Task InstallPackagesInFolderAsync(string folder)
    {
        string[] packages;
        try
        {
            packages = Directory.EnumerateFiles(folder, "*.pkg", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendConsoleLine(Localization.Instance.Format("Library.Pkg.Failed", exception.Message), ErrorLineBrush);
            return;
        }

        var installed = await Task.Run(() => FindInstalledTitles(folder));

        // Patches and DLC share the base game's title id and the emulator cannot overlay them, so
        // only base games are installed.
        var installable = new List<(string Path, string FolderName)>();
        foreach (var pkgPath in packages)
        {
            if (!TryReadPackageInfo(pkgPath, out var titleId, out var folderName, out var isGame, out var category))
            {
                continue;
            }

            if (!isGame)
            {
                ReportSkippedNotGame(pkgPath, category);
                continue;
            }

            // The game may already sit elsewhere under the root (next to its package, or from an
            // earlier manual install): installing it again would show two versions.
            var key = string.IsNullOrWhiteSpace(titleId) ? Path.GetFileNameWithoutExtension(pkgPath) : titleId;
            if (installed.TryGetValue(key, out var existing))
            {
                AppendConsoleLine(Localization.Instance.Format("Library.Pkg.AlreadyInstalled", Path.GetFileName(pkgPath), existing), DimLineBrush);
                continue;
            }

            if (!File.Exists(Path.Combine(Path.Combine(folder, folderName), "eboot.bin")))
            {
                installable.Add((pkgPath, folderName));
                installed[key] = Path.Combine(folder, folderName);
            }
        }

        if (installable.Count == 0)
        {
            return;
        }

        long neededBytes = 0;
        await Task.Run(() =>
        {
            foreach (var (pkgPath, _) in installable)
            {
                try
                {
                    using var package = GamePackage.Open(pkgPath);
                    neededBytes += package.GetInstalledSize();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // The install itself reports the problem; the estimate just misses this package.
                }
            }
        });

        var confirm = new PkgConfirmDialog(
            Localization.Instance.Format(
                "Library.Pkg.ConfirmMessage",
                installable.Count,
                (neededBytes / (1024.0 * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture)));
        if (await Sheets.ShowAsync(confirm) is not true)
        {
            return;
        }

        foreach (var (pkgPath, folderName) in installable)
        {
            // Cancelling one package stops the batch; later packages stay untouched.
            if (!await InstallPackageIntoAsync(pkgPath, folder, folderName))
            {
                return;
            }
        }
    }

    // Title id -> folder of every game already extracted under `root` (a folder with eboot.bin and
    // sce_sys/param.sfo or param.json), searched as deep as the package scan.
    private static Dictionary<string, string> FindInstalledTitles(string root)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var eboot in Directory.EnumerateFiles(root, "eboot.bin", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                MatchCasing = MatchCasing.CaseInsensitive,
            }))
            {
                var directory = Path.GetDirectoryName(eboot)!;
                if (ReadInstalledTitleId(directory) is { } titleId)
                {
                    titles.TryAdd(titleId, directory);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return titles;
    }

    // TITLE_ID of an extracted game: sce_sys/param.sfo for PS4, sce_sys/param.json for PS5.
    internal static string? ReadInstalledTitleId(string gameDirectory)
    {
        var sceSys = Path.Combine(gameDirectory, "sce_sys");
        try
        {
            var sfoPath = Path.Combine(sceSys, "param.sfo");
            if (File.Exists(sfoPath) &&
                PkgParamSfo.ReadString(File.ReadAllBytes(sfoPath), "TITLE_ID") is { Length: > 0 } sfoTitleId)
            {
                return sfoTitleId;
            }

            var jsonPath = Path.Combine(sceSys, "param.json");
            if (File.Exists(jsonPath))
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(jsonPath));
                if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("titleId", out var titleId) &&
                    titleId.ValueKind == System.Text.Json.JsonValueKind.String &&
                    titleId.GetString() is { Length: > 0 } jsonTitleId)
                {
                    return jsonTitleId;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }

        return null;
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
