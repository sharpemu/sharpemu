// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SharpEmu.Libs.Pad;
using SharpEmu.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace SharpEmu.GUI;

public partial class MainWindow : Window
{
    private const int MaxConsoleLines = 4000;

    private static readonly IBrush DefaultLineBrush = new SolidColorBrush(Color.Parse("#C7CFDE"));
    private static readonly IBrush DimLineBrush = new SolidColorBrush(Color.Parse("#6B7488"));
    private static readonly IBrush InfoLineBrush = new SolidColorBrush(Color.Parse("#6FA8FF"));
    private static readonly IBrush WarningLineBrush = new SolidColorBrush(Color.Parse("#E8B341"));
    private static readonly IBrush ErrorLineBrush = new SolidColorBrush(Color.Parse("#F2777C"));
    private static readonly IBrush SuccessLineBrush = new SolidColorBrush(Color.Parse("#63D489"));

    private readonly List<GameEntry> _allGames = new();
    private readonly ObservableCollection<GameEntry> _visibleGames = new();
    private readonly AvaloniaList<LogLine> _consoleLines = new();
    private readonly List<LogLine> _allConsoleLines = new();
    private readonly ConcurrentQueue<(string Line, bool IsError)> _pendingLines = new();
    private readonly DispatcherTimer _consoleFlushTimer;

    private GuiSettings _settings = new();
    private EmulatorProcess? _emulator;
    private ConsoleWindow? _consoleWindow;
    private StreamWriter? _fileLog;
    private readonly SndPreviewPlayer _sndPreview = new();
    private string? _emulatorExePath;
    private bool _isRunning;
    private int _autoScrollTicks;

    // Discord Rich Presence state.
    private readonly long _launcherStartUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private DiscordRichPresence? _discord;
    private string? _runningGameName;
    private string? _runningGameTitleId;
    private long _runningSinceUnixSeconds;
    private int _detailLoadGeneration;
    private int _backdropGeneration;

    // Controller navigation state.
    private readonly DispatcherTimer _gamepadTimer;
    private uint _previousPadButtons;
    private long _navLeftNextAt;
    private long _navRightNextAt;
    private long _navUpNextAt;
    private long _navDownNextAt;

    public MainWindow()
    {
        InitializeComponent();

        GameList.ItemsSource = _visibleGames;
        ConsoleList.ItemsSource = _consoleLines;

        _consoleFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _consoleFlushTimer.Tick += (_, _) =>
        {
            FlushPendingConsoleLines();
            MaybeAutoScroll();
        };
        _consoleFlushTimer.Start();

        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        GameList.SelectionChanged += (_, _) => UpdateSelectedGame();
        GameList.DoubleTapped += (_, _) => LaunchSelected();
        SearchBox.TextChanged += (_, _) => RefreshVisibleGames();
        ConsoleSearchBox.TextChanged += (_, _) => RefreshVisibleConsoleLines();
        AddFolderButton.Click += async (_, _) => await AddFolderAsync();
        EmptyAddFolderButton.Click += async (_, _) => await AddFolderAsync();
        RescanButton.Click += async (_, _) => await RescanLibraryAsync();
        OpenFileButton.Click += async (_, _) => await OpenFileAsync();
        LaunchButton.Click += (_, _) => LaunchSelected();
        StopButton.Click += (_, _) => _emulator?.Stop();
        ClearLogButton.Click += (_, _) => { _consoleLines.Clear(); _allConsoleLines.Clear(); };
        StopButton.Click += (_, _) => StopEmulator();
        ClearLogButton.Click += (_, _) => _consoleLines.Clear();
        CopyLogButton.Click += async (_, _) => await CopyConsoleAsync();
        DetachConsoleButton.Click += (_, _) => ShowConsoleWindow();
        OptionsToggle.IsCheckedChanged += (_, _) => OptionsPanel.IsVisible = OptionsToggle.IsChecked == true;
        ConsoleToggle.IsCheckedChanged += (_, _) => ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;
        SelectLogFilePathButton.Click += async (_, _) => await SelectFilePathAsync();
        TitleMusicToggle.IsCheckedChanged += (_, _) => OnTitleMusicToggled();
        DiscordToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.DiscordRichPresence = DiscordToggle.IsChecked == true;
            UpdateDiscordPresence();
        };

        GameList.AddHandler(ContextRequestedEvent, OnGameContextRequested, RoutingStrategies.Tunnel);
        CtxLaunch.Click += (_, _) => LaunchSelected();
        CtxOpenFolder.Click += (_, _) => OpenSelectedGameFolder();
        CtxCopyPath.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.Path, "Path");
        CtxCopyTitleId.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.TitleId, "Title ID");
        CtxRemove.Click += (_, _) => RemoveSelectedFromLibrary();

        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => OnWindowClosing();

        DualSenseReader.EnsureStarted();
        XInputReader.EnsureStarted();
        _gamepadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _gamepadTimer.Tick += (_, _) => PollGamepad();
        _gamepadTimer.Start();
    }

    // ---- Controller navigation ----

    private void PollGamepad()
    {
        // DualSense wins when both are connected; XInput covers Xbox pads.
        if (!DualSenseReader.TryGetState(out var pad) && !XInputReader.TryGetState(out pad))
        {
            _previousPadButtons = 0;
            return;
        }

        if (!IsActive)
        {
            // Ignore input while the launcher is in the background (e.g. the
            // game window is focused and using the same controller).
            _previousPadButtons = pad.Buttons;
            return;
        }

        var now = Environment.TickCount64;
        var left = (pad.Buttons & 0x0080) != 0 || pad.LeftX < 64;
        var right = (pad.Buttons & 0x0020) != 0 || pad.LeftX > 192;
        var up = (pad.Buttons & 0x0010) != 0 || pad.LeftY < 64;
        var down = (pad.Buttons & 0x0040) != 0 || pad.LeftY > 192;

        if (ShouldNavigate(left, ref _navLeftNextAt, now))
        {
            MoveSelection(-1);
        }

        if (ShouldNavigate(right, ref _navRightNextAt, now))
        {
            MoveSelection(1);
        }

        if (ShouldNavigate(up, ref _navUpNextAt, now))
        {
            MoveSelection(-TilesPerRow());
        }

        if (ShouldNavigate(down, ref _navDownNextAt, now))
        {
            MoveSelection(TilesPerRow());
        }

        var pressed = pad.Buttons & ~_previousPadButtons;
        if ((pressed & 0x4000) != 0) // Cross
        {
            LaunchSelected();
        }

        if ((pressed & 0x2000) != 0) // Circle
        {
            StopEmulator();
        }

        _previousPadButtons = pad.Buttons;
    }

    /// <summary>
    /// Edge-triggered with hold-to-repeat: fires on press, then repeats
    /// after 400ms at 130ms intervals while held.
    /// </summary>
    private static bool ShouldNavigate(bool held, ref long nextAt, long now)
    {
        if (!held)
        {
            nextAt = 0;
            return false;
        }

        if (nextAt == 0)
        {
            nextAt = now + 400;
            return true;
        }

        if (now >= nextAt)
        {
            nextAt = now + 130;
            return true;
        }

        return false;
    }

    private void MoveSelection(int delta)
    {
        if (_visibleGames.Count == 0)
        {
            return;
        }

        var index = GameList.SelectedIndex < 0
            ? 0
            : Math.Clamp(GameList.SelectedIndex + delta, 0, _visibleGames.Count - 1);
        GameList.SelectedIndex = index;
        GameList.ScrollIntoView(index);
    }

    private int TilesPerRow()
    {
        // Tile footprint: 128 content + 20 item padding + 10 item margin.
        const double TileOuterWidth = 158;
        var width = GameList.Bounds.Width;
        return width > TileOuterWidth ? (int)(width / TileOuterWidth) : 1;
    }

    private async Task OnOpenedAsync()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var display = version is not null ? $"v{version.ToString(3)}" : "v0.0.1";
        display += BuildInfo.CommitSha is null
            ? " · dev"
            : BuildInfo.IsOfficialRelease
                ? $" · {BuildInfo.CommitSha}"
                : $" · UNOFFICIAL {BuildInfo.CommitSha}";
        VersionText.Text = display;
        Title = $"SharpEmu {display}";
        ToolTip.SetTip(VersionText, BuildInfo.Banner);

        _settings = GuiSettings.Load();
        ApplySettingsToControls();
        LocateEmulator();
        UpdateDiscordPresence();
        await RescanLibraryAsync();
    }

    // ---- Discord Rich Presence ----

    /// <summary>
    /// Publishes the launcher state to Discord: browsing while idle, the
    /// running game (with elapsed time) during emulation. No-ops when
    /// disabled or when no Discord application ID is configured.
    /// </summary>
    private void UpdateDiscordPresence()
    {
        if (!_settings.DiscordRichPresence || _settings.DiscordClientId.Length == 0)
        {
            _discord?.Dispose();
            _discord = null;
            return;
        }

        _discord ??= new DiscordRichPresence(_settings.DiscordClientId);
        if (_isRunning && _runningGameName is { } gameName)
        {
            _discord.SetPresence(
                $"Playing {gameName}",
                _runningGameTitleId,
                _runningSinceUnixSeconds);
        }
        else
        {
            // Discord does not render activities without timestamps, so the
            // browsing state carries the launcher's start time.
            _discord.SetPresence(
                "Browsing the library",
                $"{_allGames.Count} game(s)",
                _launcherStartUnixSeconds);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        args.Handled = true;
        switch (args.Key)
        {
            case Key.F11:
                OnWindowFullScreen(this, new RoutedEventArgs());
                break;
            default:
                args.Handled = false;
                break;
        }
    }

    private void OnWindowFullScreen(object sender, RoutedEventArgs args)
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = WindowState.Normal;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
        }
        else
        {
            WindowState = WindowState.FullScreen;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
        }
    }

    private void OnWindowClosing()
    {
        ReadControlsIntoSettings();
        _settings.Save();
        _consoleFlushTimer.Stop();
        _gamepadTimer.Stop();
        _sndPreview.Stop();
        _discord?.Dispose();
        _consoleWindow?.Close();
        _emulator?.Dispose();
        DropFileLog();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ---- Settings <-> controls ----

    private void ApplySettingsToControls()
    {
        LogLevelBox.SelectedIndex = _settings.LogLevel.ToLowerInvariant() switch
        {
            "trace" => 0,
            "debug" => 1,
            "info" => 2,
            "warning" or "warn" => 3,
            "error" => 4,
            "critical" or "fatal" => 5,
            _ => 2,
        };
        TraceImportsBox.Value = Math.Clamp(_settings.ImportTraceLimit, 0, 4096);
        StrictToggle.IsChecked = _settings.StrictDynlibResolution;
        LogToFileToggle.IsChecked = _settings.LogToFile;
        OverrideLogFileToggle.IsChecked = _settings.OverrideLogFile;
        TitleMusicToggle.IsChecked = _settings.PlayTitleMusic;
        ToolTip.SetTip(SelectLogFilePathButton, string.IsNullOrWhiteSpace(_settings.LogFilePath) ? "No path selected" : _settings.LogFilePath);
        DiscordToggle.IsChecked = _settings.DiscordRichPresence;
    }

    private void ReadControlsIntoSettings()
    {
        _settings.LogLevel = SelectedLogLevel();
        _settings.ImportTraceLimit = (int)(TraceImportsBox.Value ?? 0);
        _settings.StrictDynlibResolution = StrictToggle.IsChecked == true;
        _settings.LogToFile = LogToFileToggle.IsChecked == true;
        _settings.OverrideLogFile = OverrideLogFileToggle.IsChecked == true;
        _settings.PlayTitleMusic = TitleMusicToggle.IsChecked == true;
        _settings.DiscordRichPresence = DiscordToggle.IsChecked == true;
    }

    private string SelectedLogLevel()
    {
        return LogLevelBox.SelectedIndex switch
        {
            0 => "Trace",
            1 => "Debug",
            2 => "Info",
            3 => "Warning",
            4 => "Error",
            5 => "Critical",
            _ => "Info",
        };
    }

    // ---- Emulator discovery ----

    private void LocateEmulator()
    {
        var exeName = OperatingSystem.IsWindows() ? "SharpEmu.exe" : "SharpEmu";
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settings.EmulatorPath))
        {
            candidates.Add(_settings.EmulatorPath);
        }

        // The GUI and the CLI are the same executable: with arguments it runs
        // the emulator, so the preferred child process is this process itself.
        if (Environment.ProcessPath is { } selfPath &&
            Path.GetFileNameWithoutExtension(selfPath).Equals("SharpEmu", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(selfPath);
        }

        candidates.Add(Path.Combine(baseDirectory, exeName));
        candidates.Add(Path.Combine(baseDirectory, "win-x64", exeName));
        candidates.Add(Path.Combine(baseDirectory, "..", exeName));

        _emulatorExePath = candidates.FirstOrDefault(File.Exists) is { } found
            ? Path.GetFullPath(found)
            : null;

        EmulatorPathText.Text = _emulatorExePath is not null
            ? $"Emulator: {_emulatorExePath}"
            : "Emulator: SharpEmu executable not found — build SharpEmu.CLI first.";
    }

    // ---- Game library ----

    private async Task AddFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder containing games",
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var changed = false;
        if (!_settings.GameFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            _settings.GameFolders.Add(path);
            changed = true;
        }

        // Adding (or re-adding) a folder is an explicit signal to restore any
        // games beneath it that were removed from the library earlier.
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        changed |= _settings.ExcludedGames.RemoveAll(excluded =>
            excluded.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) > 0;

        if (changed)
        {
            _settings.Save();
        }

        await RescanLibraryAsync();
    }

    private async Task RescanLibraryAsync()
    {
        var folders = _settings.GameFolders.ToArray();
        var excluded = new HashSet<string>(_settings.ExcludedGames, StringComparer.OrdinalIgnoreCase);
        StatusBarRight.Text = "Scanning library…";

        var games = await Task.Run(() => ScanFolders(folders, excluded));

        _allGames.Clear();
        _allGames.AddRange(games);
        RefreshVisibleGames();
        LoadGameDetailsInBackground(games);
        UpdateDiscordPresence();
        StatusBarRight.Text = folders.Length == 0
            ? "Add a game folder to populate the library."
            : $"Library scanned: {games.Count} game(s) in {folders.Length} folder(s).";
    }

    /// <summary>
    /// Enriches games off the UI thread — decodes cover art and totals each
    /// game's install folder size — posting results back as they become
    /// ready. A newer scan invalidates older loads.
    /// </summary>
    private void LoadGameDetailsInBackground(IReadOnlyList<GameEntry> games)
    {
        var generation = ++_detailLoadGeneration;
        _ = Task.Run(() =>
        {
            // Covers first: they are cheap and the most visible, so the grid
            // fills with art before the (potentially slow) size pass runs.
            foreach (var game in games)
            {
                if (generation != _detailLoadGeneration)
                {
                    return;
                }

                if (game.CoverPath is null)
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(game.CoverPath);
                    var bitmap = Bitmap.DecodeToWidth(stream, 312);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _detailLoadGeneration)
                        {
                            game.Cover = bitmap;
                        }
                    });
                }
                catch (Exception)
                {
                    // A missing or undecodable image keeps the placeholder.
                }
            }

            foreach (var game in games)
            {
                if (generation != _detailLoadGeneration)
                {
                    return;
                }

                var size = ComputeInstallSize(game.Path);
                if (size > 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _detailLoadGeneration)
                        {
                            game.SizeBytes = size;
                        }
                    });
                }
            }
        });
    }

    /// <summary>
    /// Totals the size of the game's install folder (the directory holding
    /// the eboot), which is far more accurate than the eboot alone.
    /// </summary>
    private static long ComputeInstallSize(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        if (directory is null)
        {
            return 0;
        }

        long total = 0;
        try
        {
            var enumeration = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
            };
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", enumeration))
            {
                total += file.Length;
            }
        }
        catch (Exception)
        {
            // Fall back to whatever was accumulated so far.
        }

        return total;
    }

    private static List<GameEntry> ScanFolders(IReadOnlyList<string> folders, IReadOnlySet<string> excludedPaths)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 8,
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "eboot.bin", enumeration))
                {
                    var fullPath = Path.GetFullPath(file);
                    if (!seen.Add(fullPath) || excludedPaths.Contains(fullPath))
                    {
                        continue;
                    }

                    long size = 0;
                    try
                    {
                        size = new FileInfo(fullPath).Length;
                    }
                    catch (IOException)
                    {
                    }

                    var (title, titleId) = TryReadParamJson(fullPath);
                    games.Add(new GameEntry(
                        title ?? GameNameFor(fullPath), titleId, fullPath, size,
                        FindCoverFor(fullPath), FindBackgroundFor(fullPath)));
                }
            }
            catch (Exception)
            {
                // Skip folders that fail to enumerate.
            }
        }

        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return games;
    }

    /// <summary>
    /// Reads the game title and title id from sce_sys/param.json next to the
    /// executable, when present.
    /// </summary>
    private static (string? Title, string? TitleId) TryReadParamJson(string ebootPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(ebootPath);
            if (directory is null)
            {
                return (null, null);
            }

            var paramPath = Path.Combine(directory, "sce_sys", "param.json");
            if (!File.Exists(paramPath))
            {
                return (null, null);
            }

            // ReadAllText handles a UTF-8 BOM, which JsonDocument rejects in
            // raw bytes.
            using var document = JsonDocument.Parse(File.ReadAllText(paramPath));
            var root = document.RootElement;

            string? titleId = null;
            if (root.TryGetProperty("titleId", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                titleId = idElement.GetString();
            }

            string? title = null;
            if (root.TryGetProperty("localizedParameters", out var localized) &&
                localized.ValueKind == JsonValueKind.Object)
            {
                if (localized.TryGetProperty("defaultLanguage", out var language) &&
                    language.ValueKind == JsonValueKind.String &&
                    localized.TryGetProperty(language.GetString()!, out var defaultBlock) &&
                    defaultBlock.ValueKind == JsonValueKind.Object &&
                    defaultBlock.TryGetProperty("titleName", out var titleName) &&
                    titleName.ValueKind == JsonValueKind.String)
                {
                    title = titleName.GetString();
                }
                else
                {
                    foreach (var property in localized.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object &&
                            property.Value.TryGetProperty("titleName", out var anyTitleName) &&
                            anyTitleName.ValueKind == JsonValueKind.String)
                        {
                            title = anyTitleName.GetString();
                            break;
                        }
                    }
                }
            }

            return (
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(titleId) ? null : titleId);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Finds the cover art shipped with the game: sce_sys/icon0.png next to
    /// the executable (falling back to pic0.png).
    /// </summary>
    private static string? FindCoverFor(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        if (directory is null)
        {
            return null;
        }

        var sceSys = Path.Combine(directory, "sce_sys");
        foreach (var candidate in new[] { "icon0.png", "pic0.png" })
        {
            var coverPath = Path.Combine(sceSys, candidate);
            if (File.Exists(coverPath))
            {
                return coverPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the key art shipped with the game (sce_sys/pic0.png, falling
    /// back to pic1.png), used as the window backdrop when selected.
    /// </summary>
    private static string? FindBackgroundFor(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        if (directory is null)
        {
            return null;
        }

        var sceSys = Path.Combine(directory, "sce_sys");
        foreach (var candidate in new[] { "pic0.png", "pic1.png" })
        {
            var backgroundPath = Path.Combine(sceSys, candidate);
            if (File.Exists(backgroundPath))
            {
                return backgroundPath;
            }
        }

        return null;
    }

    private static string GameNameFor(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        var name = directory is not null ? Path.GetFileName(directory) : null;
        return string.IsNullOrEmpty(name) ? Path.GetFileName(ebootPath) : name;
    }

    // ---- Game context menu ----

    /// <summary>
    /// Selects the tile under the pointer before its context menu opens, and
    /// suppresses the menu on empty grid space.
    /// </summary>
    private void OnGameContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is not GameEntry game)
        {
            e.Handled = true;
            return;
        }

        GameList.SelectedItem = game;
        CtxLaunch.IsEnabled = !_isRunning;
        CtxCopyTitleId.IsEnabled = game.TitleId is not null;
    }

    private void OpenSelectedGameFolder()
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{game.Path}\"",
                    UseShellExecute = false,
                });
            }
            else if (Path.GetDirectoryName(game.Path) is { } directory)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                    Arguments = $"\"{directory}\"",
                    UseShellExecute = false,
                });
            }
        }
        catch (Exception ex)
        {
            StatusBarRight.Text = $"Could not open folder: {ex.Message}";
        }
    }

    private async Task CopyToClipboardAsync(string? text, string what)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(text);
        StatusBarRight.Text = $"{what} copied to clipboard.";
    }

    private void RemoveSelectedFromLibrary()
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        if (!_settings.ExcludedGames.Contains(game.Path, StringComparer.OrdinalIgnoreCase))
        {
            _settings.ExcludedGames.Add(game.Path);
            _settings.Save();
        }

        _allGames.RemoveAll(g => string.Equals(g.Path, game.Path, StringComparison.OrdinalIgnoreCase));
        GameList.SelectedItem = null;
        RefreshVisibleGames();
        StatusBarRight.Text = $"Removed “{game.Name}” from the library. Re-add its folder to restore it.";
    }

    private void RefreshVisibleGames()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var selectedPath = (GameList.SelectedItem as GameEntry)?.Path;

        _visibleGames.Clear();
        foreach (var game in _allGames)
        {
            if (query.Length == 0 ||
                game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                game.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (game.TitleId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                _visibleGames.Add(game);
            }
        }

        GameCountText.Text = _visibleGames.Count == 1 ? "1 game" : $"{_visibleGames.Count} games";

        if (selectedPath is not null &&
            _visibleGames.FirstOrDefault(g => g.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
                is { } reselected)
        {
            GameList.SelectedItem = reselected;
        }

        EmptyState.IsVisible = _visibleGames.Count == 0;
        if (_visibleGames.Count == 0)
        {
            var hasFilter = query.Length > 0;
            EmptyStateTitle.Text = hasFilter ? "No games match your search" : "Your library is empty";
            EmptyStateHint.Text = hasFilter
                ? $"Nothing in the library matches “{query}”."
                : "Add a folder containing your games to get started.";
            EmptyAddFolderButton.IsVisible = !hasFilter;
        }

        UpdateSelectedGame();
    }

    private void UpdateSelectedGame()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            SelectedGameTitle.Text = game.Name;
            SelectedGamePath.Text = game.Path;
            SelectedCoverPanel.DataContext = game;
            _ = UpdateBackdropAsync(game);
            PlaySelectedGamePreview(game);
        }
        else
        {
            SelectedGameTitle.Text = "No game selected";
            SelectedGamePath.Text = "Pick a game from the library, or open an eboot.bin directly.";
            SelectedCoverPanel.DataContext = null;
            _ = UpdateBackdropAsync(null);
            _sndPreview.Stop();
        }

        UpdateRunButtons();
    }

    /// <summary>
    /// Loops the selected game's sce_sys/snd0.at9 preview music, console
    /// home screen style. Silent while a game is running or when disabled
    /// in the options.
    /// </summary>
    private void PlaySelectedGamePreview(GameEntry game)
    {
        if (_isRunning || !_settings.PlayTitleMusic)
        {
            return;
        }

        var directory = Path.GetDirectoryName(game.Path);
        var sndPath = directory is null ? null : Path.Combine(directory, "sce_sys", "snd0.at9");
        if (sndPath is not null && File.Exists(sndPath))
        {
            _sndPreview.Play(sndPath);
        }
        else
        {
            _sndPreview.Stop();
        }
    }

    private void OnTitleMusicToggled()
    {
        _settings.PlayTitleMusic = TitleMusicToggle.IsChecked == true;
        if (!_settings.PlayTitleMusic)
        {
            _sndPreview.Stop();
        }
        else if (GameList.SelectedItem is GameEntry game)
        {
            PlaySelectedGamePreview(game);
        }
    }

    /// <summary>Pauses the preview music while the window is minimized.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            if (WindowState == WindowState.Minimized)
            {
                _sndPreview.Pause();
            }
            else
            {
                _sndPreview.Resume();
            }
        }
    }

    /// <summary>
    /// Fades the window backdrop to the selected game's key art. The image
    /// decodes off the UI thread and is cached on the entry; a newer
    /// selection cancels the fade-in of an older one.
    /// </summary>
    private async Task UpdateBackdropAsync(GameEntry? game)
    {
        var generation = ++_backdropGeneration;
        BackdropImage.Opacity = 0;

        if (game?.BackgroundPath is null)
        {
            return;
        }

        if (game.Background is null)
        {
            try
            {
                var path = game.BackgroundPath;
                game.Background = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(stream, 1600);
                });
            }
            catch (Exception)
            {
                return; // undecodable key art: keep the plain background
            }
        }

        if (generation == _backdropGeneration)
        {
            BackdropImage.Source = game.Background;
            BackdropImage.Opacity = 1.0;
        }
    }

    // ---- Launching ----

    private async Task OpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open an executable to launch",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PS executables") { Patterns = new[] { "eboot.bin", "*.bin", "*.self", "*.elf" } },
                FilePickerFileTypes.All,
            },
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            Launch(path, Path.GetFileName(path));
        }
    }

    private void LaunchSelected()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            Launch(game.Path, game.Name, game.TitleId);
        }
    }

    private void Launch(string ebootPath, string displayName, string? titleId = null)
    {
        if (_isRunning)
        {
            return;
        }

        if (_emulatorExePath is null)
        {
            LocateEmulator();
            if (_emulatorExePath is null)
            {
                AppendConsoleLine("SharpEmu executable not found. Build the SharpEmu.CLI project first (dotnet build).", ErrorLineBrush);
                return;
            }
        }

        _sndPreview.Stop();
        ReadControlsIntoSettings();
        _settings.Save();

        var arguments = new List<string>
        {
            "--cpu-engine=native",
            $"--log-level={_settings.LogLevel.ToLowerInvariant()}",
        };
        if (_settings.StrictDynlibResolution)
        {
            arguments.Add("--strict");
        }

        if (_settings.ImportTraceLimit > 0)
        {
            arguments.Add($"--trace-imports={_settings.ImportTraceLimit}");
        }

        arguments.Add(ebootPath);

        _consoleLines.Clear();
        ConsoleToggle.IsChecked = true;

        // Mirror everything the console pane shows into a log file for the
        // duration of the run, regardless of the emulator's log level.
        DropFileLog();
        if (_settings.LogToFile)
        {
            string filePath;
            if (!string.IsNullOrWhiteSpace(_settings.LogFilePath))
            {
                if (_settings.OverrideLogFile)
                {
                    filePath = _settings.LogFilePath;
                }
                else
                {
                    string path = _settings.LogFilePath;
                    string id = string.IsNullOrWhiteSpace(titleId) ? "UNKNOWN" : titleId;
                    foreach (var invalidChar in Path.GetInvalidFileNameChars())
                    {
                        id = id.Replace(invalidChar.ToString(), "");
                    }
                    string identifier = $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}";

                    string? dir = Path.GetDirectoryName(path);
                    string? fileName = Path.GetFileNameWithoutExtension(path);
                    string? extension = Path.GetExtension(path);

                    string newFileName = $"{fileName}-{identifier}{extension}";
                    filePath = string.IsNullOrEmpty(dir)
                        ? newFileName
                        : Path.Combine(dir, newFileName);
                }
            }
            else
            {
                filePath = BuildLogFilePath(titleId) ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    _fileLog = new StreamWriter(filePath, append: false);
                    AppendConsoleLine($"Log file: {filePath}", DimLineBrush);
                }
                catch (Exception ex)
                {
                    AppendConsoleLine($"Could not open the log file: {ex.Message}", WarningLineBrush);
                }
            }
        }

        AppendConsoleLine($"$ SharpEmu {string.Join(' ', arguments)}", DimLineBrush);

        var emulator = new EmulatorProcess();
        emulator.OutputReceived += (line, isError) => _pendingLines.Enqueue((line, isError));
        emulator.Exited += code => Dispatcher.UIThread.Post(() => OnEmulatorExited(code));

        try
        {
            emulator.Start(_emulatorExePath, arguments, Path.GetDirectoryName(ebootPath));
        }
        catch (Exception ex)
        {
            emulator.Dispose();
            AppendConsoleLine($"Failed to start the emulator: {ex.Message}", ErrorLineBrush);
            DropFileLog();
            return;
        }

        _emulator = emulator;
        _isRunning = true;
        _runningGameName = displayName;
        _runningGameTitleId = _allGames
            .FirstOrDefault(game => game.Path.Equals(ebootPath, StringComparison.OrdinalIgnoreCase))?
            .TitleId;
        _runningSinceUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        StatusDot.Fill = SuccessLineBrush;
        StatusText.Text = $"Running — {displayName}";
        StatusBarRight.Text = $"Running {displayName}";
        UpdateRunButtons();
        UpdateDiscordPresence();
    }

    /// <summary>
    /// Stops the running game and updates status/presence immediately. The
    /// process-exit path still runs when the corpse is collected, but a game
    /// wedged in a GPU driver call can keep its process alive for a long
    /// time after termination — the launcher should not look (or tell
    /// Discord it is) "playing" during that window.
    /// </summary>
    private void StopEmulator()
    {
        if (!_isRunning)
        {
            return;
        }

        _emulator?.Stop();
        _runningGameName = null;
        _runningGameTitleId = null;
        StatusText.Text = "Stopping…";
        StatusBarRight.Text = "Stopping…";
        UpdateDiscordPresence();
    }

    /// <summary>
    /// Builds "user/logs/&lt;titleId&gt;-&lt;timestamp&gt;.log" next to the emulator
    /// executable, following the same portable-data convention as savedata.
    /// </summary>
    private string? BuildLogFilePath(string? titleId)
    {
        try
        {
            var exeDirectory = Path.GetDirectoryName(_emulatorExePath);
            if (string.IsNullOrEmpty(exeDirectory))
            {
                return null;
            }

            var logsDirectory = Path.Combine(exeDirectory, "user", "logs");
            Directory.CreateDirectory(logsDirectory);

            var id = string.IsNullOrWhiteSpace(titleId) ? "UNKNOWN" : titleId;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                id = id.Replace(invalid, '_');
            }

            return Path.Combine(logsDirectory, $"{id}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch (Exception)
        {
            return null; // unwritable location: launch continues without a log file
        }
    }

    private void OnEmulatorExited(int exitCode)
    {
        FlushPendingConsoleLines();
        _isRunning = false;
        _emulator?.Dispose();
        _emulator = null;

        var meaning = exitCode switch
        {
            0 => "OK",
            1 => "invalid arguments",
            2 => "eboot not found",
            3 => "runtime exception",
            4 => "emulation error",
            _ => "unknown",
        };
        var brush = exitCode == 0 ? SuccessLineBrush : ErrorLineBrush;
        AppendConsoleLine($"Process exited with code {exitCode} ({meaning}).", brush);
        CloseFileLogSoon();

        StatusDot.Fill = exitCode == 0 ? (IBrush)SuccessLineBrush : ErrorLineBrush;
        StatusText.Text = $"Exited with code {exitCode} ({meaning})";
        StatusBarRight.Text = "Idle";
        _runningGameName = null;
        _runningGameTitleId = null;
        UpdateRunButtons();
        UpdateDiscordPresence();
    }

    private void UpdateRunButtons()
    {
        LaunchButton.IsEnabled = !_isRunning && GameList.SelectedItem is GameEntry;
        StopButton.IsEnabled = _isRunning;
        OpenFileButton.IsEnabled = !_isRunning;
    }

    private async Task SelectFilePathAsync()
    {
        SaveFilePickerResult result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = "Select where to save the Log file",
            SuggestedFileName = "SharpEmuLog",
            DefaultExtension = "log",
            FileTypeChoices =
                [
                    new FilePickerFileType("Plain Text Files") { Patterns = ["*.txt"] },
                    new FilePickerFileType("Log Files") { Patterns = ["*.log"] }
                ]
        });

        if (result.File is not null)
        {
            _settings.LogFilePath = result.File.Path.LocalPath;
            ToolTip.SetTip(SelectLogFilePathButton, _settings.LogFilePath);
        }
    }

    // ---- Console ----

    private void FlushPendingConsoleLines()
    {
        if (_pendingLines.IsEmpty)
        {
            return;
        }

        var incoming = new List<LogLine>();
        while (_pendingLines.TryDequeue(out var pending))
        {
            WriteFileLog(pending.Line);
            incoming.Add(new LogLine(pending.Line, BrushForLine(pending.Line)));
        }

        FlushFileLog();

        _allConsoleLines.AddRange(incoming);

        string query = ConsoleSearchBox.Text ?? string.Empty;

        IEnumerable<LogLine> linesToAdd = incoming;
        if (!string.IsNullOrWhiteSpace(query))
        {
            linesToAdd = incoming.Where(line =>
                line.Text != null &&
                line.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        _consoleLines.AddRange(linesToAdd);

        var overflow = _consoleLines.Count - MaxConsoleLines;
        while (_allConsoleLines.Count > MaxConsoleLines)
        {
            var droppedLine = _allConsoleLines[0];
            _allConsoleLines.RemoveAt(0);
            if (_consoleLines.Count > 0 && _consoleLines[0] == droppedLine)
            {
                _consoleLines.RemoveAt(0);
            }
        }

        _autoScrollTicks = 3;
    }

    private void AppendConsoleLine(string text, IBrush brush)
    {
        WriteFileLog(text);
        FlushFileLog();

        var line = new LogLine(text, brush);
        _allConsoleLines.Add(line);

        string query = ConsoleSearchBox.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query) || (text != null && text.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            _consoleLines.Add(line);
        }

        while (_allConsoleLines.Count > MaxConsoleLines)
        {
            var droppedLine = _allConsoleLines[0];
            _allConsoleLines.RemoveAt(0);
            if (_consoleLines.Count > 0 && _consoleLines[0] == droppedLine)
            {
                _consoleLines.RemoveAt(0);
            }
        }

        _autoScrollTicks = 3;
        MaybeAutoScroll();
    }

    private void RefreshVisibleConsoleLines()
    {
        string query = ConsoleSearchBox.Text ?? string.Empty;

        _consoleLines.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            _consoleLines.AddRange(_allConsoleLines);
        }
        else
        {
            var filtered = _allConsoleLines.Where(line =>
                line.Text != null &&
                line.Text.Contains(query, StringComparison.OrdinalIgnoreCase));

            _consoleLines.AddRange(filtered);
        }
    }

    // ---- Console-to-file mirroring ----

    private void WriteFileLog(string text)
    {
        if (_fileLog is not { } writer)
        {
            return;
        }

        try
        {
            writer.Write('[');
            writer.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
            writer.Write("] ");
            writer.WriteLine(text);
        }
        catch (Exception)
        {
            DropFileLog(); // unwritable (disk full, etc.): stop mirroring
        }
    }

    private void FlushFileLog()
    {
        try
        {
            _fileLog?.Flush();
        }
        catch (Exception)
        {
            DropFileLog();
        }
    }

    private void DropFileLog()
    {
        var writer = _fileLog;
        _fileLog = null;
        try
        {
            writer?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// The pipe reader threads can deliver a final burst after the exit
    /// event, so the file stays open for one more flush cycle.
    /// </summary>
    private void CloseFileLogSoon()
    {
        if (_fileLog is not { } writer)
        {
            return;
        }

        DispatcherTimer.RunOnce(() =>
        {
            if (ReferenceEquals(_fileLog, writer))
            {
                FlushPendingConsoleLines();
                DropFileLog();
            }
        }, TimeSpan.FromMilliseconds(400));
    }

    private void MaybeAutoScroll()
    {
        // ScrollToEnd is applied over a few flush-timer ticks because the
        // virtualizing panel re-estimates its extent after large batches, and
        // a single scroll can land short of the true end. A synchronous
        // ScrollIntoView during rapid adds is avoided entirely — it can crash
        // the panel with "Invalid Arrange rectangle".
        if (_autoScrollTicks <= 0 || AutoScrollCheck.IsChecked != true)
        {
            return;
        }

        _autoScrollTicks--;
        (ConsoleList.Scroll as ScrollViewer)?.ScrollToEnd();
    }

    private static IBrush BrushForLine(string line)
    {
        if (line.Contains("[ERROR]", StringComparison.Ordinal) ||
            line.Contains("[CRITICAL]", StringComparison.Ordinal))
        {
            return ErrorLineBrush;
        }

        if (line.Contains("[WARNING]", StringComparison.Ordinal))
        {
            return WarningLineBrush;
        }

        if (line.Contains("[INFO]", StringComparison.Ordinal))
        {
            return InfoLineBrush;
        }

        if (line.Contains("[DEBUG]", StringComparison.Ordinal) ||
            line.Contains("[TRACE]", StringComparison.Ordinal))
        {
            return DimLineBrush;
        }

        return DefaultLineBrush;
    }

    private async Task CopyConsoleAsync()
    {
        if (_consoleLines.Count == 0 || Clipboard is null)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, _consoleLines.Select(line => line.Text));
        await Clipboard.SetTextAsync(text);
    }

    private void ShowConsoleWindow()
    {
        if (_consoleWindow is { } window)
        {
            window.Activate();
            return;
        }

        ConsoleSearchBox.Text = string.Empty;
        ConsoleToggle.IsChecked = false;
        ConsolePanel.IsVisible = false;
        _consoleWindow = new ConsoleWindow(
            _consoleLines,
            () => { _consoleLines.Clear(); _allConsoleLines.Clear(); },
            AutoScrollCheck.IsChecked == true);
        _consoleWindow.Closed += (_, _) =>
        {
            _consoleWindow = null;
            ConsoleToggle.IsChecked = true;
            ConsolePanel.IsVisible = true;
        };
        _consoleWindow.Show(this);
    }
}
