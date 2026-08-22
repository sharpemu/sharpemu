// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
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
    private static readonly TimeSpan NavigationIndicatorAnimationDuration =
        TimeSpan.FromMilliseconds(180);

    private static readonly IBrush DefaultLineBrush = new SolidColorBrush(Color.Parse("#C7CFDE"));
    private static readonly IBrush DimLineBrush = new SolidColorBrush(Color.Parse("#6B7488"));
    private static readonly IBrush InfoLineBrush = new SolidColorBrush(Color.Parse("#6FA8FF"));
    private static readonly IBrush WarningLineBrush = new SolidColorBrush(Color.Parse("#E8B341"));
    private static readonly IBrush ErrorLineBrush = new SolidColorBrush(Color.Parse("#F2777C"));
    private static readonly IBrush SuccessLineBrush = new SolidColorBrush(Color.Parse("#63D489"));
    private readonly LocalizedChoice[] _cpuEngineChoices =
    [
        LocalizedChoice.FromKey("Native", "Options.CpuEngine.Native"),
    ];
    private readonly LocalizedChoice[] _logLevelChoices =
    [
        LocalizedChoice.FromKey("Trace", "Options.LogLevel.Trace"),
        LocalizedChoice.FromKey("Debug", "Options.LogLevel.Debug"),
        LocalizedChoice.FromKey("Info", "Options.LogLevel.Info"),
        LocalizedChoice.FromKey("Warning", "Options.LogLevel.Warning"),
        LocalizedChoice.FromKey("Error", "Options.LogLevel.Error"),
        LocalizedChoice.FromKey("Critical", "Options.LogLevel.Critical"),
    ];
    private readonly LocalizedChoice[] _renderResolutionChoices =
    [
        LocalizedChoice.FromKey("1.0", "Options.RenderResolution.Native"),
        LocalizedChoice.Literal("0.75", "75%"),
        LocalizedChoice.Literal("0.5", "50%"),
        LocalizedChoice.Literal("0.25", "25%"),
    ];
    private readonly LocalizedChoice[] _windowModeChoices =
    [
        LocalizedChoice.FromKey("Windowed", "Options.WindowMode.Windowed"),
        LocalizedChoice.FromKey("Borderless", "Options.WindowMode.Borderless"),
        LocalizedChoice.FromKey("Exclusive", "Options.WindowMode.Exclusive"),
    ];
    private readonly LocalizedChoice[] _scalingModeChoices =
    [
        LocalizedChoice.FromKey("Fit", "Options.Scaling.Fit"),
        LocalizedChoice.FromKey("Cover", "Options.Scaling.Cover"),
        LocalizedChoice.FromKey("Stretch", "Options.Scaling.Stretch"),
        LocalizedChoice.FromKey("Integer", "Options.Scaling.Integer"),
    ];
    private readonly LocalizedChoice[] _hdrModeChoices =
    [
        LocalizedChoice.FromKey("Auto", "Options.Hdr.Auto"),
        LocalizedChoice.FromKey("On", "Common.On"),
        LocalizedChoice.FromKey("Off", "Common.Off"),
    ];
    private readonly LocalizedChoice[] _binkPlaybackChoices =
    [
        LocalizedChoice.FromKey("Host", "Options.Env.BinkPlayback.Host"),
        LocalizedChoice.FromKey("Guest", "Options.Env.BinkPlayback.Guest"),
        LocalizedChoice.FromKey("Skip", "Options.Env.BinkPlayback.Skip"),
    ];
    private readonly List<GameEntry> _allGames = new();
    private readonly ObservableCollection<GameEntry> _visibleGames = new();
    private readonly LibraryTileCollection _libraryTiles;
    private readonly GameLibraryWatcher _libraryWatcher = new();
    private readonly AvaloniaList<LogLine> _consoleLines = new();
    private readonly List<LogLine> _allConsoleLines = new();
    private readonly ConcurrentQueue<(string Line, bool IsError)> _pendingLines = new();
    private readonly DispatcherTimer _consoleFlushTimer;
    private readonly DispatcherTimer _libraryLayoutTimer;

    private GuiSettings _settings = new();
    private IReadOnlyList<HostDisplayOption> _hostDisplays = [];
    private bool _updatingHostDisplayOptions;
    private EmulatorProcess? _emulator;
    private ConsoleWindow? _consoleWindow;
    private GuiConsoleMirror? _consoleMirror;
    private StreamWriter? _fileLog;
    private readonly SndPreviewPlayer _sndPreview = new();
    private string? _emulatorExePath;
    private PendingLaunch? _pendingLaunch;
    private bool _isRunning;
    private bool _isStopping;
    private int _autoScrollTicks;
    private int _activePageIndex;
    private int _optionsSectionIndex;
    private Updater.UpdateInfo? _availableUpdate;
    private string _updateStatusKey = "Updater.Status.Ready";
    private object?[] _updateStatusArgs = [BuildInfo.CommitSha ?? "dev"];

    // Discord Rich Presence state.
    private readonly long _launcherStartUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private DiscordRichPresence? _discord;
    private string? _runningGameName;
    private string? _runningGameTitleId;
    private long _runningSinceUnixSeconds;
    private int _libraryScanGeneration;
    private int _detailLoadGeneration;
    private int _backdropGeneration;
    private bool _isClosing;
    private bool _restoringGameSelection;
    private bool _addFolderInProgress;
    private bool _isLibraryGridLayout;
    private GameEntry? _lastSelectedGame;
    private double _embeddedConsoleHeight = 240;
    private double _libraryRailRowHeight = 188;
    private double _libraryGridRowHeight = 216;

    // Bundled key art shown whenever no game-specific backdrop applies; the
    // plain window color remains the fallback when the asset fails to load.
    private Bitmap? _defaultBackdrop;

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
        EffectiveLaunchSettings Settings,
        SharpEmuRuntimeOptions RuntimeOptions);

    public MainWindow()
    {
        InitializeComponent();
        InitializeLocalizedChoiceBoxes();
        _libraryTiles = new LibraryTileCollection(_visibleGames);

        try
        {
            _defaultBackdrop = new Bitmap(
                AssetLoader.Open(new Uri("avares://SharpEmu.GUI/Assets/pic0.png")));
            BackdropImage.Source = _defaultBackdrop;
            BackdropImage.Opacity = 1.0;
        }
        catch (Exception)
        {
            _defaultBackdrop = null; // color background remains the fallback
        }

        GameList.ItemsSource = _libraryTiles;
        _libraryWatcher.RefreshRequested += OnLibraryRefreshRequested;
        ConsoleList.ItemsSource = _consoleLines;
        _consoleMirror = GuiConsoleMirror.Install((line, isError) =>
            _pendingLines.Enqueue((line, isError)));
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
        _libraryLayoutTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _libraryLayoutTimer.Tick += (_, _) =>
        {
            _libraryLayoutTimer.Stop();
            UpdateLibraryLayoutMetrics();
        };

        TitleBar.PointerPressed += OnTitleBarPointerPressed;
        TitleBar.DoubleTapped += OnTitleBarDoubleTapped;
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            // Some compositors ignore the initial maximized state while a
            // frameless native window is still being created.
            if (WindowState == WindowState.Normal)
            {
                WindowState = WindowState.Maximized;
            }

            UpdateWindowChromeState();
        };
        UpdateWindowChromeState();
        GameList.SelectionChanged += (_, _) => OnGameListSelectionChanged();
        GameList.DoubleTapped += OnGameListDoubleTapped;
        SearchBox.TextChanged += (_, _) => RefreshVisibleGames();
        ConsoleSearchBox.TextChanged += (_, _) => RefreshVisibleConsoleLines();
        OpenFileButton.Click += async (_, _) => await OpenFileAsync();
        LaunchButton.Click += (_, _) =>
        {
            if (_isRunning)
            {
                StopEmulator();
            }
            else
            {
                LaunchSelected();
            }
        };
        LaunchCustomEnvButton.Click += async (_, _) => await OpenCustomEnvironmentDialogAsync();
        ClearLogButton.Click += (_, _) => { _consoleLines.Clear(); _allConsoleLines.Clear(); };
        CopyLogButton.Click += async (_, _) => await CopyConsoleAsync();
        DetachConsoleButton.Click += (_, _) => ShowConsoleWindow();
        CloseConsoleButton.Click += (_, _) => ConsoleToggle.IsChecked = false;
        LibraryTabButton.Click += (_, _) => SetActivePage(0);
        OptionsTabButton.Click += (_, _) => SetActivePage(1);
        LibraryLayoutButton.Click += (_, _) => ToggleLibraryLayout();
        LibraryPage.SizeChanged += (_, _) => ScheduleLibraryLayoutMetricsUpdate();
        LibrarySelectedDetails.SizeChanged += (_, _) => UpdateLibraryMinimumHeight();
        MainContent.SizeChanged += (_, _) => ClampEmbeddedConsoleHeight();
        ConsoleToggle.IsCheckedChanged += (_, _) => UpdateEmbeddedConsoleVisibility();
        WireOptionsNavigation();
        WireGameOptions();

        // The settings page edits _settings live, so a launch started while
        // it is open already uses the new values.
        LogLevelBox.SelectionChanged += (_, _) => _settings.LogLevel = SelectedLogLevel();
        TraceImportsBox.ValueChanged += (_, _) => _settings.ImportTraceLimit = (int)(TraceImportsBox.Value ?? 0);
        RenderResolutionBox.SelectionChanged += (_, _) =>
        {
            if (RenderResolutionBox.SelectedItem is LocalizedChoice { Value: var value } &&
                double.TryParse(
                    value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var scale))
            {
                _settings.RenderResolutionScale = scale;
            }
        };
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
        WindowModeBox.SelectionChanged += (_, _) => _settings.WindowMode = SelectedComboText(WindowModeBox, "Windowed");
        DisplayBox.SelectionChanged += (_, _) => OnHostDisplayChanged();
        ResolutionBox.SelectionChanged += (_, _) => OnHostResolutionChanged();
        RefreshRateBox.SelectionChanged += (_, _) => OnHostRefreshRateChanged();
        ScalingModeBox.SelectionChanged += (_, _) => _settings.ScalingMode = SelectedComboText(ScalingModeBox, "Fit");
        VSyncToggle.IsCheckedChanged += (_, _) => _settings.VSync = VSyncToggle.IsChecked == true;
        HdrModeBox.SelectionChanged += (_, _) => _settings.HdrMode = SelectedComboText(HdrModeBox, "Auto");
        UpdateButton.Click += async (_, _) => await OnUpdateButtonAsync();
        SelectLogFilePathButton.Click += async (_, _) => await SelectLogFilePathAsync();
        EnvBthidToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_BTHID_UNAVAILABLE", EnvBthidToggle.IsChecked == true);
        EnvLoopGuardToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", EnvLoopGuardToggle.IsChecked == true);
        EnvWritableApp0Toggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle("SHARPEMU_WRITABLE_APP0", EnvWritableApp0Toggle.IsChecked == true);
        BinkPlaybackBox.SelectionChanged += (_, _) =>
            _settings.BinkPlaybackMode = SelectedComboText(BinkPlaybackBox, "Host");
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
        EnvGuestImageCpuSyncToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle(
                "SHARPEMU_GUEST_IMAGE_CPU_SYNC",
                EnvGuestImageCpuSyncToggle.IsChecked == true);
        EnvForceSubmitOrphanPreamblesToggle.IsCheckedChanged += (_, _) =>
            SetEnvironmentToggle(
                "SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES",
                EnvForceSubmitOrphanPreamblesToggle.IsChecked == true);
        DefaultProfileBox.TextChanged += (_, _) =>
            _settings.DefaultProfile = GuiSettings.NormalizeDefaultProfile(DefaultProfileBox.Text);
        LanguageBox.SelectionChanged += (_, _) => OnLanguageChanged();

        GameList.AddHandler(ContextRequestedEvent, OnGameContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        CtxLaunch.Click += (_, _) => LaunchSelected();
        CtxOpenFolder.Click += (_, _) => OpenSelectedGameFolder();
        CtxCopyPath.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.Path);
        CtxCopyTitleId.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.TitleId);
        CtxGameSettings.Click += (_, _) => OpenSelectedGameSettings();
        CtxRemove.Click += (_, _) => RemoveSelectedFromLibrary();

        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => BeginWindowClosing();
        Closed += (_, _) => CompleteWindowClosing();

        SdlLauncherGamepad.EnsureStarted();
        _gamepadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        EnvRenderDocToggle.IsCheckedChanged += (_, _) =>

            SetEnvironmentToggle(
                "SHARPEMU_RENDERDOC",
                EnvRenderDocToggle.IsChecked == true);
        DefaultProfileBox.TextChanged += (_, _) =>
            _settings.DefaultProfile = GuiSettings.NormalizeDefaultProfile(DefaultProfileBox.Text);
        LanguageBox.SelectionChanged += (_, _) => OnLanguageChanged();

        GameList.AddHandler(ContextRequestedEvent, OnGameContextRequested, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        CtxLaunch.Click += (_, _) => LaunchSelected();
        CtxOpenFolder.Click += (_, _) => OpenSelectedGameFolder();
        CtxCopyPath.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.Path);
        CtxCopyTitleId.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.TitleId);
        CtxGameSettings.Click += (_, _) => OpenSelectedGameSettings();
        CtxRemove.Click += (_, _) => RemoveSelectedFromLibrary();

        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => BeginWindowClosing();
        Closed += (_, _) => CompleteWindowClosing();

        SdlLauncherGamepad.EnsureStarted();
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
                FileName = ProjectLinks.RepositoryUrl,
                UseShellExecute = true
            });
        };

        DiscordButton.Click += (_, _) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://discord.com/invite/zdTuUU9Uwn",
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
                FileName = ProjectLinks.CommitUrl(_latestCommitSha),
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
            if (index == 0 && _isGameSettingsOpen)
            {
                CloseGameSettings();
            }

            return;
        }

        if (_activePageIndex == 1)
        {
            _settings.Save(); // leaving the Options page
        }

        if (_isGameSettingsOpen)
        {
            CloseGameSettings(restoreLibrary: false);
        }

        _activePageIndex = index;
        SetActiveClass(LibraryTabButton, index == 0);
        SetActiveClass(OptionsTabButton, index == 1);
        LibraryPage.IsVisible = index == 0;
        LibraryToolbar.IsVisible = index == 0;
        OptionsPageSurface.IsVisible = index == 1;
        OptionsPage.IsVisible = index == 1;

        if (index == 1)
        {
            Dispatcher.UIThread.Post(
                () => SetOptionsNavigationIndicator(_optionsSectionIndex, animate: false),
                DispatcherPriority.Loaded);
        }
    }

    private void SetLibraryLayout(bool grid)
    {
        _isLibraryGridLayout = grid;
        SetClass(GameList, "gridLayout", grid);
        SetClass(LibrarySelectedDetails, "gridLayout", grid);
        LibraryPage.Margin = new Thickness(0, 6, 0, 0);
        UpdateLibraryLayoutMetrics();
        UpdateLibraryLayoutButton();

        if (GameList.SelectedItem is { } selected)
        {
            Dispatcher.UIThread.Post(
                () => GameList.ScrollIntoView(selected),
                DispatcherPriority.Loaded);
        }
    }

    private void UpdateLibraryLayoutMetrics()
    {
        var metrics = LibraryLayoutMetrics.Calculate(LibraryPage.Bounds.Width);
        Resources["LibraryCoverSize"] = metrics.CoverSize;
        Resources["LibraryTileWidth"] = metrics.ItemWidth;
        Resources["LibraryRailItemHeight"] = metrics.RailItemHeight;
        Resources["LibraryGridItemHeight"] = metrics.GridItemHeight;
        _libraryRailRowHeight = metrics.RailItemHeight + 16;
        _libraryGridRowHeight = metrics.GridItemHeight + 16;

        GameList.MaxHeight = double.PositiveInfinity;
        UpdateLibraryMinimumHeight();
    }

    private void ScheduleLibraryLayoutMetricsUpdate()
    {
        _libraryLayoutTimer.Stop();
        _libraryLayoutTimer.Start();
    }

    private void UpdateLibraryMinimumHeight()
    {
        var detailsHeight = LibrarySelectedDetails.IsVisible
            ? LibrarySelectedDetails.DesiredSize.Height
            : 0;
        var cardRowHeight = _isLibraryGridLayout
            ? _libraryGridRowHeight
            : _libraryRailRowHeight;
        MainContent.RowDefinitions[1].MinHeight =
            LibraryPage.Margin.Top + detailsHeight + cardRowHeight;
        ClampEmbeddedConsoleHeight();
    }

    private void ToggleLibraryLayout()
    {
        SetLibraryLayout(!_isLibraryGridLayout);
        _settings.LibraryLayout = _isLibraryGridLayout ? "Grid" : "Carousel";
        _settings.Save();
    }

    private void UpdateLibraryLayoutButton()
    {
        LibraryLayoutGlyph.Text = _isLibraryGridLayout ? "view_carousel" : "grid_view";
        var label = Localization.Instance.Get(
            _isLibraryGridLayout ? "Library.View.Carousel" : "Library.View.Grid");
        ToolTip.SetTip(LibraryLayoutButton, label);
        AutomationProperties.SetName(LibraryLayoutButton, label);
    }

    private static void SetActiveClass(Button button, bool active) =>
        SetClass(button, "active", active);

    private static void SetClass(Control control, string className, bool active)
    {
        if (active)
        {
            if (!control.Classes.Contains(className))
            {
                control.Classes.Add(className);
            }
        }
        else
        {
            control.Classes.Remove(className);
        }
    }

    private void WireOptionsNavigation()
    {
        var buttons = OptionsNavigationButtons();
        for (var index = 0; index < buttons.Length; index++)
        {
            var section = index;
            buttons[index].Click += (_, _) => SetOptionsSection(section);
            buttons[index].PointerEntered += (_, _) => SetOptionsNavigationIndicator(section);
            buttons[index].GotFocus += (_, _) => SetOptionsNavigationIndicator(section);
        }

        OptionsNavHost.PointerExited += (_, _) =>
            SetOptionsNavigationIndicator(_optionsSectionIndex);
        SetOptionsSection(0);
    }

    private Button[] OptionsNavigationButtons() =>
    [
        OptionsGeneralNav,
        OptionsLoggingNav,
        OptionsLauncherNav,
        OptionsRenderingNav,
        OptionsEnvironmentNav,
        OptionsAboutNav,
    ];

    private Control[] OptionsSectionPanels() =>
    [
        OptionsGeneralPanel,
        OptionsLoggingPanel,
        OptionsLauncherPanel,
        OptionsRenderingPanel,
        OptionsEnvironmentPanel,
        OptionsAboutPanel,
    ];

    private void SetOptionsSection(int section)
    {
        var buttons = OptionsNavigationButtons();
        var panels = OptionsSectionPanels();
        section = Math.Clamp(section, 0, buttons.Length - 1);
        _optionsSectionIndex = section;
        SetOptionsNavigationIndicator(section);

        for (var index = 0; index < buttons.Length; index++)
        {
            var active = index == section;
            SetActiveClass(buttons[index], active);
            SetOptionsPanelInteraction(panels[index], active);
        }
    }

    private static void SetOptionsPanelInteraction(Control panel, bool active)
    {
        if (active)
        {
            if (!panel.Classes.Contains("active"))
            {
                panel.Classes.Add("active");
            }
        }
        else
        {
            panel.Classes.Remove("active");
        }

        panel.IsHitTestVisible = active;
        AutomationProperties.SetAccessibilityView(
            panel,
            active ? AccessibilityView.Content : AccessibilityView.Raw);
        KeyboardNavigation.SetTabNavigation(
            panel,
            active ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
    }

    private void SetOptionsNavigationIndicator(int section, bool animate = true)
    {
        var buttons = OptionsNavigationButtons();
        var button = buttons[Math.Clamp(section, 0, buttons.Length - 1)];
        MoveNavigationIndicator(
            OptionsNavIndicator,
            OptionsNavHost,
            button,
            section,
            animate);
    }

    private static void ConfigureNavigationIndicatorAnimation(Border indicator)
    {
        if (ElementComposition.GetElementVisual(indicator) is not { } visual)
        {
            return;
        }

        var translationAnimation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translationAnimation.Duration = NavigationIndicatorAnimationDuration;
        translationAnimation.Target = nameof(CompositionVisual.Translation);
        translationAnimation.InsertExpressionKeyFrame(
            1f,
            "this.FinalValue",
            new CubicEaseOut());

        var animations = visual.Compositor.CreateImplicitAnimationCollection();
        animations[nameof(CompositionVisual.Translation)] = translationAnimation;
        visual.ImplicitAnimations = animations;
    }

    private static void MoveNavigationIndicator(
        Border indicator,
        Control host,
        Button button,
        int section,
        bool animate = true)
    {
        if (ElementComposition.GetElementVisual(indicator) is not { } visual)
        {
            return;
        }

        var targetY = button.TranslatePoint(default, host)?.Y
            ?? section * button.Bounds.Height;

        if (!animate)
        {
            visual.ImplicitAnimations = null;
            visual.StopAnimation(nameof(CompositionVisual.Translation));
        }
        else if (visual.ImplicitAnimations is null)
        {
            ConfigureNavigationIndicatorAnimation(indicator);
        }

        visual.Translation = new Vector3D(0, targetY, 0);

        if (!animate)
        {
            ConfigureNavigationIndicatorAnimation(indicator);
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
        _latestCommitSha = null;
        LatestCommitHashText.Content = "Loading…";
        LatestCommitHashText.IsEnabled = false;

        try
        {
            using var response = await GithubHttpClient.GetAsync(ProjectLinks.LatestCommitApiUrl);
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
        if (!SdlLauncherGamepad.TryGetState(out var pad))
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

        if (_isRunning || _isStopping)
        {
            // The controller belongs to the separate game window while a
            // session is active; Circle/B must never stop the session.
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

        if (_activePageIndex != 0 || _isGameSettingsOpen)
        {
            _previousPadButtons = pad.Buttons;
            return;
        }

        var now = Environment.TickCount64;
        var left = (pad.Buttons & HostGamepadButtons.Left) != 0 || pad.LeftX < 64;
        var right = (pad.Buttons & HostGamepadButtons.Right) != 0 || pad.LeftX > 192;

        if (ShouldNavigate(left, ref _navLeftNextAt, now))
        {
            MoveSelection(-1);
        }

        if (ShouldNavigate(right, ref _navRightNextAt, now))
        {
            MoveSelection(1);
        }

        if (_isLibraryGridLayout)
        {
            var up = (pad.Buttons & HostGamepadButtons.Up) != 0 || pad.LeftY < 64;
            var down = (pad.Buttons & HostGamepadButtons.Down) != 0 || pad.LeftY > 192;
            var rowStep = LibraryRowStep();

            if (ShouldNavigate(up, ref _navUpNextAt, now))
            {
                MoveSelection(-rowStep);
            }

            if (ShouldNavigate(down, ref _navDownNextAt, now))
            {
                MoveSelection(rowStep);
            }
        }

        var pressed = pad.Buttons & ~_previousPadButtons;
        if ((pressed & HostGamepadButtons.Cross) != 0)
        {
            LaunchSelected();
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

    private int LibraryRowStep()
    {
        if (GameList.ContainerFromIndex(0) is not { } first)
        {
            return 1;
        }

        var top = first.Bounds.Top;
        var columns = 1;
        for (var index = 1; index < _libraryTiles.Count; index++)
        {
            if (GameList.ContainerFromIndex(index) is not { } container ||
                Math.Abs(container.Bounds.Top - top) > 0.5)
            {
                break;
            }

            columns++;
        }

        return columns;
    }

    private void MoveSelection(int delta)
    {
        var index = GameList.SelectedIndex < 0
            ? 0
            : Math.Clamp(GameList.SelectedIndex + delta, 0, _libraryTiles.Count - 1);
        GameList.SelectedIndex = index;
        GameList.ScrollIntoView(index);
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

        SeedLibraryFromCache();
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
        if (LanguageBox.SelectedItem is not LanguageInfo language)
        {
            return;
        }

        _settings.Language = language.Code;
        Localization.Instance.Load(language.Code);
        ApplyLocalization();
    }

    /// <summary>
    /// Recomputes localized strings that also depend on runtime state.
    /// Static labels update automatically through XAML bindings.
    /// </summary>
    private void ApplyLocalization()
    {
        RefreshOptionsNavigationLabels();
        RefreshLocalizedChoices();
        UpdateLogFilePathText();
        RefreshHostRefreshRates(_settings.RefreshRate);
        RefreshUpdateText();
        UpdateEmptyStateTexts();
        UpdateLibraryLayoutButton();
        UpdateRunButtons();
    }

    private void RefreshOptionsNavigationLabels()
    {
        var localization = Localization.Instance;
        SetOptionsNavigationLabel(
            OptionsGeneralNav,
            OptionsGeneralNavLabel,
            localization.Get("Options.General"));
        SetOptionsNavigationLabel(
            OptionsLoggingNav,
            OptionsLoggingNavLabel,
            localization.Get("Options.Section.Logging"));
        SetOptionsNavigationLabel(
            OptionsLauncherNav,
            OptionsLauncherNavLabel,
            localization.Get("Options.Section.Launcher"));
        SetOptionsNavigationLabel(
            OptionsRenderingNav,
            OptionsRenderingNavLabel,
            localization.Get("Options.Section.Rendering"));
        SetOptionsNavigationLabel(
            OptionsEnvironmentNav,
            OptionsEnvironmentNavLabel,
            localization.Get("Options.Env.Tab"));
        SetOptionsNavigationLabel(
            OptionsAboutNav,
            OptionsAboutNavLabel,
            localization.Get("Options.About"));
    }

    private static void SetOptionsNavigationLabel(Button button, TextBlock label, string value)
    {
        var navigationLabel = SectionNavigationLabel(value);
        label.Text = navigationLabel;
        AutomationProperties.SetName(button, navigationLabel);
    }

    private static string SectionNavigationLabel(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(char.IsLower))
        {
            return value;
        }

        var lower = value.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
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
        if (args.Key == Key.Escape && _isGameSettingsOpen)
        {
            CloseGameSettings();
            args.Handled = true;
            return;
        }

        if (args.Key == Key.F11 && !_isRunning)
        {
            WindowState = WindowState == WindowState.FullScreen
                ? WindowState.Maximized
                : WindowState.FullScreen;
            args.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        // The session runs in its own SDL window and takes keyboard focus with
        // it, so launcher buttons no longer see game input and nothing has to
        // be swallowed here. Kept as the wired handler because the launcher
        // still needs a preview hook for its own shortcuts.
    }

    private void BeginWindowClosing()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        Interlocked.Increment(ref _libraryScanGeneration);
        Interlocked.Increment(ref _detailLoadGeneration);
        _consoleFlushTimer.Stop();
        _libraryLayoutTimer.Stop();
        _gamepadTimer.Stop();
    }

    private void CompleteWindowClosing()
    {
        RunShutdownStep("library watcher", _libraryWatcher.Dispose);
        RunShutdownStep("settings", _settings.Save);
        RunShutdownStep("SDL gamepad", SdlLauncherGamepad.Shutdown);
        RunShutdownStep("title music", _sndPreview.Stop);
        RunShutdownStep("Discord Rich Presence", () => _discord?.Dispose());
        RunShutdownStep("console window", () => _consoleWindow?.Close());
        RunShutdownStep("emulator process", () => _emulator?.Dispose());
        RunShutdownStep("console mirror", () => _consoleMirror?.Dispose());
        RunShutdownStep("file log", DropFileLog);
    }

    private static void RunShutdownStep(string component, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[GUI][WARN] Failed to clean up {component}: {exception}");
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source &&
            source.FindAncestorOfType<Button>(includeSelf: true) is null &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source &&
            source.FindAncestorOfType<Button>(includeSelf: true) is null)
        {
            ToggleMaximized();
            e.Handled = true;
        }
    }

    private void ToggleMaximized()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeGlyph is not { } glyph || MaximizeButton is not { } button)
        {
            return;
        }

        var state = GetMaximizeButtonState(WindowState);
        glyph.Text = state.Glyph;
        ToolTip.SetTip(button, state.ToolTip);
        AutomationProperties.SetName(button, state.AutomationName);
    }

    internal static WindowMaximizeButtonState GetMaximizeButtonState(WindowState windowState) =>
        windowState == WindowState.Maximized
            ? new("filter_none", "Restore", "Restore window")
            : new("crop_square", "Maximize", "Maximize window");

    private void UpdateWindowChromeState()
    {
        UpdateMaximizeButton();
        var isFullscreen = WindowState == WindowState.FullScreen;

        if (TitleBar is { } titleBar)
        {
            titleBar.IsVisible = !isFullscreen;
        }

        if (ResizeHandles is { } handles)
        {
            handles.IsVisible = CanResize && WindowState == WindowState.Normal;
        }
    }

    private void OnResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            sender is not Control { Tag: string edgeName } ||
            !Enum.TryParse<WindowEdge>(edgeName, out var edge))
        {
            return;
        }

        BeginResizeDrag(edge, e);
        e.Handled = true;
    }

    // ---- Settings ----

    private void InitializeLocalizedChoiceBoxes()
    {
        CpuEngineBox.ItemsSource = _cpuEngineChoices;
        LogLevelBox.ItemsSource = _logLevelChoices;
        RenderResolutionBox.ItemsSource = _renderResolutionChoices;
        WindowModeBox.ItemsSource = _windowModeChoices;
        ScalingModeBox.ItemsSource = _scalingModeChoices;
        HdrModeBox.ItemsSource = _hdrModeChoices;
        BinkPlaybackBox.ItemsSource = _binkPlaybackChoices;
    }

    private void RefreshLocalizedChoices()
    {
        RefreshChoices(_cpuEngineChoices);
        RefreshChoices(_logLevelChoices);
        RefreshChoices(_renderResolutionChoices);
        RefreshChoices(_windowModeChoices);
        RefreshChoices(_scalingModeChoices);
        RefreshChoices(_hdrModeChoices);
        RefreshChoices(_binkPlaybackChoices);
    }

    private static void RefreshChoices(IEnumerable<LocalizedChoice> choices)
    {
        foreach (var choice in choices)
        {
            choice.Refresh(Localization.Instance);
        }
    }

    private void ApplySettingsToControls()
    {
        CpuEngineBox.SelectedIndex = 0;
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
        RenderResolutionBox.SelectedIndex = _settings.RenderResolutionScale switch
        {
            >= 0.875 => 0,
            >= 0.625 => 1,
            >= 0.375 => 2,
            _ => 3,
        };
        StrictToggle.IsChecked = _settings.StrictDynlibResolution;
        LogToFileToggle.IsChecked = _settings.LogToFile;
        OverrideLogFileToggle.IsChecked = _settings.OverrideLogFile;
        TitleMusicToggle.IsChecked = _settings.PlayTitleMusic;
        SetLibraryLayout(string.Equals(_settings.LibraryLayout, "Grid", StringComparison.OrdinalIgnoreCase));
        DiscordToggle.IsChecked = _settings.DiscordRichPresence;
        AutoUpdateToggle.IsChecked = _settings.CheckForUpdatesOnStartup;
        EnvBthidToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_BTHID_UNAVAILABLE");
        EnvLoopGuardToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD");
        EnvWritableApp0Toggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_WRITABLE_APP0");
        BinkPlaybackBox.SelectedIndex = ChoiceIndex(_settings.BinkPlaybackMode, "Host", "Guest", "Skip");
        EnvVkValidationToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_VK_VALIDATION");
        EnvDumpSpirvToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_DUMP_SPIRV");
        EnvLogDirectMemoryToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_LOG_DIRECT_MEMORY");
        EnvLogIoToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_LOG_IO");
        EnvLogNpToggle.IsChecked = _settings.EnvironmentToggles.Contains("SHARPEMU_LOG_NP");
        EnvGuestImageCpuSyncToggle.IsChecked =
            _settings.EnvironmentToggles.Contains("SHARPEMU_GUEST_IMAGE_CPU_SYNC");
        EnvForceSubmitOrphanPreamblesToggle.IsChecked =
            _settings.EnvironmentToggles.Contains("SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES");
        EnvRenderDocToggle.IsChecked =
            _settings.EnvironmentToggles.Contains("SHARPEMU_RENDERDOC");
        DefaultProfileBox.Text = _settings.DefaultProfile;
        WindowModeBox.SelectedIndex = ChoiceIndex(_settings.WindowMode, "Windowed", "Borderless", "Exclusive");
        LoadHostDisplayOptions();
        ScalingModeBox.SelectedIndex = ChoiceIndex(_settings.ScalingMode, "Fit", "Cover", "Stretch", "Integer");
        VSyncToggle.IsChecked = _settings.VSync;
        HdrModeBox.SelectedIndex = ChoiceIndex(_settings.HdrMode, "Auto", "On", "Off");
        UpdateLogFilePathText();
    }

    private static string SelectedComboText(ComboBox comboBox, string fallback) =>
        comboBox.SelectedItem switch
        {
            LocalizedChoice { Value: var value } => value,
            string value => value,
            _ => fallback,
        };

    private void LoadHostDisplayOptions()
    {
        _updatingHostDisplayOptions = true;
        try
        {
            _hostDisplays = HostDisplayOptions.BuildDisplays(
                HostDisplayCatalog.Query(),
                _settings.DisplayIndex);
            DisplayBox.ItemsSource = _hostDisplays;
            var display = HostDisplayOptions.SelectDisplay(_hostDisplays, _settings.DisplayIndex);
            DisplayBox.SelectedItem = display;
            PopulateHostModes(display, _settings.Resolution, _settings.RefreshRate);
        }
        finally
        {
            _updatingHostDisplayOptions = false;
        }

        SyncHostVideoSettings();
    }

    private void OnHostDisplayChanged()
    {
        if (_updatingHostDisplayOptions || DisplayBox.SelectedItem is not HostDisplayOption display)
        {
            return;
        }

        _updatingHostDisplayOptions = true;
        try
        {
            PopulateHostModes(display, _settings.Resolution, _settings.RefreshRate);
        }
        finally
        {
            _updatingHostDisplayOptions = false;
        }

        SyncHostVideoSettings();
    }

    private void OnHostResolutionChanged()
    {
        if (_updatingHostDisplayOptions || DisplayBox.SelectedItem is not HostDisplayOption)
        {
            return;
        }

        _settings.Resolution = SelectedComboText(ResolutionBox, "1920x1080");
        RefreshHostRefreshRates(_settings.RefreshRate);
        OnHostRefreshRateChanged();
    }

    private void OnHostRefreshRateChanged()
    {
        if (!_updatingHostDisplayOptions && RefreshRateBox.SelectedItem is HostRefreshRateOption refreshRate)
        {
            _settings.RefreshRate = refreshRate.Value;
        }
    }

    private void PopulateHostModes(
        HostDisplayOption display,
        string selectedResolution,
        int selectedRefreshRate)
    {
        var resolutions = HostDisplayOptions.BuildResolutions(display, selectedResolution);
        ResolutionBox.ItemsSource = resolutions;
        ResolutionBox.SelectedItem = resolutions.FirstOrDefault(resolution =>
            string.Equals(resolution, selectedResolution, StringComparison.OrdinalIgnoreCase)) ?? resolutions[0];
        RefreshHostRefreshRates(selectedRefreshRate);
    }

    private void RefreshHostRefreshRates(int selectedRefreshRate)
    {
        if (DisplayBox.SelectedItem is not HostDisplayOption display)
        {
            return;
        }

        var wasUpdating = _updatingHostDisplayOptions;
        _updatingHostDisplayOptions = true;
        try
        {
            var rates = HostDisplayOptions.BuildRefreshRates(
                display,
                SelectedComboText(ResolutionBox, _settings.Resolution),
                selectedRefreshRate,
                Localization.Instance.Get("Options.RefreshRate.Automatic"));
            RefreshRateBox.ItemsSource = rates;
            RefreshRateBox.SelectedItem = rates.FirstOrDefault(rate => rate.Value == selectedRefreshRate) ?? rates[0];
        }
        finally
        {
            _updatingHostDisplayOptions = wasUpdating;
        }
    }

    private void SyncHostVideoSettings()
    {
        if (DisplayBox.SelectedItem is HostDisplayOption display)
        {
            _settings.DisplayIndex = display.Index;
        }

        _settings.Resolution = SelectedComboText(ResolutionBox, "1920x1080");
        _settings.RefreshRate = RefreshRateBox.SelectedItem is HostRefreshRateOption refreshRate
            ? refreshRate.Value
            : 0;
    }

    private static int ChoiceIndex(string value, params string[] choices)
    {
        var index = Array.FindIndex(choices, choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
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
        catch (InvalidDataException)
        {
            SetUpdateStatus("Updater.Status.ChecksumFailed");
            UpdateButton.IsEnabled = true;
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

    private const string DefaultProfileEnvironmentName = "SHARPEMU_DEFAULT_PROFILE";
    private const string BinkModeEnvironmentName = "SHARPEMU_BINK_MODE";

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
        if (!_settings.GameFolders.Contains(path, GameLibraryPath.Comparer))
        {
            _settings.GameFolders.Add(path);
            changed = true;
        }

        // Adding (or re-adding) a folder is an explicit signal to restore any
        // games beneath it that were removed from the library earlier.
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        changed |= _settings.ExcludedGames.RemoveAll(excluded =>
            excluded.StartsWith(prefix, GameLibraryPath.Comparison)) > 0;

        if (changed)
        {
            _settings.Save();
        }

        await RescanLibraryAsync();
    }

    private void OnLibraryRefreshRequested(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_isClosing)
                {
                    _ = RescanLibraryFromWatcherAsync();
                }
            },
            DispatcherPriority.Background);
    }

    private async Task RescanLibraryFromWatcherAsync()
    {
        try
        {
            await RescanLibraryAsync(showProgress: false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[GUI][WARN] Automatic library refresh failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Paints the previous scan's result before the real scan starts. The
    /// scan that follows reconciles over this, so the cache only ever
    /// shortens the blank period; it never decides what the library holds.
    /// </summary>
    private void SeedLibraryFromCache()
    {
        Dispatcher.UIThread.VerifyAccess();

        if (_allGames.Count != 0)
        {
            return;
        }

        var cached = GameLibraryCache.Load(_settings.GameFolders.ToArray());
        if (cached.Count == 0)
        {
            return;
        }

        _allGames.AddRange(cached);
        RefreshVisibleGames(new HashSet<GameEntry>(cached));
        LoadGameDetailsInBackground(cached, cached);
    }

    private async Task RescanLibraryAsync(bool showProgress = true)
    {
        Dispatcher.UIThread.VerifyAccess();

        var scanGeneration = Interlocked.Increment(ref _libraryScanGeneration);
        var folders = _settings.GameFolders.ToArray();
        var excluded = new HashSet<string>(_settings.ExcludedGames, GameLibraryPath.Comparer);
        _libraryWatcher.Watch(folders);
        var showLoadingState = showProgress && _allGames.Count == 0;
        if (showLoadingState)
        {
            EmptyState.IsVisible = false;
            LoadingState.IsVisible = true;
        }

        var games = await Task.Run(() => ScanFolders(folders, excluded));
        if (_isClosing || scanGeneration != Volatile.Read(ref _libraryScanGeneration))
        {
            return;
        }

        Dispatcher.UIThread.VerifyAccess();
        var reconciliation = GameLibraryReconciler.Reconcile(_allGames, games);
        _allGames.Clear();
        _allGames.AddRange(reconciliation.Games);
        RefreshVisibleGames(reconciliation.BackgroundsChanged);
        LoadingState.IsVisible = false;
        LoadGameDetailsInBackground(reconciliation.CoversToLoad, reconciliation.Games);
        UpdateDiscordPresence();
        GameLibraryCache.Save(folders, reconciliation.Games);
    }

    /// <summary>
    /// Enriches games off the UI thread — decodes cover art and totals each
    /// game's install folder size — posting results back as they become
    /// ready. A newer scan invalidates older loads.
    /// </summary>
    private void LoadGameDetailsInBackground(
        IReadOnlyList<GameEntry> coversToLoad,
        IReadOnlyList<GameEntry> gamesToMeasure)
    {
        var generation = ++_detailLoadGeneration;
        _ = Task.Run(() =>
        {
            // Covers first: they are cheap and the most visible, so the grid
            // fills with art before the (potentially slow) size pass runs.
            foreach (var game in coversToLoad)
            {
                if (generation != _detailLoadGeneration)
                {
                    return;
                }

                var requestedCoverPath = game.CoverPath;
                if (requestedCoverPath is null)
                {
                    continue;
                }

                try
                {
                    using var stream = File.OpenRead(requestedCoverPath);
                    var bitmap = Bitmap.DecodeToWidth(stream, 312);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (generation == _detailLoadGeneration
                            && _allGames.Contains(game)
                            && string.Equals(
                                game.CoverPath,
                                requestedCoverPath,
                                StringComparison.Ordinal))
                        {
                            game.Cover = bitmap;
                        }
                        else
                        {
                            bitmap.Dispose();
                        }
                    });
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        $"[GUI][WARN] Could not load cover '{requestedCoverPath}': {exception.Message}");
                }
            }

            foreach (var game in gamesToMeasure)
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
                        if (generation == _detailLoadGeneration && _allGames.Contains(game))
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
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[GUI][WARN] Could not measure game folder '{directory}': {exception.Message}");
        }

        return total;
    }

    private static List<GameEntry> ScanFolders(IReadOnlyList<string> folders, IReadOnlySet<string> excludedPaths)
    {
        var games = new List<GameEntry>();
        var seen = new HashSet<string>(GameLibraryPath.Comparer);
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
                    catch (IOException exception)
                    {
                        Console.Error.WriteLine(
                            $"[GUI][WARN] Could not inspect executable '{fullPath}': {exception.Message}");
                    }

                    var (title, titleId, version) = TryReadParamJson(fullPath);
                    games.Add(new GameEntry(
                        title ?? GameNameFor(fullPath), titleId, version, fullPath, size,
                        FindCoverFor(fullPath), FindBackgroundFor(fullPath)));
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[GUI][WARN] Could not scan game folder '{folder}': {exception.Message}");
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

    private void OnGameListSelectionChanged()
    {
        if (_restoringGameSelection)
        {
            return;
        }

        if (GameList.SelectedItem is AddFolderTile)
        {
            _restoringGameSelection = true;
            try
            {
                GameList.SelectedItem = _lastSelectedGame;
            }
            finally
            {
                _restoringGameSelection = false;
            }

            // A held direction must not reopen the picker after it closes.
            _navLeftNextAt = long.MaxValue;
            _navRightNextAt = long.MaxValue;
            StartAddFolderFromTile();
            return;
        }

        _lastSelectedGame = GameList.SelectedItem as GameEntry;
        UpdateSelectedGame();
    }

    private void OnGameListDoubleTapped(object? sender, TappedEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (item?.DataContext is AddFolderTile)
        {
            e.Handled = true;
            return;
        }

        LaunchSelected();
    }

    private void StartAddFolderFromTile()
    {
        if (_addFolderInProgress)
        {
            return;
        }

        _addFolderInProgress = true;
        _ = AddFolderFromTileAsync();
    }

    private async Task AddFolderFromTileAsync()
    {
        try
        {
            await AddFolderAsync();
        }
        finally
        {
            _addFolderInProgress = false;
        }
    }

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
            AppendConsoleLine(
                Localization.Instance.Format("Status.CouldNotOpenFolder", ex.Message),
                WarningLineBrush);
        }
    }

    private async Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is null)
        {
            return;
        }

        await Clipboard.SetTextAsync(text);
    }

    private void RemoveSelectedFromLibrary()
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        if (!_settings.ExcludedGames.Contains(game.Path, GameLibraryPath.Comparer))
        {
            _settings.ExcludedGames.Add(game.Path);
            _settings.Save();
        }

        _allGames.RemoveAll(g =>
            string.Equals(g.Path, game.Path, GameLibraryPath.Comparison));
        GameList.SelectedItem = null;
        RefreshVisibleGames();
    }

    private void RefreshVisibleGames(IReadOnlySet<GameEntry>? backgroundsChanged = null)
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var selectedBefore = GameList.SelectedItem as GameEntry;
        var selectedPath = selectedBefore?.Path;
        var desired = new List<GameEntry>(_allGames.Count);

        foreach (var game in _allGames)
        {
            if (query.Length == 0 ||
                game.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                game.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (game.TitleId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                desired.Add(game);
            }
        }

        GameLibraryReconciler.ReconcileVisibleGames(_visibleGames, desired);

        var selectedAfter = ResolveLibrarySelection(_visibleGames, selectedPath);
        if (!ReferenceEquals(GameList.SelectedItem, selectedAfter))
        {
            GameList.SelectedItem = selectedAfter;
        }

        EmptyState.IsVisible = _visibleGames.Count == 0;
        UpdateEmptyStateTexts();

        if (!ReferenceEquals(selectedBefore, selectedAfter))
        {
            UpdateSelectedGame();
        }
        else
        {
            UpdateRunButtons();
            if (selectedAfter is not null && backgroundsChanged?.Contains(selectedAfter) == true)
            {
                _ = UpdateBackdropAsync(selectedAfter);
            }
        }
    }

    internal static GameEntry? ResolveLibrarySelection(
        IReadOnlyList<GameEntry> visibleGames,
        string? selectedPath)
    {
        var selected = selectedPath is null
            ? null
            : visibleGames.FirstOrDefault(game =>
                game.Path.Equals(selectedPath, GameLibraryPath.Comparison));
        return selected ?? visibleGames.FirstOrDefault();
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
    }

    private void UpdateSelectedGame()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            LibrarySelectedDetails.DataContext = game;
            LibrarySelectedDetails.IsVisible = true;
            _ = UpdateBackdropAsync(game);
            PlaySelectedGamePreview(game);
        }
        else
        {
            LibrarySelectedDetails.DataContext = null;
            LibrarySelectedDetails.IsVisible = false;
            _ = UpdateBackdropAsync(null);
            _sndPreview.Stop();
        }

        UpdateLibraryMinimumHeight();
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
            UpdateWindowChromeState();

            // The XAML WindowState="Maximized" assignment raises this change
            // during InitializeComponent, before named controls are wired up.
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

        // The bundled key art is the primary backdrop whenever the selection
        // has no art of its own; the window color stays as the last fallback.
        void ShowDefaultBackdrop()
        {
            if (generation == _backdropGeneration && _defaultBackdrop is not null)
            {
                BackdropImage.Source = _defaultBackdrop;
                BackdropImage.Opacity = 1.0;
            }
        }

        if (game?.BackgroundPath is null)
        {
            ShowDefaultBackdrop();
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
                ShowDefaultBackdrop(); // undecodable key art
                return;
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

    private async Task OpenCustomEnvironmentDialogAsync()
    {
        if (_isRunning || GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        var perGame = PerGameSettings.Load(game.TitleId);
        var initialEntries = CustomEnvironmentVariables.Merge(
            GlobalCustomEnvironmentSettings.Load(),
            perGame?.CustomEnvironmentVariables);
        var dialog = new CustomEnvironmentDialog(game.TitleId, initialEntries);
        dialog.SaveGlobalRequested += entries => GlobalCustomEnvironmentSettings.Save(entries);
        dialog.SaveGameRequested += (titleId, entries) =>
        {
            var settings = PerGameSettings.Load(titleId) ?? new PerGameSettings();
            settings.CustomEnvironmentVariables = entries.Count == 0 ? null : entries.ToList();
            settings.RemoveInheritedValues(_settings);
            settings.Save(titleId);
        };

        var customEntries = await dialog.ShowDialog<IReadOnlyList<string>?>(this);
        if (customEntries is not null && !_isRunning)
        {
            Launch(game.Path, game.Name, game.TitleId, customEntries);
        }
    }

    private void LaunchSelected()
    {
        if (GameList.SelectedItem is GameEntry game)
        {
            Launch(game.Path, game.Name, game.TitleId);
        }
    }

    private void Launch(
        string ebootPath,
        string displayName,
        string? titleId = null,
        IReadOnlyList<string>? oneShotCustomEnvironment = null)
    {
        if (_isRunning)
        {
            return;
        }

        var resolvedTitleId = string.IsNullOrWhiteSpace(titleId)
            ? _allGames.FirstOrDefault(game =>
                game.Path.Equals(ebootPath, GameLibraryPath.Comparison))?.TitleId
            : titleId;
        var perGame = PerGameSettings.Load(resolvedTitleId);
        var effective = EffectiveLaunchSettings.Resolve(_settings, perGame);
        var customEnvironment = CustomEnvironmentVariables.Merge(
            GlobalCustomEnvironmentSettings.Load(),
            perGame?.CustomEnvironmentVariables,
            oneShotCustomEnvironment);

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
            if (!effective.EnvironmentToggles.Any(entry =>
                    TryParseEnvironmentEntry(entry, out var name, out _) &&
                    string.Equals(name, staleName, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable(staleName, null);
            }
        }

        _appliedEnvironmentVariables.Clear();
        foreach (var entry in effective.EnvironmentToggles)
        {
            if (!TryParseEnvironmentEntry(entry, out var name, out var value))
            {
                continue;
            }

            if (string.Equals(name, DefaultProfileEnvironmentName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(name, value);
            _appliedEnvironmentVariables.Add(name);
        }

        Environment.SetEnvironmentVariable(
            DefaultProfileEnvironmentName,
            GuiSettings.NormalizeDefaultProfile(_settings.DefaultProfile));
        _appliedEnvironmentVariables.Add(DefaultProfileEnvironmentName);

        Environment.SetEnvironmentVariable(
            BinkModeEnvironmentName,
            _settings.BinkPlaybackMode switch
            {
                "Guest" => "guest",
                "Skip" => "skip",
                _ => "native",
            });
        _appliedEnvironmentVariables.Add(BinkModeEnvironmentName);

        Environment.SetEnvironmentVariable(
            "SHARPEMU_RENDER_SCALE",
            _settings.RenderResolutionScale.ToString(
                "0.###",
                System.Globalization.CultureInfo.InvariantCulture));
        _appliedEnvironmentVariables.Add("SHARPEMU_RENDER_SCALE");

        foreach (var entry in customEnvironment)
        {
            if (!CustomEnvironmentVariables.TryParseEntry(entry, out var name, out var value))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(name, value);
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
        _isStopping = false;
        _runningGameName = displayName;
        _runningGameTitleId = resolvedTitleId;
        _runningSinceUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        UpdateRunButtons();
        UpdateDiscordPresence();

        _pendingLaunch = new PendingLaunch(
            Path.GetFullPath(ebootPath),
            displayName,
            _runningGameTitleId,
            effective,
            runtimeOptions);

        StartPendingSession();
    }

    private static bool TryParseEnvironmentEntry(string entry, out string name, out string value)
    {
        var separator = entry.IndexOf('=');
        name = separator >= 0 ? entry[..separator] : entry;
        value = separator >= 0 ? entry[(separator + 1)..] : "1";
        return name.StartsWith("SHARPEMU_", StringComparison.OrdinalIgnoreCase) &&
               name.Length > "SHARPEMU_".Length &&
               value.Length != 0;
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
        UpdateRunButtons();
        _emulator.Stop();
        _runningGameName = null;
        _runningGameTitleId = null;
        UpdateDiscordPresence();
        UpdateEmbeddedConsoleVisibility();
        Console.Error.WriteLine("[GUI][INFO] Waiting for the SDL game process to exit.");
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
        UpdateEmbeddedConsoleVisibility();

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

        _runningGameName = null;
        _runningGameTitleId = null;
        UpdateRunButtons();
        UpdateDiscordPresence();
    }

    private void StartPendingSession()
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
            var arguments = BuildEmulatorArguments(launch);
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

    private List<string> BuildEmulatorArguments(PendingLaunch launch)
    {
        var arguments = new List<string>
        {
            "--cpu-engine=native",
            $"--log-level={launch.Settings.LogLevel}",
        };
        if (launch.RuntimeOptions.StrictDynlibResolution)
        {
            arguments.Add("--strict");
        }
        if (launch.RuntimeOptions.ImportTraceLimit > 0)
        {
            arguments.Add($"--trace-imports={launch.RuntimeOptions.ImportTraceLimit}");
        }

        arguments.Add($"--window-mode={launch.Settings.WindowMode.ToLowerInvariant()}");
        arguments.Add($"--resolution={launch.Settings.Resolution}");
        arguments.Add($"--display={launch.Settings.DisplayIndex}");
        arguments.Add($"--refresh-rate={launch.Settings.RefreshRate}");
        arguments.Add($"--scaling={launch.Settings.ScalingMode.ToLowerInvariant()}");
        arguments.Add($"--vsync={(launch.Settings.VSync ? "on" : "off")}");
        arguments.Add($"--hdr={launch.Settings.HdrMode.ToLowerInvariant()}");

        arguments.Add(launch.EbootPath);
        return arguments;
    }

    private void OnEmulatorOutput(string line, bool isError)
    {
        _pendingLines.Enqueue((line, isError));
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
        LaunchButton.Classes.Remove("accent");
        LaunchButton.Classes.Remove("danger");

        if (_isRunning)
        {
            LaunchButton.Classes.Add("danger");
            LaunchButton.Content = Localization.Instance.Get(
                _isStopping ? "Launch.Stopping" : "Launch.Stop");
            LaunchButton.IsEnabled = !_isStopping;
        }
        else
        {
            LaunchButton.Classes.Add("accent");
            LaunchButton.Content = Localization.Instance.Get("Launch.Launch");
            LaunchButton.IsEnabled = GameList.SelectedItem is GameEntry;
        }

        LaunchCustomEnvButton.IsEnabled = !_isRunning && GameList.SelectedItem is GameEntry;

        GameSettingsButton.IsEnabled =
            GameList.SelectedItem is GameEntry game &&
            !string.IsNullOrWhiteSpace(game.TitleId);
        OpenFileButton.IsEnabled = !_isRunning;
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

    private void UpdateEmbeddedConsoleVisibility()
    {
        var visible = ConsoleToggle.IsChecked == true && _consoleWindow is null;
        if (visible == ConsolePanel.IsVisible)
        {
            return;
        }

        if (!visible && ConsolePanel.Bounds.Height >= 120)
        {
            _embeddedConsoleHeight = ConsolePanel.Bounds.Height;
        }

        ConsolePanel.IsVisible = visible;
        ConsoleSplitter.IsVisible = visible;
        MainContent.RowDefinitions[2].Height = visible
            ? new GridLength(8)
            : new GridLength(0);
        MainContent.RowDefinitions[3].MinHeight = visible ? 120 : 0;
        MainContent.RowDefinitions[3].Height = visible
            ? new GridLength(Math.Min(_embeddedConsoleHeight, MaximumEmbeddedConsoleHeight()))
            : new GridLength(0);
    }

    private void ClampEmbeddedConsoleHeight()
    {
        if (!ConsolePanel.IsVisible)
        {
            return;
        }

        var maximumHeight = MaximumEmbeddedConsoleHeight();
        if (MainContent.RowDefinitions[3].ActualHeight > maximumHeight)
        {
            MainContent.RowDefinitions[3].Height = new GridLength(maximumHeight);
        }
    }

    private double MaximumEmbeddedConsoleHeight()
    {
        const double minimumConsoleHeight = 120;
        const double splitterHeight = 8;
        var toolbarHeight = ContentToolbar.Bounds.Height +
            ContentToolbar.Margin.Top +
            ContentToolbar.Margin.Bottom;
        return Math.Max(
            minimumConsoleHeight,
            MainContent.Bounds.Height -
            toolbarHeight -
            MainContent.RowDefinitions[1].MinHeight -
            splitterHeight);
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
        UpdateEmbeddedConsoleVisibility();
        _consoleWindow = new ConsoleWindow(
            _consoleLines,
            () => { _consoleLines.Clear(); _allConsoleLines.Clear(); },
            AutoScrollCheck.IsChecked == true);
        _consoleWindow.Closed += (_, _) =>
        {
            _consoleWindow = null;
            ConsoleToggle.IsChecked = true;
            UpdateEmbeddedConsoleVisibility();
        };
        _consoleWindow.Show(this);
    }
}
