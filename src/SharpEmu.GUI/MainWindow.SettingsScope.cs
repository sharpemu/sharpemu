// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace SharpEmu.GUI;

// The game settings overlay edits one of two layers: one game's settings, or the settings
// every game starts from. Both use the same rows. While a game is open, each row shows
// whether it follows the settings for every game (a "Global" badge) or has its own value
// (a button that puts the shared value back).
public partial class MainWindow
{
    private readonly List<ScopedRow> _scopedRows = new();
    private readonly List<(SettingRow Row, EnvironmentSettingControl Control)> _gameEnvironmentSettingRows = new();
    private EffectiveLaunchSettings _gameSettingsInherited = EffectiveLaunchSettings.Defaults;
    private bool _editingAllGames;
    private bool _returnToOptionsOnClose;
    private IReadOnlyList<string> _lastOneOffVariables = [];

    /// <summary>
    /// One row of the overlay: <paramref name="Key"/> compares a value between layers,
    /// <paramref name="Describe"/> names it for the reset tooltip, and <paramref name="Apply"/>
    /// shows it in the row's control (which then saves like any other edit).
    /// </summary>
    private sealed record ScopedRow(
        SettingRow Row,
        Func<EffectiveLaunchSettings, string> Key,
        Func<EffectiveLaunchSettings, string> Describe,
        Action<EffectiveLaunchSettings> Apply);

    private void BuildScopedRows()
    {
        LaunchVariablesButton.Click += async (_, _) => await LaunchWithVariablesAsync();

        string OnOff(bool value) => Localization.Instance.Get(value ? "Common.On" : "Common.Off");
        string Choice(IEnumerable<LocalizedChoice> choices, string value, string fallback) =>
            FindChoice(choices, value, fallback).Label;

        Add(GameStrictRow, e => e.StrictDynlibResolution, e => OnOff(e.StrictDynlibResolution),
            e => GameStrictToggle.IsChecked = e.StrictDynlibResolution);
        Add(GameLogLevelRow, e => e.LogLevel, e => Choice(_logLevelChoices, e.LogLevel, "Info"),
            e => GameLogLevelBox.SelectedItem = FindChoice(_logLevelChoices, e.LogLevel, "Info"));
        Add(GameTraceImportsRow, e => e.ImportTraceLimit, e => e.ImportTraceLimit.ToString(),
            e => GameTraceImportsBox.Value = Math.Clamp(e.ImportTraceLimit, 0, 4096));
        Add(GameLogToFileRow, e => e.LogToFile, e => OnOff(e.LogToFile),
            e => GameLogToFileToggle.IsChecked = e.LogToFile);
        Add(GameWindowModeRow, e => e.WindowMode, e => Choice(_windowModeChoices, e.WindowMode, "Windowed"),
            e => GameWindowModeBox.SelectedItem = FindChoice(_windowModeChoices, e.WindowMode, "Windowed"));
        Add(GameDisplayRow, e => e.DisplayIndex,
            e => HostDisplayOptions.SelectDisplay(_gameHostDisplays, e.DisplayIndex).ToString(),
            e => GameDisplayBox.SelectedItem = HostDisplayOptions.SelectDisplay(_gameHostDisplays, e.DisplayIndex));
        Add(GameResolutionRow, e => e.Resolution, e => e.Resolution, e => ApplyGameHostMode(e.Resolution, null));
        Add(GameRefreshRateRow, e => e.RefreshRate,
            e => e.RefreshRate == 0
                ? Localization.Instance.Get("Options.RefreshRate.Automatic")
                : $"{e.RefreshRate} Hz",
            e => ApplyGameHostMode(null, e.RefreshRate));
        Add(GameScalingModeRow, e => e.ScalingMode, e => Choice(_scalingModeChoices, e.ScalingMode, "Fit"),
            e => GameScalingModeBox.SelectedItem = FindChoice(_scalingModeChoices, e.ScalingMode, "Fit"));
        Add(GameVSyncRow, e => e.VSync, e => OnOff(e.VSync), e => GameVSyncToggle.IsChecked = e.VSync);
        Add(GameHdrModeRow, e => e.HdrMode, e => Choice(_hdrModeChoices, e.HdrMode, "Auto"),
            e => GameHdrModeBox.SelectedItem = FindChoice(_hdrModeChoices, e.HdrMode, "Auto"));
        Add(GameOverlayEnabledRow, e => e.OverlayEnabled, e => OnOff(e.OverlayEnabled),
            e => GameOverlayEnabledToggle.IsChecked = e.OverlayEnabled);
        Add(GameOverlayModeRow, e => e.OverlayMode, e => Choice(_overlayModeChoices, e.OverlayMode, "TitleBar"),
            e => GameOverlayModeBox.SelectedItem = FindChoice(_overlayModeChoices, e.OverlayMode, "TitleBar"));
        Add(GameOverlayCornerRow, e => e.OverlayCorner, e => Choice(_overlayCornerChoices, e.OverlayCorner, "TopRight"),
            e => GameOverlayCornerBox.SelectedItem = FindChoice(_overlayCornerChoices, e.OverlayCorner, "TopRight"));
        Add(GameGpuReadbacksRow, e => e.GpuReadbacks, e => Choice(_gpuReadbackChoices, e.GpuReadbacks, "Full"),
            e => GameGpuReadbacksBox.SelectedItem = FindChoice(_gpuReadbackChoices, e.GpuReadbacks, "Full"));
        Add(GameBinkPlaybackRow, e => e.BinkPlaybackMode, e => Choice(_binkPlaybackChoices, e.BinkPlaybackMode, "Guest"),
            e => GameBinkPlaybackBox.SelectedItem = FindChoice(_binkPlaybackChoices, e.BinkPlaybackMode, "Guest"));

        foreach (var (name, toggle) in GameEnvironmentToggles())
        {
            if (toggle.FindLogicalAncestorOfType<SettingRow>() is { } row)
            {
                Add(row, e => IsEnvironmentEnabled(e.EnvironmentToggles, name),
                    e => OnOff(IsEnvironmentEnabled(e.EnvironmentToggles, name)),
                    e => toggle.IsChecked = IsEnvironmentEnabled(e.EnvironmentToggles, name));
            }
        }

        foreach (var (row, control) in _gameEnvironmentSettingRows)
        {
            _scopedRows.Add(new ScopedRow(
                row,
                e => control.ValueKey(e.EnvironmentToggles),
                e => control.Describe(e.EnvironmentToggles),
                e => control.Load(e.EnvironmentToggles)));
        }

        foreach (var scoped in _scopedRows)
        {
            var target = scoped;
            target.Row.ResetRequested += (_, _) =>
            {
                target.Apply(_gameSettingsInherited);
                UpdateScopeBadges();
            };
        }

        void Add<T>(
            SettingRow row,
            Func<EffectiveLaunchSettings, T> value,
            Func<EffectiveLaunchSettings, string> describe,
            Action<EffectiveLaunchSettings> apply) =>
            _scopedRows.Add(new ScopedRow(
                row,
                e => Convert.ToString(value(e), System.Globalization.CultureInfo.InvariantCulture)?.ToUpperInvariant() ?? string.Empty,
                describe,
                apply));
    }

    // Resolution and refresh rate depend on the display, so they are set through its mode lists.
    private void ApplyGameHostMode(string? resolution, int? refreshRate)
    {
        if (GameDisplayBox.SelectedItem is not HostDisplayOption display)
        {
            return;
        }

        _updatingGameHostDisplayOptions = true;
        try
        {
            if (resolution is not null)
            {
                PopulateGameHostModes(display, resolution, SelectedGameRefreshRate());
            }
            else
            {
                PopulateGameRefreshRates(display, GameResolutionBox.SelectedItem as string, refreshRate ?? 0);
            }
        }
        finally
        {
            _updatingGameHostDisplayOptions = false;
        }

        PersistOpenGameSettings();
    }

    /// <summary>Marks every row as following the settings for every game or having its own value.</summary>
    private void UpdateScopeBadges()
    {
        if (_editingAllGames || !_isGameSettingsOpen)
        {
            foreach (var scoped in _scopedRows)
            {
                scoped.Row.ScopeState = SettingScopeState.None;
            }

            return;
        }

        var current = EffectiveLaunchSettings.Resolve(GameSettingsFromControls());
        foreach (var scoped in _scopedRows)
        {
            var inherited = scoped.Key(_gameSettingsInherited) == scoped.Key(current);
            scoped.Row.InheritedValue = scoped.Describe(_gameSettingsInherited);
            scoped.Row.ScopeState = inherited ? SettingScopeState.Inherited : SettingScopeState.Overridden;
        }
    }

    private bool _globalGameSettingsAttached;

    // Both scopes use the same controls and persistence path. Move their content into global
    // Options while it is open, then return it to the per-game overlay on leaving Options.
    private (ContentControl Source, ContentControl Target)[] GlobalGameSettingsHosts() =>
    [
        (GameOptionsGeneralPanel, GlobalGeneralSettingsHost),
        (GameOptionsLoggingPanel, GlobalLoggingSettingsHost),
        (GameOptionsRenderingPanel, OptionsGraphicsPanel),
        (GameOptionsEnvironmentPanel, OptionsEnvironmentPanel),
    ];

    private void AttachGlobalGameSettings()
    {
        if (_globalGameSettingsAttached) return;
        foreach (var (source, target) in GlobalGameSettingsHosts())
        {
            var content = source.Content;
            source.Content = null;
            target.Content = content;
            if (target is not ScrollViewer && content is StackPanel stack)
                stack.Children[0].IsVisible = false; // the global section already has a heading
        }
        _globalGameSettingsAttached = true;
        _editingAllGames = true;
        GameOptionsActions.IsVisible = false;
        LoadScopedGameSettings();
    }

    private void DetachGlobalGameSettings()
    {
        if (!_globalGameSettingsAttached) return;
        foreach (var (source, target) in GlobalGameSettingsHosts())
        {
            var content = target.Content;
            target.Content = null;
            source.Content = content;
            if (content is StackPanel stack) stack.Children[0].IsVisible = true;
        }
        _globalGameSettingsAttached = false;
        _editingAllGames = false;
        UpdateScopeBadges();
    }

    // The game's header, cover, facts, launch button and file actions mean nothing for every
    // game at once; the shared layer shows its own title instead.
    private void ApplyGameSettingsScopeChrome()
    {
        var allGames = _editingAllGames;
        BackdropLayer.IsVisible = !allGames;
        GameOptionsGameTitle.IsVisible = !allGames;
        GameOptionsAllGamesTitle.IsVisible = allGames;
        GameOptionsCover.IsVisible = !allGames;
        GameOptionsAllGamesTile.IsVisible = allGames;
        GameOptionsStats.IsVisible = !allGames;
        GameOptionsActions.IsVisible = !allGames;
        GameOptionsLaunchButton.IsVisible = !allGames;
    }

    /// <summary>
    /// Variables for this launch only: they apply after every saved setting and are kept for
    /// the next quick launch in this session, or saved as the game's own on request.
    /// </summary>
    private async Task LaunchWithVariablesAsync()
    {
        if (_isRunning || GameList.SelectedItem is not GameEntry game)
        {
            return;
        }

        var dialog = CustomEnvironmentDialog.ForLaunch(_lastOneOffVariables, game.Name);
        if (await Sheets.ShowAsync(dialog) is not CustomEnvironmentDialog.LaunchVariables result)
        {
            return;
        }

        _lastOneOffVariables = result.Entries;
        if (!result.SaveToGame)
        {
            Launch(game.Path, game.Name, game.TitleId, result.Entries);
            return;
        }

        var key = PerGameSettings.KeyFor(game.TitleId, game.Path);
        var settings = PerGameSettings.Load(key) ?? new PerGameSettings();
        settings.CustomEnvironmentVariables = CustomEnvironmentVariables.Merge(
            settings.CustomEnvironmentVariables, result.Entries).ToList();
        try
        {
            settings.SaveOrThrow(key);
            AppendConsoleLine(
                Localization.Instance.Format("Launch.Variables.Saved", result.Entries.Count, game.Name),
                DimLineBrush);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppendConsoleLine($"[SETTINGS][WARN] {exception.Message}", WarningLineBrush);
        }
    }
}
