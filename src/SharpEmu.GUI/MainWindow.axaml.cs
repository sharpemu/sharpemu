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
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Windows;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Net.Http.Headers;

namespace SharpEmu.GUI;

public partial class MainWindow : Window
{
    private const int MaxConsoleLines = 4000;
    private const int MaxConsoleLinesPerFlush = 500;
    private const double LaunchBlurRadius = 12;
    private const double BlurTransitionSeconds = 0.24;

    private static readonly IBrush DefaultLineBrush = new SolidColorBrush(Color.Parse("#C7CFDE"));
    private static readonly IBrush DimLineBrush = new SolidColorBrush(Color.Parse("#6B7488"));
    private static readonly IBrush InfoLineBrush = new SolidColorBrush(Color.Parse("#6FA8FF"));
    private static readonly IBrush WarningLineBrush = new SolidColorBrush(Color.Parse("#E8B341"));
    private static readonly IBrush ErrorLineBrush = new SolidColorBrush(Color.Parse("#F2777C"));
    private static readonly IBrush SuccessLineBrush = new SolidColorBrush(Color.Parse("#63D489"));
    private static readonly StringComparer FilePathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static readonly StringComparison FilePathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly List<GameEntry> _allGames = new();
    private readonly ObservableCollection<GameEntry> _visibleGames = new();
    private readonly AvaloniaList<LogLine> _consoleLines = new();
    private readonly List<LogLine> _allConsoleLines = new();
    private readonly ConcurrentQueue<(string Line, bool IsError)> _pendingLines = new();
    private readonly DispatcherTimer _consoleFlushTimer;
    private readonly DispatcherTimer _libraryBlurTimer;
    private BlurEffect? _libraryBlur;
    private double _libraryBlurStartRadius;
    private double _libraryBlurTargetRadius;
    private long _libraryBlurStartedAt;
    private bool _clearLibraryBlurWhenComplete;

    private GuiSettings _settings = new();
    private EmulatorProcess? _emulator;
    private GameSurfaceHost? _gameSurfaceHost;
    private ConsoleWindow? _consoleWindow;
    private GuiConsoleMirror? _consoleMirror;
    private StreamWriter? _fileLog;
    private readonly SndPreviewPlayer _sndPreview = new();
    private string? _emulatorExePath;
    private PendingLaunch? _pendingLaunch;
    private bool _gameFullscreen;
    private bool _isRunning;
    private bool _isStopping;
    private bool _awaitingFirstFrame;
    private int _autoScrollTicks;
    private int _activePageIndex;
    private Updater.UpdateInfo? _availableUpdate;
    private string _updateStatusKey = "Updater.Status.Ready";
    private object?[] _updateStatusArgs = [BuildInfo.CommitSha ?? "dev"];

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
    private HostGamepadButtons _previousPadButtons;
    private long _navLeftNextAt;
    private long _navRightNextAt;
    private long _navUpNextAt;
    private long _navDownNextAt;

    //Github http client for latest commit
    private static readonly HttpClient GithubHttpClient = CreateGithubHttpClient();
    private string? _latestCommitSha;

    private sealed record PendingLaunch(
        string EbootPath,
        string DisplayName,
        string? TitleId,
        string LogLevel,
        SharpEmuRuntimeOptions RuntimeOptions);

    public MainWindow()
    {
        InitializeComponent();

        GameList.ItemsSource = _visibleGames;
        ConsoleList.ItemsSource = _consoleLines;
        _consoleMirror = GuiConsoleMirror.Install((line, isError) =>
            _pendingLines.Enqueue((line, isError)));
        Closed += (_, _) => _emulator?.Stop();

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

        _libraryBlurTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _libraryBlurTimer.Tick += (_, _) => AdvanceLibraryBlur();

        Activated += (_, _) => UpdateSessionBarVisibility();
        Deactivated += (_, _) => SessionBarPopup.IsOpen = false;

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
        ClearLogButton.Click += (_, _) => { _consoleLines.Clear(); _allConsoleLines.Clear(); };
        StopButton.Click += (_, _) => StopEmulator();
        SessionStopButton.Click += (_, _) => StopEmulator();
        SessionConsoleButton.Click += (_, _) => ShowConsoleWindow();
        CopyLogButton.Click += async (_, _) => await CopyConsoleAsync();
        DetachConsoleButton.Click += (_, _) => ShowConsoleWindow();
        LibraryTabButton.Click += (_, _) => SetActivePage(0);
        OptionsTabButton.Click += (_, _) => SetActivePage(1);
        ConsoleToggle.IsCheckedChanged += (_, _) => ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;

        // The settings page edits _settings live, so a launch started while
        // it is open already uses the new values.
        LogLevelBox.SelectionChanged += (_, _) => _settings.LogLevel = SelectedLogLevel();
        TraceImportsBox.ValueChanged += (_, _) => _settings.ImportTraceLimit = (int)(TraceImportsBox.Value ?? 0);
        StrictToggle.IsCheckedChanged += (_, _) => _settings.StrictDynlibResolution = StrictToggle.IsChecked == true;
        LogToFileToggle.IsCheckedChanged += (_, _) => _settings.LogToFile = LogToFileToggle.IsChecked == true;
        OverrideLogFileToggle.IsCheckedChanged += (_, _) =>
            _settings.OverrideLogFile = OverrideLogFileToggle.IsChecked == true;
        TitleMusicToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.PlayTitleMusic = TitleMusicToggle.IsChecked == true;
            OnTitleMusicSettingChanged();
        };
        DiscordToggle.IsCheckedChanged += (_, _) =>
        {
            _settings.DiscordRichPresence = DiscordToggle.IsChecked == true;
            UpdateDiscordPresence();
        };
        AutoUpdateToggle.IsCheckedChanged += (_, _) =>
            _settings.CheckForUpdatesOnStartup = AutoUpdateToggle.IsChecked == true;
        UpdateButton.Click += async (_, _) => await OnUpdateButtonAsync();
        SelectLogFilePathButton.Click += async (_, _) => await SelectLogFilePathAsync();
        EnvBthidToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_BTHID_UNAVAILABLE", EnvBthidToggle.IsChecked == true);
        EnvLoopGuardToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", EnvLoopGuardToggle.IsChecked == true);
        EnvWritableApp0Toggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_WRITABLE_APP0", EnvWritableApp0Toggle.IsChecked == true);
        EnvVkValidationToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_VK_VALIDATION", EnvVkValidationToggle.IsChecked == true);
        EnvDumpSpirvToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_DUMP_SPIRV", EnvDumpSpirvToggle.IsChecked == true);
        EnvLogDirectMemoryToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_LOG_DIRECT_MEMORY", EnvLogDirectMemoryToggle.IsChecked == true);
        EnvLogIoToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_LOG_IO", EnvLogIoToggle.IsChecked == true);
        EnvLogNpToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_LOG_NP", EnvLogNpToggle.IsChecked == true);
        LanguageBox.SelectionChanged += (_, _) => OnLanguageChanged();

        GameList.AddHandler(ContextRequestedEvent, OnGameContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        CtxLaunch.Click += (_, _) => LaunchSelected();
        CtxOpenFolder.Click += (_, _) => OpenSelectedGameFolder();
        CtxCopyPath.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.Path, "Clipboard.Path");
        CtxCopyTitleId.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.TitleId, "Clipboard.TitleId");
        CtxGameSettings.Click += (_, _) => OpenSelectedGameSettings();
        CtxRemove.Click += (_, _) => RemoveSelectedFromLibrary();

        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => OnWindowClosing();

        WindowsDualSenseReader.EnsureStarted();
        WindowsXInputReader.EnsureStarted();
        _gamepadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _gamepadTimer.Tick += (_, _) => PollGamepad();
        _gamepadTimer.Start();


        GithubButton.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/sharpemu/sharpemu",
                UseShellExecute = true
            });
        };

        DiscordButton.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://discord.com/invite/6GejPEDqpc",
                UseShellExecute = true
            });
        };

        LatestCommitHashText.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_latestCommitSha))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName =
                    $"https://github.com/sharpemu/sharpemu/commit/{_latestCommitSha}",
                UseShellExecute = true
            });
        };
    }

    /// <summary>
    /// Switches between the Library and Options pages. Also reachable via
    /// the gamepad's shoulder buttons (LB/RB, L1/R1) from <see cref="PollGamepad"/>.
    /// </summary>
    private void SetActivePage(int index)
    {
        if (index == _activePageIndex)
        {
            return;
        }

        if (_activePageIndex == 1)
        {
            _settings.Save(); // leaving the Options page
        }

        _activePageIndex = index;
        SetActiveClass(LibraryTabButton, index == 0);
        SetActiveClass(OptionsTabButton, index == 1);
        LibraryPage.IsVisible = index == 0;
        LibraryToolbar.IsVisible = index == 0;
        OptionsPage.IsVisible = index == 1;
    }

    private static void SetActiveClass(Button button, bool active)
    {
        if (active)
        {
            if (!button.Classes.Contains("active"))
            {
                button.Classes.Add("active");
            }
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    // ---- Github http client config ----
    // This is for getting lash commit id
    private static HttpClient CreateGithubHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SharpEmu/1.0");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github.sha"));

        client.DefaultRequestHeaders.Add(
            "X-GitHub-Api-Version",
            "2026-03-10");

        return client;
    }
    private async Task LoadLatestCommitAsync()
    {
        const string apiUrl =
            "https://api.github.com/repos/sharpemu/sharpemu/commits/main";

        _latestCommitSha = null;
        LatestCommitHashText.Content = "Loading…";
        LatestCommitHashText.IsEnabled = false;

        try
        {
            using var response = await GithubHttpClient.GetAsync(apiUrl);
            var responseBody =
                (await response.Content.ReadAsStringAsync()).Trim();

            if (!response.IsSuccessStatusCode)
            {
                LatestCommitHashText.Content =
                    $"HTTP {(int)response.StatusCode}";

                ToolTip.SetTip(
                    LatestCommitHashText,
                    string.IsNullOrWhiteSpace(responseBody)
                        ? response.ReasonPhrase
                        : responseBody);

                return;
            }

            if (responseBody.Length < 7)
            {
                LatestCommitHashText.Content = "Invalid response";
                ToolTip.SetTip(LatestCommitHashText, responseBody);
                return;
            }

            // Keep the complete SHA for the URL.
            _latestCommitSha = responseBody;

            // Display only the short SHA.
            LatestCommitHashText.Content =
                responseBody[..Math.Min(7, responseBody.Length)];

            LatestCommitHashText.IsEnabled = true;

            ToolTip.SetTip(
                LatestCommitHashText,
                $"Open commit {_latestCommitSha}");
        }
        catch (TaskCanceledException ex)
        {
            LatestCommitHashText.Content = "Timeout";
            ToolTip.SetTip(LatestCommitHashText, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            LatestCommitHashText.Content = "Connection error";
            ToolTip.SetTip(LatestCommitHashText, ex.Message);
        }
        catch (Exception ex)
        {
            LatestCommitHashText.Content = "Error";
            ToolTip.SetTip(LatestCommitHashText, ex.Message);
        }
    }

    // ---- Controller navigation ----

    private void PollGamepad()
    {
        // DualSense wins when both are connected; XInput covers Xbox pads.
        if (!WindowsDualSenseReader.TryGetState(out var pad) && !WindowsXInputReader.TryGetState(out pad))
        {
            _previousPadButtons = HostGamepadButtons.None;
            return;
        }

        if (!IsActive)
        {
            // Ignore input while the launcher is in the background, e.g. the
            // game window is focused and using the same controller.
            _previousPadButtons = pad.Buttons;
            return;
        }

        var shoulderPressed = pad.Buttons & ~_previousPadButtons;
        if ((shoulderPressed & HostGamepadButtons.L1) != 0)
        {
            SetActivePage(0);
        }

        if ((shoulderPressed & HostGamepadButtons.R1) != 0)
        {
            SetActivePage(1);
        }

        if (_activePageIndex != 0)
        {
            _previousPadButtons = pad.Buttons;
            return;
        }

        var now = Environment.TickCount64;
        var left = (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;
        var right = (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;
        var up = (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;
        var down = (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;

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
        if ((pressed & HostGamepadButtons.Cross) != 0)
        {
            LaunchSelected();
        }

        if ((pressed & HostGamepadButtons.Circle) != 0)
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
        Localization.Instance.Load(_settings.Language);
        PopulateLanguageBox();
        ApplyLocalization();
        ApplySettingsToControls();
        LocateEmulator();
        UpdateDiscordPresence();
        _ = LoadLatestCommitAsync();

        if (_settings.CheckForUpdatesOnStartup)
        {
            _ = CheckForUpdatesAsync();
        }
        await RescanLibraryAsync();
    }

    private void PopulateLanguageBox()
    {
        var languages = Localization.Instance.DiscoverLanguages();
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.FirstOrDefault(language =>
            string.Equals(language.Code, _settings.Language, StringComparison.OrdinalIgnoreCase))
            ?? languages.FirstOrDefault();
    }

    private void OnLanguageChanged()
    {
        if (LanguageBox.SelectedItem is not Localization.LanguageInfo language)
        {
            return;
        }

        _settings.Language = language.Code;
        Localization.Instance.Load(language.Code);
        ApplyLocalization();
    }

    /// <summary>
    /// Re-applies every UI string from the current language, so switching
    /// languages in Options takes effect immediately without reopening the
    /// window.
    /// </summary>
    private void ApplyLocalization()
    {
        var loc = Localization.Instance;

        LibraryTabButton.Content = loc.Get("Page.Library");
        OptionsTabButton.Content = loc.Get("Page.Options");

        SearchBox.Watermark = loc.Get("Library.SearchWatermark");
        AddFolderButton.Content = loc.Get("Library.AddFolder");
        RescanButton.Content = loc.Get("Library.Rescan");
        OpenFileButton.Content = loc.Get("Library.OpenFile");

        CtxLaunch.Header = loc.Get("Library.Context.Launch");
        CtxOpenFolder.Header = loc.Get("Library.Context.OpenFolder");
        CtxCopyPath.Header = loc.Get("Library.Context.CopyPath");
        CtxCopyTitleId.Header = loc.Get("Library.Context.CopyTitleId");
        CtxGameSettings.Header = loc.Get("Library.Context.GameSettings");
        CtxRemove.Header = loc.Get("Library.Context.Remove");

        EmptyAddFolderButton.Content = loc.Get("Library.Empty.AddFolder");
        LoadingStateText.Text = loc.Get("Library.Loading");

        GeneralTabItem.Header = loc.Get("Options.General");
        EnvTabItem.Header = loc.Get("Options.Env.Tab");
        EnvSectionTitle.Text = loc.Get("Options.Section.Environment");
        EnvDesc.Text = loc.Get("Options.Env.Desc");
        EnvBthidRow.Description = loc.Get("Options.Env.Bthid.Desc");
        EnvLoopGuardRow.Description = loc.Get("Options.Env.LoopGuard.Desc");
        EnvWritableApp0Row.Description = loc.Get("Options.Env.WritableApp0.Desc");
        EnvVkValidationRow.Description = loc.Get("Options.Env.VkValidation.Desc");
        EnvDumpSpirvRow.Description = loc.Get("Options.Env.DumpSpirv.Desc");
        EnvLogDirectMemoryRow.Description = loc.Get("Options.Env.LogDirectMemory.Desc");
        EnvLogIoRow.Description = loc.Get("Options.Env.LogIo.Desc");
        EnvLogNpRow.Description = loc.Get("Options.Env.LogNp.Desc");
        EmulationSectionTitle.Text = loc.Get("Options.Section.Emulation");
        LoggingSectionTitle.Text = loc.Get("Options.Section.Logging");
        LauncherSectionTitle.Text = loc.Get("Options.Section.Launcher");

        CpuEngineRow.Label = loc.Get("Options.CpuEngine.Label");
        CpuEngineRow.Description = loc.Get("Options.CpuEngine.Desc");
        CpuEngineNativeItem.Content = loc.Get("Options.CpuEngine.Native");

        StrictRow.Label = loc.Get("Options.Strict.Label");
        StrictRow.Description = loc.Get("Options.Strict.Desc");

        LogLevelRow.Label = loc.Get("Options.LogLevel.Label");
        LogLevelRow.Description = loc.Get("Options.LogLevel.Desc");
        LogLevelTraceItem.Content = loc.Get("Options.LogLevel.Trace");
        LogLevelDebugItem.Content = loc.Get("Options.LogLevel.Debug");
        LogLevelInfoItem.Content = loc.Get("Options.LogLevel.Info");
        LogLevelWarningItem.Content = loc.Get("Options.LogLevel.Warning");
        LogLevelErrorItem.Content = loc.Get("Options.LogLevel.Error");
        LogLevelCriticalItem.Content = loc.Get("Options.LogLevel.Critical");

        TraceImportsRow.Label = loc.Get("Options.TraceImports.Label");
        TraceImportsRow.Description = loc.Get("Options.TraceImports.Desc");

        LogToFileRow.Label = loc.Get("Options.LogToFile.Label");
        LogToFileRow.Description = loc.Get("Options.LogToFile.Desc");

        LogFilePathRow.Label = loc.Get("Options.LogFilePath.Label");
        SelectLogFilePathButton.Content = loc.Get("Options.LogFilePath.Select");
        UpdateLogFilePathText();

        OverrideLogFileRow.Label = loc.Get("Options.OverrideLogFile.Label");
        OverrideLogFileRow.Description = loc.Get("Options.OverrideLogFile.Desc");

        LanguageRow.Label = loc.Get("Options.Language.Label");
        LanguageRow.Description = loc.Get("Options.Language.Desc");

        TitleMusicRow.Label = loc.Get("Options.TitleMusic.Label");
        TitleMusicRow.Description = loc.Get("Options.TitleMusic.Desc");

        DiscordRow.Label = loc.Get("Options.Discord.Label");
        DiscordRow.Description = loc.Get("Options.Discord.Desc");
        AutoUpdateRow.Label = loc.Get("Updater.Auto.Label");
        AutoUpdateRow.Description = loc.Get("Updater.Auto.Desc");

        foreach (var toggle in new[] { StrictToggle, LogToFileToggle, OverrideLogFileToggle, TitleMusicToggle, DiscordToggle, AutoUpdateToggle })
        {
            toggle.OnContent = loc.Get("Common.On");
            toggle.OffContent = loc.Get("Common.Off");
        }

        ConsoleSectionTitle.Text = loc.Get("Console.Title");
        ConsoleSearchBox.Watermark = loc.Get("Console.SearchWatermark");
        AutoScrollCheck.Content = loc.Get("Console.AutoScroll");
        DetachConsoleButton.Content = loc.Get("Console.Split");
        CopyLogButton.Content = loc.Get("Console.Copy");
        ClearLogButton.Content = loc.Get("Console.Clear");

        ConsoleToggle.Content = loc.Get("Launch.Console");
        LaunchButton.Content = loc.Get("Launch.Launch");
        StopButton.Content = loc.Get("Launch.Stop");

        AboutSectionTitle.Text = loc.Get("Options.About");
        GithubLabel.Text = loc.Get("About.Github.Label");
        GithubDesc.Text = loc.Get("About.Github.Desc");
        DiscordServerLabel.Text = loc.Get("About.Discord.Label");
        DiscordServerDesc.Text = loc.Get("About.Discord.Desc");
        GithubButton.Content = loc.Get("About.GithubButton");
        DiscordButton.Content = loc.Get("About.DiscordButton");
        UpdateLabel.Text = loc.Get("Updater.Label");
        LatestCommitLabel.Text = loc.Get("About.Github.LatestCommitLabel");
        LatestCommitDescription.Text = loc.Get("About.Github.LatestCommitDescription");
        RefreshUpdateText();

        UpdateEmptyStateTexts();
        UpdateSelectedGameTexts();
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
                Localization.Instance.Format("Discord.Playing", gameName),
                _runningGameTitleId,
                _runningSinceUnixSeconds);
        }
        else
        {
            // Discord does not render activities without timestamps, so the
            // browsing state carries the launcher's start time.
            var count = _allGames.Count == 1
                ? Localization.Instance.Get("Page.GameCount.One")
                : Localization.Instance.Format("Page.GameCount.Other", _allGames.Count);
            _discord.SetPresence(
                Localization.Instance.Get("Discord.Browsing"),
                count,
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

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        // While a session is on screen, Enter and Space are game input
        // (Cross button). Keyboard focus stays on the launcher window, so a
        // previously clicked, still-focused button (console toggle, session
        // bar) would also activate and reshape the game view. Swallow the
        // keys before button activation; the emulator process reads raw key
        // state and is unaffected. Fullscreen hides those buttons, which is
        // why this only manifested in windowed sessions.
        if (_isRunning && GameView.IsVisible &&
            args.Key is Key.Enter or Key.Space)
        {
            args.Handled = true;
        }
    }

    private void OnWindowFullScreen(object sender, RoutedEventArgs args)
    {
        if (WindowState == WindowState.FullScreen)
        {
            // Leaving F11 should restore a monitor-sized window with the
            // launcher chrome, not fall back to the design-time window size.
            WindowState = WindowState.Maximized;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.PreferSystemChrome;
            TitleBar.IsVisible = true;
            StatusBar.IsVisible = true;
            if (_gameFullscreen)
            {
                _gameFullscreen = false;
                Grid.SetRow(MainContent, 1);
                Grid.SetRowSpan(MainContent, 1);
                MainContent.Margin = _isRunning
                    ? new Thickness(0)
                    : new Thickness(32, 24, 32, 20);
                ContentToolbar.IsVisible = !_isRunning;
                ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;
                LaunchBar.IsVisible = true;
                QueueGameSurfaceResize();
                UpdateSessionBarVisibility();
            }
        }
        else
        {
            WindowState = WindowState.FullScreen;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            TitleBar.IsVisible = false;
            StatusBar.IsVisible = false;
            if (_isRunning && !_isStopping && !_awaitingFirstFrame && GameView.IsVisible)
            {
                // The native child receives its new physical Bounds as soon
                // as this grid spans the monitor. The presenter recreates its
                // swapchain from that size, rather than stretching 720p.
                _gameFullscreen = true;
                // Re-arming restarts the idle countdown, so the cursor also
                // hides a moment after F11 even without further mouse motion.
                _gameSurfaceHost?.SetCursorAutoHide(true);
                Grid.SetRow(MainContent, 0);
                Grid.SetRowSpan(MainContent, 3);
                MainContent.Margin = new Thickness(0);
                ContentToolbar.IsVisible = false;
                ConsolePanel.IsVisible = false;
                LaunchBar.IsVisible = false;
                QueueGameSurfaceResize();
                UpdateSessionBarVisibility();
            }
        }
    }

    private void QueueGameSurfaceResize()
    {
        Dispatcher.UIThread.Post(
            () => _gameSurfaceHost?.RefreshSurfaceSize(),
            DispatcherPriority.Render);
    }

    private void OnWindowClosing()
    {
        _settings.Save();
        _consoleFlushTimer.Stop();
        _libraryBlurTimer.Stop();
        _gamepadTimer.Stop();
        _sndPreview.Stop();
        _discord?.Dispose();
        _consoleWindow?.Close();
        _emulator?.Dispose();
        _consoleMirror?.Dispose();
        DropFileLog();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    // ---- Settings ----

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
        DiscordToggle.IsChecked = _settings.DiscordRichPresence;
        AutoUpdateToggle.IsChecked = _settings.CheckForUpdatesOnStartup;
        EnvBthidToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_BTHID_UNAVAILABLE");
        EnvLoopGuardToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD");
        EnvWritableApp0Toggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_WRITABLE_APP0");
        EnvVkValidationToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_VK_VALIDATION");
        EnvDumpSpirvToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_DUMP_SPIRV");
        EnvLogDirectMemoryToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_LOG_DIRECT_MEMORY");
        EnvLogIoToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_LOG_IO");
        EnvLogNpToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_LOG_NP");
        UpdateLogFilePathText();
    }

    private async Task OnUpdateButtonAsync()
    {
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync();
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(value =>
                SetUpdateStatus("Updater.Status.Downloading", value));
            await Updater.DownloadAndRestartAsync(_availableUpdate, progress);
            SetUpdateStatus("Updater.Status.Installing");
            Close();
        }
        catch
        {
            SetUpdateStatus("Updater.Status.Failed");
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        _availableUpdate = null;
        UpdateButton.IsEnabled = false;
        SetUpdateStatus("Updater.Status.Checking");
        try
        {
            _availableUpdate = await Updater.CheckAsync(BuildInfo.CommitSha);
            SetUpdateStatus(
                _availableUpdate is null ? "Updater.Status.Current" : "Updater.Status.Available",
                _availableUpdate?.Sha ?? BuildInfo.CommitSha ?? "dev");
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("Updater.Status.Timeout");
        }
        catch (PlatformNotSupportedException)
        {
            SetUpdateStatus("Updater.Status.Unsupported");
        }
        catch
        {
            SetUpdateStatus("Updater.Status.Failed");
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            RefreshUpdateText();
        }
    }

    private void SetUpdateStatus(string key, params object?[] args)
    {
        _updateStatusKey = key;
        _updateStatusArgs = args;
        RefreshUpdateText();
    }

    private void RefreshUpdateText()
    {
        UpdateStatusText.Text = Localization.Instance.Format(_updateStatusKey, _updateStatusArgs);
        UpdateButton.Content = Localization.Instance.Get(
            _availableUpdate is null ? "Updater.Check" : "Updater.DownloadRestart");
    }

    // Environment variables set on this process at the previous launch; children
    // inherit the process environment, so stale names must be cleared explicitly.
    private readonly HashSet<string> _appliedEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase);

    private void SetEnvironmentToggle(string name, bool enabled)
    {
        if (enabled)
        {
            if (!_settings.EnvironmentToggles.Contains(name))
            {
                _settings.EnvironmentToggles.Add(name);
            }
        }
        else
        {
            _settings.EnvironmentToggles.Remove(name);
        }
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

    private void UpdateLogFilePathText()
    {
        LogFilePathRow.Description = string.IsNullOrWhiteSpace(_settings.LogFilePath)
            ? Localization.Instance.Get("Options.LogFilePath.Default")
            : _settings.LogFilePath;
    }

    private async Task SelectLogFilePathAsync()
    {
        var loc = Localization.Instance;
        SaveFilePickerResult result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = loc.Get("Dialog.SaveLogFile"),
            SuggestedFileName = "SharpEmuLog",
            DefaultExtension = "log",
            FileTypeChoices =
                [
                    new FilePickerFileType(loc.Get("Dialog.PlainTextFiles")) { Patterns = ["*.txt"] },
                    new FilePickerFileType(loc.Get("Dialog.LogFiles")) { Patterns = ["*.log"] }
                ]
        });

        if (result.File is not null)
        {
            _settings.LogFilePath = result.File.Path.LocalPath;
            UpdateLogFilePathText();
        }
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

        // The GUI and CLI share one executable. The selected path is the
        // isolated child executable and also defines the portable data root.
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
            ? Localization.Instance.Format("Status.EmulatorPath", _emulatorExePath)
            : Localization.Instance.Get("Status.EmulatorNotFound");
    }

    // ---- Game library ----

    private async Task AddFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localization.Instance.Get("Dialog.ChooseGameFolder"),
            AllowMultiple = false,
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var changed = false;
        if (!_settings.GameFolders.Contains(path, FilePathComparer))
        {
            _settings.GameFolders.Add(path);
            changed = true;
        }

        // Adding (or re-adding) a folder is an explicit signal to restore any
        // games beneath it that were removed from the library earlier.
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        changed |= _settings.ExcludedGames.RemoveAll(excluded =>
            excluded.StartsWith(prefix, FilePathComparison)) > 0;

        if (changed)
        {
            _settings.Save();
        }

        await RescanLibraryAsync();
    }

    private async Task RescanLibraryAsync()
    {
        var folders = _settings.GameFolders.ToArray();
        var excluded = new HashSet<string>(_settings.ExcludedGames, FilePathComparer);
        StatusBarRight.Text = Localization.Instance.Get("Status.ScanningLibrary");
        EmptyState.IsVisible = false;
        LoadingState.IsVisible = true;

        var games = await Task.Run(() => ScanFolders(folders, excluded));

        _allGames.Clear();
        _allGames.AddRange(games);
        RefreshVisibleGames();
        LoadingState.IsVisible = false;
        LoadGameDetailsInBackground(games);
        UpdateDiscordPresence();
        StatusBarRight.Text = folders.Length == 0
            ? Localization.Instance.Get("Status.AddFolderPrompt")
            : Localization.Instance.Format("Status.LibraryScanned", games.Count, folders.Length);
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
        var seen = new HashSet<string>(FilePathComparer);
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

                    var (title, titleId, version) = TryReadParamJson(fullPath);
                    games.Add(new GameEntry(
                        title ?? GameNameFor(fullPath), titleId, version, fullPath, size,
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
    /// Reads the game title, title id and content version from
    /// sce_sys/param.json next to the executable, when present.
    /// </summary>
    private static (string? Title, string? TitleId, string? Version) TryReadParamJson(string ebootPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(ebootPath);
            if (directory is null)
            {
                return (null, null, null);
            }

            var paramPath = Path.Combine(directory, "sce_sys", "param.json");
            if (!File.Exists(paramPath))
            {
                return (null, null, null);
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

            // contentVersion carries the installed app version
            // ("01.000.000"); masterVersion is the fallback on older dumps.
            string? version = null;
            if (root.TryGetProperty("contentVersion", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                version = versionElement.GetString();
            }
            else if (root.TryGetProperty("masterVersion", out var masterElement) &&
                     masterElement.ValueKind == JsonValueKind.String)
            {
                version = masterElement.GetString();
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
                string.IsNullOrWhiteSpace(titleId) ? null : titleId,
                string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        }
        catch (Exception)
        {
            return (null, null, null);
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
        CtxGameSettings.IsEnabled = !string.IsNullOrWhiteSpace(game.TitleId);
    }

    private void OpenSelectedGameSettings()
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(game.TitleId))
        {
            AppendConsoleLine(
                "[GUI][WARN] Per-game settings require a title ID, which this game does not have.",
                WarningLineBrush);
            return;
        }

        _ = new PerGameSettingsDialog(game.TitleId, game.Name, _settings).ShowDialog(this);
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
            StatusBarRight.Text = Localization.Instance.Format("Status.CouldNotOpenFolder", ex.Message);
        }
    }

    /// <summary>Copies <paramref name="text"/> and reports it via <paramref name="whatKey"/>, e.g. "Clipboard.Path".</summary>
    private async Task CopyToClipboardAsync(string? text, string whatKey)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(text);
        StatusBarRight.Text = Localization.Instance.Format("Status.CopiedToClipboard", Localization.Instance.Get(whatKey));
    }

    private void RemoveSelectedFromLibrary()
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        if (!_settings.ExcludedGames.Contains(game.Path, FilePathComparer))
        {
            _settings.ExcludedGames.Add(game.Path);
            _settings.Save();
        }

        _allGames.RemoveAll(g => string.Equals(g.Path, game.Path, FilePathComparison));
        GameList.SelectedItem = null;
        RefreshVisibleGames();
        StatusBarRight.Text = Localization.Instance.Format("Status.RemovedFromLibrary", game.Name);
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

        if (selectedPath is not null &&
            _visibleGames.FirstOrDefault(g => g.Path.Equals(selectedPath, FilePathComparison))
                is { } reselected)
        {
            GameList.SelectedItem = reselected;
        }

        EmptyState.IsVisible = _visibleGames.Count == 0;
        UpdateEmptyStateTexts();

        UpdateSelectedGame();
    }

    /// <summary>
    /// Refreshes the empty-state title/hint from the current language and
    /// search text; a no-op while the empty state is not showing.
    /// </summary>
    private void UpdateEmptyStateTexts()
    {
        if (_visibleGames.Count != 0)
        {
            return;
        }

        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var hasFilter = query.Length > 0;
        EmptyStateTitle.Text = hasFilter
            ? Localization.Instance.Get("Library.Empty.SearchTitle")
            : Localization.Instance.Get("Library.Empty.Title");
        EmptyStateHint.Text = hasFilter
            ? Localization.Instance.Format("Library.Empty.SearchHint", query)
            : Localization.Instance.Get("Library.Empty.Hint");
        EmptyAddFolderButton.IsVisible = !hasFilter;
    }

    private void UpdateSelectedGame()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            UpdateSelectedGameTexts();
            SelectedCoverPanel.DataContext = game;
            SelectedBadgesRow.DataContext = game;
            SelectedBadgesRow.IsVisible = true;
            _ = UpdateBackdropAsync(game);
            PlaySelectedGamePreview(game);
        }
        else
        {
            UpdateSelectedGameTexts();
            SelectedCoverPanel.DataContext = null;
            SelectedBadgesRow.DataContext = null;
            SelectedBadgesRow.IsVisible = false;
            _ = UpdateBackdropAsync(null);
            _sndPreview.Stop();
        }

        UpdateRunButtons();
    }

    /// <summary>
    /// Text-only refresh of the launch bar's title/path, split out of
    /// <see cref="UpdateSelectedGame"/> so a language change can re-apply it
    /// without restarting the backdrop fade or preview music.
    /// </summary>
    private void UpdateSelectedGameTexts()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            SelectedGameTitle.Text = game.Name;
            SelectedGamePath.Text = game.Path;
        }
        else
        {
            SelectedGameTitle.Text = Localization.Instance.Get("Launch.NoGameSelected");
            SelectedGamePath.Text = Localization.Instance.Get("Launch.NoGameHint");
        }
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

    private void OnTitleMusicSettingChanged()
    {
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
            Title = Localization.Instance.Get("Dialog.OpenExecutable"),
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Localization.Instance.Get("Dialog.PsExecutables"))
                    { Patterns = new[] { "eboot.bin", "*.bin", "*.self", "*.elf" } },
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

        var resolvedTitleId = string.IsNullOrWhiteSpace(titleId)
            ? _allGames.FirstOrDefault(game => game.Path.Equals(ebootPath, FilePathComparison))?.TitleId
            : titleId;
        var effective = EffectiveLaunchSettings.Resolve(_settings, PerGameSettings.Load(resolvedTitleId));

        _sndPreview.Stop();
        _consoleLines.Clear();
        _allConsoleLines.Clear();

        DropFileLog();
        if (effective.LogToFile)
        {
            OpenFileLog(resolvedTitleId);
        }

        // The isolated game child inherits these diagnostics. Keep them on the
        // launcher process so every platform receives the same launch options.
        foreach (var staleName in _appliedEnvironmentVariables)
        {
            if (!effective.EnvironmentToggles.Contains(staleName))
            {
                Environment.SetEnvironmentVariable(staleName, null);
            }
        }

        _appliedEnvironmentVariables.Clear();
        foreach (var name in effective.EnvironmentToggles)
        {
            Environment.SetEnvironmentVariable(name, "1");
            _appliedEnvironmentVariables.Add(name);
        }

        if (SharpEmuLog.TryParseLevel(effective.LogLevel, out var logLevel))
        {
            SharpEmuLog.MinimumLevel = logLevel;
        }

        var runtimeOptions = new SharpEmuRuntimeOptions
        {
            CpuEngine = CpuExecutionEngine.NativeOnly,
            StrictDynlibResolution = effective.StrictDynlibResolution,
            ImportTraceLimit = Math.Max(0, effective.ImportTraceLimit),
        };

        _isRunning = true;
        _runningGameName = displayName;
        SessionGameTitle.Text = displayName;
        _runningGameTitleId = resolvedTitleId;
        _runningSinceUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        StatusDot.Fill = SuccessLineBrush;
        StatusText.Text = Localization.Instance.Format("Launch.Running", displayName);
        StatusBarRight.Text = Localization.Instance.Format("Status.Running", displayName);
        UpdateRunButtons();
        UpdateDiscordPresence();

        ShowGameView();
        _pendingLaunch = new PendingLaunch(
            Path.GetFullPath(ebootPath),
            displayName,
            _runningGameTitleId,
            effective.LogLevel,
            runtimeOptions);

        if (_gameSurfaceHost?.Surface is { } surface)
        {
            StartPendingSession(surface);
        }
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
        if (!_isRunning || _isStopping)
        {
            return;
        }

        if (_emulator is null)
        {
            // The native host can be created a moment after Launch. Do not
            // let that delayed callback start a session the user already
            // cancelled.
            _pendingLaunch = null;
            OnEmulatorExited(0);
            return;
        }

        _isStopping = true;
        StopButton.IsEnabled = false;
        SessionStopButton.IsEnabled = false;
        SessionHintText.Text = Localization.Instance.Get("Launch.Stopping");
        SessionF11Badge.IsVisible = false;
        ShowSessionLoading("Closing game", "Waiting for the emulation session to exit...");
        _emulator.Stop();
        _runningGameName = null;
        _runningGameTitleId = null;
        StatusText.Text = Localization.Instance.Get("Launch.Stopping");
        StatusBarRight.Text = Localization.Instance.Get("Status.Stopping");
        UpdateDiscordPresence();
        UpdateSessionBarVisibility();
        ReturnToLibraryWhileStopping();
    }

    /// <summary>
    /// Builds "user/logs/&lt;titleId&gt;-&lt;timestamp&gt;.log" next to the emulator
    /// executable, following the same portable-data convention as savedata.
    /// </summary>
    private string? BuildLogFilePath(string? titleId)
    {
        try
        {
            var exeDirectory = Path.GetDirectoryName(_emulatorExePath) ?? AppContext.BaseDirectory;
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
        _isStopping = false;
        _emulator?.Dispose();
        _emulator = null;
        _pendingLaunch = null;
        DisposeGameSurfaceHost();
        HideGameView();

        var meaningKey = exitCode switch
        {
            0 => "Exit.Ok",
            1 => "Exit.InvalidArguments",
            2 => "Exit.EbootNotFound",
            3 => "Exit.RuntimeException",
            4 => "Exit.EmulationError",
            -1073741819 => "Exit.EmulationError",
            _ => "Exit.Unknown",
        };
        var stoppedByUser = exitCode == EmulatorProcess.HostStopExitCode;
        var meaning = Localization.Instance.Get(meaningKey);
        var brush = exitCode == 0 || stoppedByUser ? SuccessLineBrush : ErrorLineBrush;
        AppendConsoleLine(
            stoppedByUser
                ? "Game closed by the user."
                : Localization.Instance.Format("Launch.ProcessExited", exitCode, meaning),
            brush);
        CloseFileLogSoon();

        StatusDot.Fill = exitCode == 0 || stoppedByUser ? (IBrush)SuccessLineBrush : ErrorLineBrush;
        StatusText.Text = stoppedByUser
            ? "Game closed by the user."
            : Localization.Instance.Format("Launch.Exited", exitCode, meaning);
        StatusBarRight.Text = Localization.Instance.Get("Status.Idle");
        _runningGameName = null;
        _runningGameTitleId = null;
        UpdateRunButtons();
        UpdateDiscordPresence();
    }

    private void StartPendingSession(VulkanHostSurface surface)
    {
        if (_pendingLaunch is not { } launch || _emulator is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_emulatorExePath))
        {
            AppendConsoleLine(Localization.Instance.Get("Launch.ExeNotFound"), ErrorLineBrush);
            OnEmulatorExited(3);
            return;
        }

        var process = new EmulatorProcess();
        process.OutputReceived += OnEmulatorOutput;
        process.Exited += code => Dispatcher.UIThread.Post(() => OnEmulatorExited(code));

        try
        {
            var arguments = BuildEmulatorArguments(launch, surface);
            _emulator = process;
            _pendingLaunch = null;
            process.Start(
                _emulatorExePath,
                arguments,
                Path.GetDirectoryName(_emulatorExePath));
            AppendConsoleLine(
                Localization.Instance.Format("Launch.Command", launch.EbootPath),
                DimLineBrush);
        }
        catch (Exception exception)
        {
            _emulator = null;
            process.Dispose();
            AppendConsoleLine(
                Localization.Instance.Format("Launch.StartFailed", exception.Message),
                ErrorLineBrush);
            OnEmulatorExited(3);
        }
    }

    private List<string> BuildEmulatorArguments(PendingLaunch launch, VulkanHostSurface surface)
    {
        var arguments = new List<string>
        {
            "--cpu-engine=native",
            $"--log-level={launch.LogLevel}",
        };
        if (launch.RuntimeOptions.StrictDynlibResolution)
        {
            arguments.Add("--strict");
        }
        if (launch.RuntimeOptions.ImportTraceLimit > 0)
        {
            arguments.Add($"--trace-imports={launch.RuntimeOptions.ImportTraceLimit}");
        }

        if (surface.TryGetChildProcessDescriptor(out var descriptor))
        {
            arguments.Add($"--host-surface={descriptor}");
        }
        else
        {
            AppendConsoleLine(
                "[GUI][WARN] Embedded child surfaces are unavailable on this platform; opening a game window instead.",
                WarningLineBrush);
        }

        arguments.Add(launch.EbootPath);
        return arguments;
    }

    private void OnEmulatorOutput(string line, bool isError)
    {
        _pendingLines.Enqueue((line, isError));
        if (!line.Contains("[VIDEOOUT][INFO] Hosted splash ready.", StringComparison.Ordinal) &&
            !line.Contains("[VIDEOOUT][INFO] Hosted first frame presented.", StringComparison.Ordinal))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_isRunning && !_isStopping)
            {
                _awaitingFirstFrame = false;
                ClearLibraryBlur();
                MainContent.Margin = new Thickness(0);
                GameView.Background = Brushes.Black;
                GameView.IsHitTestVisible = true;
                _gameSurfaceHost?.SetPresentationVisible(true);
                _gameSurfaceHost?.SetCursorAutoHide(true);
                LibraryPage.IsVisible = false;
                OptionsPage.IsVisible = false;
                LibraryToolbar.IsVisible = false;
                ContentToolbar.IsVisible = false;
                ConsolePanel.IsVisible = false;
                LaunchBar.IsVisible = false;
                SessionLoadingPopup.IsOpen = false;
                UpdateSessionBarVisibility();
            }
        });
    }

    private GameSurfaceHost EnsureGameSurfaceHost()
    {
        if (_gameSurfaceHost is not null)
        {
            return _gameSurfaceHost;
        }

        var host = new GameSurfaceHost();
        // Configure this before attaching it to Avalonia so its first native
        // HWND is hidden while the child process starts.
        host.SetPresentationVisible(false);
        host.SurfaceAvailable += (_, surface) =>
        {
            if (ReferenceEquals(_gameSurfaceHost, host))
            {
                StartPendingSession(surface);
            }
        };
        host.SurfaceDestroyed += (_, surface) => OnGameSurfaceDestroyed(host, surface);
        _gameSurfaceHost = host;
        GameSurfaceContainer.Children.Add(host);
        return host;
    }

    private void DisposeGameSurfaceHost()
    {
        var host = _gameSurfaceHost;
        if (host is null)
        {
            return;
        }

        _gameSurfaceHost = null;
        host.SetPresentationVisible(false);
        GameSurfaceContainer.Children.Remove(host);
    }

    private void OnGameSurfaceDestroyed(GameSurfaceHost host, VulkanHostSurface surface)
    {
        if (ReferenceEquals(_gameSurfaceHost, host) && _isRunning)
        {
            StopEmulator();
        }
    }

    private void ShowGameView()
    {
        _isStopping = false;
        _awaitingFirstFrame = true;
        var host = EnsureGameSurfaceHost();
        GameView.IsVisible = true;
        GameView.Background = Brushes.Transparent;
        GameView.IsHitTestVisible = false;
        host.SetPresentationVisible(false);
        AnimateLibraryBlur(LaunchBlurRadius);
        SessionHintText.Text = "Fullscreen";
        SessionF11Badge.IsVisible = true;
        UpdateSessionBarVisibility();
        ShowSessionLoading("Loading game", "Preparing the emulation session...");
    }

    private void HideGameView()
    {
        if (_gameFullscreen && WindowState == WindowState.FullScreen)
        {
            OnWindowFullScreen(this, new RoutedEventArgs());
        }

        _gameSurfaceHost?.SetCursorAutoHide(false);
        _gameSurfaceHost?.SetPresentationVisible(false);
        _awaitingFirstFrame = false;
        GameView.IsVisible = false;
        GameView.IsHitTestVisible = true;
        SessionBarPopup.IsOpen = false;
        SessionLoadingPopup.IsOpen = false;
        AnimateLibraryBlur(0, clearWhenComplete: true);
        MainContent.Margin = new Thickness(32, 24, 32, 20);
        ContentToolbar.IsVisible = true;
        ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;
        LaunchBar.IsVisible = true;
        LibraryPage.IsVisible = _activePageIndex == 0;
        LibraryToolbar.IsVisible = _activePageIndex == 0;
        OptionsPage.IsVisible = _activePageIndex == 1;
        if (GameList.SelectedItem is GameEntry game && game.Background is not null)
        {
            BackdropImage.Opacity = 1;
        }
    }

    private void AnimateLibraryBlur(double targetRadius, bool clearWhenComplete = false)
    {
        _libraryBlur ??= new BlurEffect();
        MainContent.Effect = _libraryBlur;

        _libraryBlurStartRadius = _libraryBlur.Radius;
        _libraryBlurTargetRadius = Math.Max(0, targetRadius);
        _libraryBlurStartedAt = Stopwatch.GetTimestamp();
        _clearLibraryBlurWhenComplete = clearWhenComplete && _libraryBlurTargetRadius == 0;

        if (Math.Abs(_libraryBlurStartRadius - _libraryBlurTargetRadius) < 0.01)
        {
            CompleteLibraryBlur();
            return;
        }

        _libraryBlurTimer.Start();
    }

    private void AdvanceLibraryBlur()
    {
        if (_libraryBlur is null)
        {
            _libraryBlurTimer.Stop();
            return;
        }

        var elapsed = (Stopwatch.GetTimestamp() - _libraryBlurStartedAt) /
                      (double)Stopwatch.Frequency;
        var progress = Math.Clamp(elapsed / BlurTransitionSeconds, 0, 1);
        // Cubic ease-out gives the loading transition a quick response while
        // keeping the final change of sharpness unobtrusive.
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        _libraryBlur.Radius = _libraryBlurStartRadius +
                              ((_libraryBlurTargetRadius - _libraryBlurStartRadius) * easedProgress);

        if (progress >= 1)
        {
            CompleteLibraryBlur();
        }
    }

    private void CompleteLibraryBlur()
    {
        _libraryBlurTimer.Stop();
        if (_libraryBlur is not null)
        {
            _libraryBlur.Radius = _libraryBlurTargetRadius;
        }

        if (_clearLibraryBlurWhenComplete)
        {
            MainContent.Effect = null;
            _libraryBlur = null;
            _clearLibraryBlurWhenComplete = false;
        }
    }

    private void ClearLibraryBlur()
    {
        _libraryBlurTimer.Stop();
        _libraryBlur = null;
        _clearLibraryBlurWhenComplete = false;
        MainContent.Effect = null;
    }

    private void ShowSessionLoading(string title, string detail)
    {
        SessionLoadingTitle.Text = title;
        SessionLoadingDetail.Text = detail;
        SessionLoadingPopup.IsOpen = true;
    }

    private void ReturnToLibraryWhileStopping()
    {
        if (_gameFullscreen && WindowState == WindowState.FullScreen)
        {
            OnWindowFullScreen(this, new RoutedEventArgs());
        }

        // Keep the native child alive until the session exits, but hide it
        // immediately. Destroying it while Vulkan still owns the surface can
        // crash the GUI; leaving it transparent lets the library recover
        // while the native closing popup reports teardown progress.
        _gameSurfaceHost?.SetPresentationVisible(false);
        _awaitingFirstFrame = false;
        GameView.Background = Brushes.Transparent;
        GameView.IsHitTestVisible = false;
        SessionBarPopup.IsOpen = false;
        AnimateLibraryBlur(LaunchBlurRadius);
        MainContent.Margin = new Thickness(32, 24, 32, 20);
        ContentToolbar.IsVisible = true;
        ConsolePanel.IsVisible = ConsoleToggle.IsChecked == true && _consoleWindow is null;
        LaunchBar.IsVisible = true;
        LibraryPage.IsVisible = _activePageIndex == 0;
        LibraryToolbar.IsVisible = _activePageIndex == 0;
        OptionsPage.IsVisible = _activePageIndex == 1;
        BackdropImage.Opacity = GameList.SelectedItem is GameEntry { Background: not null } ? 1 : 0;
        UpdateRunButtons();
        Console.Error.WriteLine("[GUI][INFO] Library restored while embedded session is closing.");
    }

    private void OpenFileLog(string? titleId)
    {
        var filePath = ResolveLogFilePath(titleId);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileLog = new StreamWriter(filePath, append: false) { AutoFlush = true };
            AppendConsoleLine(Localization.Instance.Format("Launch.LogFile", filePath), DimLineBrush);
        }
        catch (Exception exception)
        {
            AppendConsoleLine($"[GUI][WARN] Could not open log file: {exception.Message}", WarningLineBrush);
            DropFileLog();
        }
    }

    private string? ResolveLogFilePath(string? titleId)
    {
        if (string.IsNullOrWhiteSpace(_settings.LogFilePath))
        {
            return BuildLogFilePath(titleId);
        }

        if (_settings.OverrideLogFile)
        {
            return _settings.LogFilePath;
        }

        var path = _settings.LogFilePath;
        var id = string.IsNullOrWhiteSpace(titleId) ? "UNKNOWN" : titleId;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            id = id.Replace(invalid.ToString(), string.Empty, StringComparison.Ordinal);
        }

        var directory = Path.GetDirectoryName(path);
        var filename = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var timestampedName = $"{filename}-{id}-{DateTime.Now:yyyyMMdd-HHmmss}{extension}";
        return string.IsNullOrEmpty(directory) ? timestampedName : Path.Combine(directory, timestampedName);
    }

    private void UpdateRunButtons()
    {
        LaunchButton.IsEnabled = !_isRunning && GameList.SelectedItem is GameEntry;
        StopButton.IsEnabled = _isRunning && !_isStopping;
        SessionStopButton.IsEnabled = _isRunning && !_isStopping;
        OpenFileButton.IsEnabled = !_isRunning;
    }

    private void UpdateSessionBarVisibility()
    {
        SessionBarPopup.IsOpen = _isRunning && !_isStopping && !_awaitingFirstFrame && GameView.IsVisible &&
            !_gameFullscreen && WindowState != WindowState.FullScreen;
    }

    // ---- Console ----

    private void FlushPendingConsoleLines()
    {
        if (_pendingLines.IsEmpty)
        {
            return;
        }

        var incoming = new List<LogLine>();
        while (incoming.Count < MaxConsoleLinesPerFlush &&
               _pendingLines.TryDequeue(out var pending))
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
