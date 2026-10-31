// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GUI;

/// <summary>Inline per-game settings navigation, loading and persistence.</summary>
public partial class MainWindow
{
    private static readonly IReadOnlyList<string> GameEnvironmentToggleNames = LaunchConfiguration.SwitchVariables;

    private readonly List<string> _gameEnvironmentPassthrough = new();

    // What the open game had stored, and what its controls showed once loaded. A save writes
    // only the values the user changed since then, so a control showing a fallback for a
    // default (a clamped trace limit, a display that is no longer attached) never turns
    // into a setting by itself.
    private PerGameSettings? _gameSettingsStored;
    private PerGameSettings? _gameSettingsBaseline;
    private IReadOnlyList<HostDisplayOption> _gameHostDisplays = [];
    private bool _isGameSettingsOpen;
    private bool _isLoadingGameSettings;
    private bool _updatingGameHostDisplayOptions;
    private int _gameOptionsIndicatorIndex;
    private int _gameOptionsSectionIndex;
    private string? _gameSettingsTitleId;

    private void WireGameOptions()
    {
        GameSettingsButton.Click += (_, _) => OpenSelectedGameSettings();
        GameCustomEnvButton.Click += async (_, _) => await EditGameCustomEnvironmentAsync();

        GameLogLevelBox.ItemsSource = _logLevelChoices;
        GameWindowModeBox.ItemsSource = _windowModeChoices;
        GameScalingModeBox.ItemsSource = _scalingModeChoices;
        GameHdrModeBox.ItemsSource = _hdrModeChoices;
        GameGpuReadbacksBox.ItemsSource = _gpuReadbackChoices;
        GameOverlayModeBox.ItemsSource = _overlayModeChoices;
        GameOverlayCornerBox.ItemsSource = _overlayCornerChoices;
        GameBinkPlaybackBox.ItemsSource = _binkPlaybackChoices;

        var navigationButtons = GameOptionsNavigationButtons();
        for (var index = 0; index < navigationButtons.Length; index++)
        {
            var section = index;
            navigationButtons[index].Click += (_, _) =>
            {
                if (section < GameOptionsSectionPanels().Length)
                {
                    SetGameOptionsSection(section);
                }
                else
                {
                    CloseGameSettings();
                }
            };
            // Moving focus onto a section selects it (console settings behaviour); the
            // selected fill follows the section, never the pointer or the focus ring.
            navigationButtons[index].GotFocus += (_, e) =>
            {
                if (_isGameSettingsOpen &&
                    e.NavigationMethod != NavigationMethod.Pointer &&
                    section < GameOptionsSectionPanels().Length &&
                    section != _gameOptionsSectionIndex)
                {
                    SetGameOptionsSection(section);
                }
            };
        }

        GameOptionsLaunchButton.Click += (_, _) =>
        {
            CloseGameSettings();
            LaunchSelected();
        };
        GameOptionsCloseButton.Click += (_, _) => CloseGameSettings();
        GameOptionsOpenFolderButton.Click += (_, _) => OpenSelectedGameFolder();
        GameOptionsCopyPathButton.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.Path);
        GameOptionsCopyTitleIdButton.Click += async (_, _) =>
            await CopyToClipboardAsync((GameList.SelectedItem as GameEntry)?.TitleId);
        GameOptionsRemoveButton.Click += (_, _) =>
        {
            CloseGameSettings();
            RemoveSelectedFromLibrary();
        };

        GameStrictToggle.IsCheckedChanged += (_, _) => PersistOpenGameSettings();
        GameLogLevelBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        GameTraceImportsBox.ValueChanged += (_, _) => PersistOpenGameSettings();
        GameLogToFileToggle.IsCheckedChanged += (_, _) => PersistOpenGameSettings();
        GameWindowModeBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        GameDisplayBox.SelectionChanged += (_, _) => OnGameHostDisplayChanged();
        GameResolutionBox.SelectionChanged += (_, _) => OnGameHostResolutionChanged();
        GameRefreshRateBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        GameScalingModeBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        GameVSyncToggle.IsCheckedChanged += (_, _) => PersistOpenGameSettings();
        GameHdrModeBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        GameGpuReadbacksBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        GameOverlayEnabledToggle.IsCheckedChanged += (_, _) =>
        {
            UpdateGameOverlayRows();
            PersistOpenGameSettings();
        };
        GameOverlayModeBox.SelectionChanged += (_, _) =>
        {
            UpdateGameOverlayRows();
            PersistOpenGameSettings();
        };
        GameOverlayCornerBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        GameBinkPlaybackBox.SelectionChanged += (_, _) => PersistOpenGameSettings();
        foreach (var (_, toggle) in GameEnvironmentToggles())
        {
            toggle.IsCheckedChanged += (_, _) => PersistOpenGameSettings();
        }

        SetGameOptionsSection(0, animateIndicator: false);
    }

    private void OpenSelectedGameSettings()
    {
        if (GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        _returnToOptionsOnClose = false;
        OpenGameSettingsOverlay(game);
    }

    /// <summary>Opens the overlay on one game's settings, or on the settings for every game when <paramref name="game"/> is null.</summary>
    private void OpenGameSettingsOverlay(GameEntry? game)
    {
        _editingAllGames = game is null;
        _gameSettingsTitleId = game is null ? null : PerGameSettings.KeyFor(game.TitleId, game.Path);
        GameOptionsOverlay.DataContext = game;
        if (game is null)
        {
            GameOptionsBackdrop.Clear();
        }
        else
        {
            GameOptionsBackdrop.Show(game);
        }

        ApplyGameSettingsScopeChrome();
        _isGameSettingsOpen = true;
        LoadScopedGameSettings();
        SetGameOptionsSection(0, animateIndicator: false);
        GameOptionsLaunchButton.IsEnabled = !_isRunning;
        GameOptionsCopyTitleIdButton.IsEnabled =
            !string.IsNullOrWhiteSpace(game?.TitleId);

        UpdateEmbeddedConsoleVisibility();
        SetGameOptionsPagesSpan(coversConsoleRow: true);
        SetGameOptionsOpenClass(BackdropLayer, active: true);
        SetGameOptionsOpenClass(CarouselHost, active: true);
        SetGameOptionsOpenClass(LibrarySelectedDetails, active: true);
        SetGameOptionsOpenClass(GameOptionsOverlay, active: true);
        GameList.IsHitTestVisible = false;
        LibraryToolbar.IsHitTestVisible = false;
        GameOptionsOverlay.IsHitTestVisible = true;
        if (game is null)
        {
            GameOptionsGeneralNav.Focus(_gamepadConnected ? NavigationMethod.Directional : NavigationMethod.Unspecified);
        }
        else
        {
            GameOptionsLaunchButton.Focus(_gamepadConnected ? NavigationMethod.Directional : NavigationMethod.Unspecified);
        }
    }

    private void CloseGameSettings(bool restoreLibrary = true)
    {
        if (!_isGameSettingsOpen)
        {
            return;
        }

        _isGameSettingsOpen = false;
        UpdateEmbeddedConsoleVisibility();
        SetGameOptionsPagesSpan(coversConsoleRow: false);
        SetGameOptionsNavigationIndicator(_gameOptionsIndicatorIndex, animate: false);
        _gameSettingsTitleId = null;
        _gameSettingsStored = null;
        _gameSettingsBaseline = null;
        _gameEnvironmentPassthrough.Clear();
        SetGameOptionsOpenClass(BackdropLayer, active: false);
        SetGameOptionsOpenClass(CarouselHost, active: false);
        SetGameOptionsOpenClass(LibrarySelectedDetails, active: false);
        SetGameOptionsOpenClass(GameOptionsOverlay, active: false);
        GameOptionsOverlay.IsHitTestVisible = false;
        GameOptionsOverlay.DataContext = null;
        GameList.IsHitTestVisible = true;
        LibraryToolbar.IsHitTestVisible = true;

        var returnToOptions = _returnToOptionsOnClose;
        _returnToOptionsOnClose = false;
        _editingAllGames = false;
        BackdropLayer.IsVisible = true;
        UpdateScopeBadges();
        if (restoreLibrary && returnToOptions)
        {
            SetActivePage(1);
            OptionsGeneralNav.Focus();
        }
        else if (restoreLibrary && _activePageIndex == 0)
        {
            GameList.Focus();
        }
    }

    // One game's file lies over the settings for every game; those lie over the built-in defaults.
    private void LoadScopedGameSettings()
    {
        var stored = _editingAllGames ? PerGameSettings.LoadGlobal() : PerGameSettings.Load(_gameSettingsTitleId);
        _gameSettingsInherited = _editingAllGames
            ? EffectiveLaunchSettings.Defaults
            : PerGameSettings.GlobalEffective();
        UpdateGameCustomEnvironmentSummary();
        var effective = EffectiveLaunchSettings.Over(_gameSettingsInherited, stored);

        _isLoadingGameSettings = true;
        _updatingGameHostDisplayOptions = true;
        try
        {
            GameStrictToggle.IsChecked = effective.StrictDynlibResolution;
            GameLogLevelBox.SelectedItem = FindChoice(
                _logLevelChoices,
                effective.LogLevel,
                "Info");
            GameTraceImportsBox.Value = Math.Clamp(effective.ImportTraceLimit, 0, 4096);
            GameLogToFileToggle.IsChecked = effective.LogToFile;
            GameWindowModeBox.SelectedItem = FindChoice(
                _windowModeChoices,
                effective.WindowMode,
                "Windowed");
            GameScalingModeBox.SelectedItem = FindChoice(
                _scalingModeChoices,
                effective.ScalingMode,
                "Fit");
            GameVSyncToggle.IsChecked = effective.VSync;
            GameOverlayEnabledToggle.IsChecked = effective.OverlayEnabled;
            GameOverlayModeBox.SelectedItem = FindChoice(_overlayModeChoices, effective.OverlayMode, "TitleBar");
            UpdateGameOverlayRows();
            GameOverlayCornerBox.SelectedItem = FindChoice(_overlayCornerChoices, effective.OverlayCorner, "TopRight");
            GameHdrModeBox.SelectedItem = FindChoice(
                _hdrModeChoices,
                effective.HdrMode,
                "Auto");
            GameGpuReadbacksBox.SelectedItem = FindChoice(
                _gpuReadbackChoices,
                effective.GpuReadbacks,
                "Full");
            GameBinkPlaybackBox.SelectedItem = FindChoice(_binkPlaybackChoices, effective.BinkPlaybackMode, "Guest");

            _gameHostDisplays = HostDisplayOptions.BuildDisplays(
                HostDisplayCatalog.Query(),
                effective.DisplayIndex);
            GameDisplayBox.ItemsSource = _gameHostDisplays;
            var display = HostDisplayOptions.SelectDisplay(
                _gameHostDisplays,
                effective.DisplayIndex);
            GameDisplayBox.SelectedItem = display;
            PopulateGameHostModes(
                display,
                effective.Resolution,
                effective.RefreshRate);

            _gameEnvironmentPassthrough.Clear();
            foreach (var entry in effective.EnvironmentToggles)
            {
                if (!IsKnownGameEnvironmentEntry(entry))
                {
                    _gameEnvironmentPassthrough.Add(entry);
                }
            }

            foreach (var (name, toggle) in GameEnvironmentToggles())
            {
                toggle.IsChecked = IsEnvironmentEnabled(effective.EnvironmentToggles, name);
            }

            LoadGameEnvironmentSettings(effective.EnvironmentToggles);
        }
        finally
        {
            _updatingGameHostDisplayOptions = false;
            _isLoadingGameSettings = false;
        }

        _gameSettingsStored = stored;
        _gameSettingsBaseline = GameSettingsFromControls();
        UpdateScopeBadges();
    }

    // The mode only matters while the overlay is on, and the corner only for a panel drawn
    // in the game (not the title bar).
    private void UpdateGameOverlayRows()
    {
        var on = GameOverlayEnabledToggle.IsChecked == true;
        GameOverlayModeRow.IsVisible = on;
        GameOverlayCornerRow.IsVisible = on && SelectedComboText(GameOverlayModeBox, "TitleBar") != "TitleBar";
    }

    // The game's own variables, edited in a sheet over the game settings and saved on Save.
    // With the settings for every game open, the variables for every game instead.
    private async Task EditGameCustomEnvironmentAsync()
    {
        if (_editingAllGames)
        {
            await EditGlobalCustomEnvironmentAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(_gameSettingsTitleId))
        {
            return;
        }

        var titleId = _gameSettingsTitleId;
        var dialog = new CustomEnvironmentDialog(PerGameSettings.Load(titleId)?.CustomEnvironmentVariables ?? []);
        if (await Sheets.ShowAsync(dialog) is not IReadOnlyList<string> entries)
        {
            return;
        }

        var settings = PerGameSettings.Load(titleId) ?? new PerGameSettings();
        settings.CustomEnvironmentVariables = entries.Count == 0 ? null : entries.ToList();
        settings.RemoveInheritedValues(PerGameSettings.GlobalEffective());
        try
        {
            settings.SaveOrThrow(titleId);
            UpdateGameCustomEnvironmentSummary();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendConsoleLine($"[SETTINGS][WARN] {exception.Message}", WarningLineBrush);
        }
    }

    private void UpdateGameCustomEnvironmentSummary()
    {
        var count = _editingAllGames
            ? GlobalCustomEnvironmentSettings.Load().Count
            : PerGameSettings.Load(_gameSettingsTitleId)?.CustomEnvironmentVariables?.Count ?? 0;
        GameCustomEnvRow.Label = Localization.Instance.Get(
            _editingAllGames ? "Options.GlobalCustomEnv.Label" : "Setting.CustomEnv.Label");
        GameCustomEnvRow.Description = Localization.Instance.Get(
            _editingAllGames ? "Options.GlobalCustomEnv.Desc" : "Launch.CustomEnv.Desc");
        GameCustomEnvSummary.Text = count == 0
            ? Localization.Instance.Get("Common.Edit")
            : string.Format(Localization.Instance.Get("Setting.CustomEnv.Count"), count);
    }

    private void PersistOpenGameSettings()
    {
        if ((!_isGameSettingsOpen && !_globalGameSettingsAttached) ||
            _isLoadingGameSettings ||
            _updatingGameHostDisplayOptions ||
            (!_editingAllGames && string.IsNullOrWhiteSpace(_gameSettingsTitleId)))
        {
            return;
        }

        var current = GameSettingsFromControls();
        var baseline = _gameSettingsBaseline ?? current;
        var stored = _gameSettingsStored ?? new PerGameSettings();
        T? Pick<T>(T? now, T? before, T? kept) => EqualityComparer<T?>.Default.Equals(now, before) ? kept : now;
        var settings = new PerGameSettings
        {
            LogLevel = Pick(current.LogLevel, baseline.LogLevel, stored.LogLevel),
            ImportTraceLimit = Pick(current.ImportTraceLimit, baseline.ImportTraceLimit, stored.ImportTraceLimit),
            StrictDynlibResolution = Pick(current.StrictDynlibResolution, baseline.StrictDynlibResolution, stored.StrictDynlibResolution),
            LogToFile = Pick(current.LogToFile, baseline.LogToFile, stored.LogToFile),
            WindowMode = Pick(current.WindowMode, baseline.WindowMode, stored.WindowMode),
            Resolution = Pick(current.Resolution, baseline.Resolution, stored.Resolution),
            DisplayIndex = Pick(current.DisplayIndex, baseline.DisplayIndex, stored.DisplayIndex),
            RefreshRate = Pick(current.RefreshRate, baseline.RefreshRate, stored.RefreshRate),
            ScalingMode = Pick(current.ScalingMode, baseline.ScalingMode, stored.ScalingMode),
            VSync = Pick(current.VSync, baseline.VSync, stored.VSync),
            HdrMode = Pick(current.HdrMode, baseline.HdrMode, stored.HdrMode),
            GpuReadbacks = Pick(current.GpuReadbacks, baseline.GpuReadbacks, stored.GpuReadbacks),
            OverlayEnabled = Pick(current.OverlayEnabled, baseline.OverlayEnabled, stored.OverlayEnabled),
            OverlayMode = Pick(current.OverlayMode, baseline.OverlayMode, stored.OverlayMode),
            OverlayCorner = Pick(current.OverlayCorner, baseline.OverlayCorner, stored.OverlayCorner),
            BinkPlaybackMode = Pick(current.BinkPlaybackMode, baseline.BinkPlaybackMode, stored.BinkPlaybackMode),
            // An older file's whole list becomes the changes from the list it inherits.
            EnvironmentChanges = EnvironmentOverrides.PickChanged(
                current.EnvironmentToggles ?? [],
                baseline.EnvironmentToggles ?? [],
                stored.EnvironmentToggles is { } legacy
                    ? EnvironmentOverrides.Between(
                        _gameSettingsInherited.EnvironmentToggles,
                        EnvironmentOverrides.Merge(legacy, stored.EnvironmentChanges))
                    : stored.EnvironmentChanges),
            CustomEnvironmentVariables = _editingAllGames
                ? null
                : PerGameSettings.Load(_gameSettingsTitleId)?.CustomEnvironmentVariables,
        };
        // A value equal to the layer below is dropped, so the game follows later changes to
        // the settings for every game.
        settings.RemoveInheritedValues(_gameSettingsInherited);
        try
        {
            if (_editingAllGames)
            {
                settings.SaveGlobalOrThrow();
            }
            else
            {
                settings.SaveOrThrow(_gameSettingsTitleId!);
            }

            _gameSettingsStored = settings;
            _gameSettingsBaseline = current;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendConsoleLine($"[SETTINGS][WARN] {exception.Message}", WarningLineBrush);
        }

        UpdateScopeBadges();
    }

    // Every game setting as the overlay's controls show it, with the environment switches as
    // the full list the controls describe.
    private PerGameSettings GameSettingsFromControls() =>
        new()
        {
            LogLevel = SelectedComboText(GameLogLevelBox, "Info"),
            ImportTraceLimit = (int)(GameTraceImportsBox.Value ?? 0),
            StrictDynlibResolution = GameStrictToggle.IsChecked == true,
            LogToFile = GameLogToFileToggle.IsChecked == true,
            WindowMode = SelectedComboText(GameWindowModeBox, "Windowed"),
            Resolution = SelectedComboText(GameResolutionBox, "1920x1080"),
            DisplayIndex = GameDisplayBox.SelectedItem is HostDisplayOption display
                ? display.Index
                : 0,
            RefreshRate = SelectedGameRefreshRate(),
            ScalingMode = SelectedComboText(GameScalingModeBox, "Fit"),
            VSync = GameVSyncToggle.IsChecked == true,
            HdrMode = SelectedComboText(GameHdrModeBox, "Auto"),
            GpuReadbacks = SelectedComboText(GameGpuReadbacksBox, "Full"),
            OverlayEnabled = GameOverlayEnabledToggle.IsChecked == true,
            OverlayMode = SelectedComboText(GameOverlayModeBox, "TitleBar"),
            OverlayCorner = SelectedComboText(GameOverlayCornerBox, "TopRight"),
            BinkPlaybackMode = SelectedComboText(GameBinkPlaybackBox, "Guest"),
            EnvironmentToggles = BuildGameEnvironmentEntries(),
        };

    private void OnGameHostDisplayChanged()
    {
        if (_isLoadingGameSettings ||
            _updatingGameHostDisplayOptions ||
            GameDisplayBox.SelectedItem is not HostDisplayOption display)
        {
            return;
        }

        _updatingGameHostDisplayOptions = true;
        try
        {
            PopulateGameHostModes(
                display,
                GameResolutionBox.SelectedItem as string ?? "1920x1080",
                SelectedGameRefreshRate());
        }
        finally
        {
            _updatingGameHostDisplayOptions = false;
        }

        PersistOpenGameSettings();
    }

    private void OnGameHostResolutionChanged()
    {
        if (_isLoadingGameSettings ||
            _updatingGameHostDisplayOptions ||
            GameDisplayBox.SelectedItem is not HostDisplayOption display)
        {
            return;
        }

        _updatingGameHostDisplayOptions = true;
        try
        {
            PopulateGameRefreshRates(
                display,
                GameResolutionBox.SelectedItem as string,
                SelectedGameRefreshRate());
        }
        finally
        {
            _updatingGameHostDisplayOptions = false;
        }

        PersistOpenGameSettings();
    }

    private void PopulateGameHostModes(
        HostDisplayOption display,
        string selectedResolution,
        int selectedRefreshRate)
    {
        var resolutions = HostDisplayOptions.BuildResolutions(display, selectedResolution);
        GameResolutionBox.ItemsSource = resolutions;
        GameResolutionBox.SelectedItem = resolutions.FirstOrDefault(resolution =>
            string.Equals(resolution, selectedResolution, StringComparison.OrdinalIgnoreCase))
            ?? resolutions[0];
        PopulateGameRefreshRates(
            display,
            GameResolutionBox.SelectedItem as string,
            selectedRefreshRate);
    }

    private void PopulateGameRefreshRates(
        HostDisplayOption display,
        string? resolution,
        int selectedRefreshRate)
    {
        var refreshRates = HostDisplayOptions.BuildRefreshRates(
            display,
            resolution,
            selectedRefreshRate,
            Localization.Instance.Get("Options.RefreshRate.Automatic"));
        GameRefreshRateBox.ItemsSource = refreshRates;
        GameRefreshRateBox.SelectedItem = refreshRates.FirstOrDefault(
            refreshRate => refreshRate.Value == selectedRefreshRate) ?? refreshRates[0];
    }

    private int SelectedGameRefreshRate() =>
        GameRefreshRateBox.SelectedItem is HostRefreshRateOption refreshRate
            ? refreshRate.Value
            : 0;

    private List<string> BuildGameEnvironmentEntries()
    {
        var entries = new List<string>(_gameEnvironmentPassthrough);
        foreach (var (name, toggle) in GameEnvironmentToggles())
        {
            if (name == StrictComputeSettings.VariableName)
                StrictComputeSettings.SetEnabled(entries, toggle.IsChecked == true);
            else if (name == RayTracingSkipSettings.VariableName)
                RayTracingSkipSettings.SetEnabled(entries, toggle.IsChecked == true);
            else if (toggle.IsChecked == true)
            {
                entries.Add(name);
            }
        }

        StoreGameEnvironmentSettings(entries);
        return entries;
    }

    private static LocalizedChoice FindChoice(
        IEnumerable<LocalizedChoice> choices,
        string value,
        string fallback) =>
        choices.FirstOrDefault(choice =>
            string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase))
        ?? choices.First(choice =>
            string.Equals(choice.Value, fallback, StringComparison.OrdinalIgnoreCase));

    private static bool IsEnvironmentEnabled(IEnumerable<string> entries, string name)
    {
        if (name == StrictComputeSettings.VariableName) return StrictComputeSettings.IsEnabled(entries);
        if (name == RayTracingSkipSettings.VariableName) return RayTracingSkipSettings.IsEnabled(entries);
        foreach (var entry in entries)
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (!string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return parts.Length == 1 || parts[1] != "0";
        }

        return false;
    }

    private static bool IsKnownGameEnvironmentEntry(string entry)
    {
        var name = entry.Split('=', 2, StringSplitOptions.TrimEntries)[0];
        return GameEnvironmentToggleNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            EnvironmentSettings.IsManagedEntry(entry);
    }

    private void SetGameOptionsSection(int section, bool animateIndicator = true)
    {
        var buttons = GameOptionsSectionButtons();
        var panels = GameOptionsSectionPanels();
        section = Math.Clamp(section, 0, buttons.Length - 1);
        _gameOptionsSectionIndex = section;
        SetGameOptionsNavigationIndicator(section, animateIndicator);

        for (var index = 0; index < buttons.Length; index++)
        {
            var active = index == section;
            SetActiveClass(buttons[index], active);
            SetOptionsPanelInteraction(panels[index], active);
        }

        SetActiveClass(GameOptionsBackNav, active: false);
    }

    private void SetGameOptionsNavigationIndicator(int section, bool animate = true)
    {
        var buttons = GameOptionsNavigationButtons();
        _gameOptionsIndicatorIndex = Math.Clamp(section, 0, buttons.Length - 1);
        var button = buttons[_gameOptionsIndicatorIndex];
        MoveNavigationIndicator(
            GameOptionsNavIndicator,
            GameOptionsNavHost,
            button,
            _gameOptionsIndicatorIndex,
            animate);
    }

    private void SetGameOptionsPagesSpan(bool coversConsoleRow)
    {
        Grid.SetRowSpan(PagesHost, coversConsoleRow ? 3 : 1);
    }

    private Button[] GameOptionsNavigationButtons() =>
    [
        GameOptionsGeneralNav,
        GameOptionsLoggingNav,
        GameOptionsRenderingNav,
        GameOptionsEnvironmentNav,
        GameOptionsBackNav,
    ];

    private Button[] GameOptionsSectionButtons() =>
    [
        GameOptionsGeneralNav,
        GameOptionsLoggingNav,
        GameOptionsRenderingNav,
        GameOptionsEnvironmentNav,
    ];

    private Control[] GameOptionsSectionPanels() =>
    [
        GameOptionsGeneralPanel,
        GameOptionsLoggingPanel,
        GameOptionsRenderingPanel,
        GameOptionsEnvironmentPanel,
    ];

    private (string Name, ToggleSwitch Toggle)[] GameEnvironmentToggles() =>
    [
        ("SHARPEMU_PROFILE_PERFORMANCE", GamePerformanceProfileToggle),
        ("SHARPEMU_PROFILE_PERFORMANCE_FRAME_TRACE", GamePerformanceFrameTraceToggle),
        (StrictComputeSettings.VariableName, GameStrictComputeToggle),
        ("SHARPEMU_BTHID_UNAVAILABLE", GameEnvBthidToggle),
        ("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", GameEnvLoopGuardToggle),
        ("SHARPEMU_WRITABLE_APP0", GameEnvWritableApp0Toggle),
        ("SHARPEMU_VK_VALIDATION", GameEnvVkValidationToggle),
        ("SHARPEMU_DUMP_SPIRV", GameEnvDumpSpirvToggle),
        ("SHARPEMU_SKIP_RT", GameEnvSkipRtToggle),
        ("SHARPEMU_LOG_DIRECT_MEMORY", GameEnvLogDirectMemoryToggle),
        ("SHARPEMU_LOG_IO", GameEnvLogIoToggle),
        ("SHARPEMU_LOG_NP", GameEnvLogNpToggle),
        ("SHARPEMU_RENDERDOC", GameEnvRenderDocToggle),
        ("SHARPEMU_VK_DISABLE_IMPLICITS", GameEnvDisableVkImplicitLayersToggle),
        ("SHARPEMU_CRASH_CAPTURE", GameCrashDumpToggle),
    ];

    private static void SetGameOptionsOpenClass(Control control, bool active) =>
        SetClass(control, "gameOptionsOpen", active);
}
