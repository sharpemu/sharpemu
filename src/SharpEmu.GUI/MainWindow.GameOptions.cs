// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.GUI;

/// <summary>Inline per-game settings navigation, loading and persistence.</summary>
public partial class MainWindow
{
    private static readonly string[] GameEnvironmentToggleNames =
    [
        "SHARPEMU_BTHID_UNAVAILABLE",
        "SHARPEMU_DISABLE_IMPORT_LOOP_GUARD",
        "SHARPEMU_WRITABLE_APP0",
        "SHARPEMU_VK_VALIDATION",
        "SHARPEMU_DUMP_SPIRV",
        "SHARPEMU_LOG_DIRECT_MEMORY",
        "SHARPEMU_LOG_IO",
        "SHARPEMU_LOG_NP",
        "SHARPEMU_GUEST_IMAGE_CPU_SYNC",
        "SHARPEMU_RENDERDOC",
    ];

    private readonly List<string> _gameEnvironmentPassthrough = new();
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

        GameLogLevelBox.ItemsSource = _logLevelChoices;
        GameWindowModeBox.ItemsSource = _windowModeChoices;
        GameScalingModeBox.ItemsSource = _scalingModeChoices;
        GameHdrModeBox.ItemsSource = _hdrModeChoices;

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
            navigationButtons[index].PointerEntered += (_, _) =>
            {
                if (_isGameSettingsOpen)
                {
                    SetGameOptionsNavigationIndicator(section);
                }
            };
            navigationButtons[index].GotFocus += (_, _) =>
            {
                if (_isGameSettingsOpen)
                {
                    SetGameOptionsNavigationIndicator(section);
                }
            };
        }

        GameOptionsNavHost.PointerExited += (_, _) =>
        {
            if (_isGameSettingsOpen)
            {
                SetGameOptionsNavigationIndicator(_gameOptionsSectionIndex);
            }
        };

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

        if (string.IsNullOrWhiteSpace(game.TitleId))
        {
            AppendConsoleLine(
                "[GUI][WARN] Per-game settings require a title ID, which this game does not have.",
                WarningLineBrush);
            return;
        }

        _gameSettingsTitleId = game.TitleId;
        GameOptionsOverlay.DataContext = game;
        LoadGameSettings(game.TitleId);
        SetGameOptionsSection(0, animateIndicator: false);
        GameOptionsLaunchButton.IsEnabled = !_isRunning;
        GameOptionsCopyTitleIdButton.IsEnabled =
            !string.IsNullOrWhiteSpace(game.TitleId);

        _isGameSettingsOpen = true;
        SetGameOptionsPagesSpan(coversConsoleRow: true);
        SetGameOptionsOpenClass(BackdropLayer, active: true);
        SetGameOptionsOpenClass(CarouselHost, active: true);
        SetGameOptionsOpenClass(LibrarySelectedDetails, active: true);
        SetGameOptionsOpenClass(GameOptionsOverlay, active: true);
        GameList.IsHitTestVisible = false;
        LibraryToolbar.IsHitTestVisible = false;
        GameOptionsOverlay.IsHitTestVisible = true;
        GameOptionsGeneralNav.Focus();
    }

    private void CloseGameSettings(bool restoreLibrary = true)
    {
        if (!_isGameSettingsOpen)
        {
            return;
        }

        _isGameSettingsOpen = false;
        SetGameOptionsPagesSpan(coversConsoleRow: false);
        SetGameOptionsNavigationIndicator(_gameOptionsIndicatorIndex, animate: false);
        _gameSettingsTitleId = null;
        _gameEnvironmentPassthrough.Clear();
        SetGameOptionsOpenClass(BackdropLayer, active: false);
        SetGameOptionsOpenClass(CarouselHost, active: false);
        SetGameOptionsOpenClass(LibrarySelectedDetails, active: false);
        SetGameOptionsOpenClass(GameOptionsOverlay, active: false);
        GameOptionsOverlay.IsHitTestVisible = false;
        GameOptionsOverlay.DataContext = null;
        GameList.IsHitTestVisible = true;
        LibraryToolbar.IsHitTestVisible = true;

        if (restoreLibrary && _activePageIndex == 0)
        {
            GameList.Focus();
        }
    }

    private void LoadGameSettings(string titleId)
    {
        var effective = EffectiveLaunchSettings.Resolve(
            _settings,
            PerGameSettings.Load(titleId));

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
            GameHdrModeBox.SelectedItem = FindChoice(
                _hdrModeChoices,
                effective.HdrMode,
                "Auto");

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
                toggle.IsChecked = IsEnvironmentEnabled(
                    effective.EnvironmentToggles,
                    name,
                    defaultEnabled: IsDefaultEnabledEnvironment(name));
            }
        }
        finally
        {
            _updatingGameHostDisplayOptions = false;
            _isLoadingGameSettings = false;
        }
    }

    private void PersistOpenGameSettings()
    {
        if (!_isGameSettingsOpen ||
            _isLoadingGameSettings ||
            _updatingGameHostDisplayOptions ||
            string.IsNullOrWhiteSpace(_gameSettingsTitleId))
        {
            return;
        }

        var settings = new PerGameSettings
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
            EnvironmentToggles = BuildGameEnvironmentEntries(),
            CustomEnvironmentVariables = PerGameSettings.Load(_gameSettingsTitleId)?.CustomEnvironmentVariables,
        };
        settings.RemoveInheritedValues(_settings);
        settings.Save(_gameSettingsTitleId);
    }

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
            if (IsDefaultEnabledEnvironment(name))
            {
                var globalEnabled = IsEnvironmentEnabled(
                    _settings.EnvironmentToggles,
                    name,
                    defaultEnabled: true);
                if (toggle.IsChecked == false)
                {
                    entries.Add($"{name}=0");
                }
                else if (!globalEnabled)
                {
                    entries.Add(name);
                }
            }
            else if (toggle.IsChecked == true)
            {
                entries.Add(name);
            }
        }

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

    private static bool IsEnvironmentEnabled(
        IEnumerable<string> entries,
        string name,
        bool defaultEnabled = false)
    {
        foreach (var entry in entries)
        {
            var parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (!string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return parts.Length == 1 || parts[1] != "0";
        }

        return defaultEnabled;
    }

    private static bool IsDefaultEnabledEnvironment(string name) =>
        string.Equals(
            name,
            "SHARPEMU_GUEST_IMAGE_CPU_SYNC",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownGameEnvironmentEntry(string entry)
    {
        var name = entry.Split('=', 2, StringSplitOptions.TrimEntries)[0];
        return GameEnvironmentToggleNames.Contains(name, StringComparer.OrdinalIgnoreCase);
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
        ("SHARPEMU_BTHID_UNAVAILABLE", GameEnvBthidToggle),
        ("SHARPEMU_DISABLE_IMPORT_LOOP_GUARD", GameEnvLoopGuardToggle),
        ("SHARPEMU_WRITABLE_APP0", GameEnvWritableApp0Toggle),
        ("SHARPEMU_VK_VALIDATION", GameEnvVkValidationToggle),
        ("SHARPEMU_DUMP_SPIRV", GameEnvDumpSpirvToggle),
        ("SHARPEMU_LOG_DIRECT_MEMORY", GameEnvLogDirectMemoryToggle),
        ("SHARPEMU_LOG_IO", GameEnvLogIoToggle),
        ("SHARPEMU_LOG_NP", GameEnvLogNpToggle),
        ("SHARPEMU_GUEST_IMAGE_CPU_SYNC", GameEnvGuestImageCpuSyncToggle),
        ("SHARPEMU_FORCE_SUBMIT_ORPHAN_PREAMBLES", GameEnvForceSubmitOrphanPreamblesToggle),
        ("SHARPEMU_RENDERDOC", GameEnvRenderDocToggle),
    ];

    private static void SetGameOptionsOpenClass(Control control, bool active) =>
        SetClass(control, "gameOptionsOpen", active);
}
