// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.Controls.Templates;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

/// <summary>
/// Options that only some platforms have, and the emulator switches the launcher exposes from
/// <see cref="EnvironmentSettings"/>: one row per switch, built in code with the same row, toggle
/// and combo styles as the hand-written rows, in per-game options.
/// </summary>
public partial class MainWindow
{
    private readonly List<EnvironmentSettingControl> _gameEnvironmentSettings = new();
    private DispatcherTimer? _clearShaderCacheTimer;

    /// <summary>One emulator switch and the control that edits it.</summary>
    private sealed class EnvironmentSettingControl(
        EnvironmentSetting setting,
        ToggleSwitch? toggle,
        ComboBox? combo,
        List<LocalizedChoice>? choices)
    {
        public EnvironmentSetting Setting { get; } = setting;

        public List<LocalizedChoice>? Choices => choices;

        public void Load(IEnumerable<string> entries)
        {
            if (toggle is not null)
            {
                toggle.IsChecked = EnvironmentSettings.IsEnabled(entries, Setting);
                return;
            }

            var value = EnvironmentSettings.GetChoice(entries, Setting);
            var choice = choices!.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
            if (choice is null)
            {
                // A value written by hand or by a newer build still shows, and survives a save.
                choice = LocalizedChoice.Literal(value, value);
                choices!.Add(choice);
                combo!.ItemsSource = choices.ToArray();
            }

            combo!.SelectedItem = choice;
        }

        public void Store(List<string> entries)
        {
            if (toggle is not null)
            {
                EnvironmentSettings.SetEnabled(entries, Setting, toggle.IsChecked == true);
            }
            else if (combo!.SelectedItem is LocalizedChoice choice)
            {
                EnvironmentSettings.SetChoice(entries, Setting, choice.Value);
            }
        }
    }

    /// <summary>
    /// Hides rows whose emulator-side feature does not exist on this platform: crash dumps are
    /// written by a Windows x64 handler only.
    /// </summary>
    private void HideUnsupportedPlatformOptions()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            GameCrashDumpRow.IsVisible = false;
        }
    }

    private void BuildEnvironmentSettingRows()
    {
        var gameHosts = new Dictionary<EnvironmentSettingGroup, Panel>
        {
            [EnvironmentSettingGroup.Performance] = GamePerformanceHost,
            [EnvironmentSettingGroup.Compatibility] = GameCompatibilityHost,
            [EnvironmentSettingGroup.Audio] = GameAudioHost,
            [EnvironmentSettingGroup.Debug] = GameDebugExtraHost,
        };

        foreach (var setting in EnvironmentSettings.All)
        {
            var perGame = CreateEnvironmentSettingRow(setting, "Game", PersistOpenGameSettings, out var gameControl);
            gameHosts[setting.Group].Children.Add(perGame);
            _gameEnvironmentSettings.Add(gameControl);
        }

        OpenShaderCacheButton.Click += (_, _) => OpenFolder(EmulatorShaderCacheRoot());
        ClearShaderCacheButton.Click += (_, _) => OnClearShaderCacheClicked();
    }

    private SettingRow CreateEnvironmentSettingRow(
        EnvironmentSetting setting,
        string namePrefix,
        Action changed,
        out EnvironmentSettingControl control)
    {
        var row = new SettingRow { Name = $"{namePrefix}{setting.Id}Row" };
        row.Classes.Add("optionRow");
        row.Bind(SettingRow.LabelProperty, LocalizedBinding($"Setting.{setting.Id}.Label"));
        row.Bind(SettingRow.DescriptionProperty, LocalizedBinding($"Setting.{setting.Id}.Desc"));

        if (setting.Kind == EnvironmentSettingKind.Toggle)
        {
            var toggle = new ToggleSwitch { Name = $"{namePrefix}{setting.Id}Toggle" };
            toggle.Classes.Add("optionToggle");
            toggle.IsCheckedChanged += (_, _) => changed();
            row.Content = toggle;
            control = new EnvironmentSettingControl(setting, toggle, null, null);
            return row;
        }

        var choices = setting.Choices!
            .Select(choice => choice.Label is { } label
                ? LocalizedChoice.Literal(choice.Value, label)
                : LocalizedChoice.FromKey(choice.Value, $"Setting.{setting.Id}.{choice.Value}"))
            .ToList();
        foreach (var choice in choices)
        {
            choice.Refresh(Localization.Instance);
        }

        var combo = new ComboBox
        {
            Name = $"{namePrefix}{setting.Id}Box",
            ItemsSource = choices.ToArray(),
            ItemTemplate = (IDataTemplate)this.FindResource("LocalizedChoiceTemplate")!,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        combo.Classes.Add("optionValue");
        combo.SelectionChanged += (_, _) => changed();
        row.Content = combo;
        control = new EnvironmentSettingControl(setting, null, combo, choices);
        return row;
    }

    private static Binding LocalizedBinding(string key) =>
        new($"[{key}]") { Source = Localization.Instance };

    private void RefreshEnvironmentSettingChoices()
    {
        foreach (var control in _gameEnvironmentSettings)
        {
            if (control.Choices is { } choices)
            {
                RefreshChoices(choices);
            }
        }
    }

    private void LoadGameEnvironmentSettings(IReadOnlyList<string> entries)
    {
        foreach (var control in _gameEnvironmentSettings)
        {
            control.Load(entries);
        }
    }

    private void StoreGameEnvironmentSettings(List<string> entries)
    {
        foreach (var control in _gameEnvironmentSettings)
        {
            control.Store(entries);
        }
    }

    /// <summary>
    /// The Vulkan pipeline caches and shader prewarm lists, one folder per title under
    /// user/pipeline_cache in the user data root (SHARPEMU_USER_DIR, the portable folder next
    /// to the executable, the XDG data directory on Linux or %APPDATA% on Windows).
    /// </summary>
    private static string EmulatorShaderCacheRoot() =>
        Path.Combine(SharpEmu.Libs.SharpEmuPaths.DataRoot, "user", "pipeline_cache");

    internal const string ShaderAnalysisCacheVariable = "SHARPEMU_SHADER_ANALYSIS_CACHE_PATH";

    /// <summary>
    /// The pre-data-root pipeline cache file in LocalApplicationData. A launch imports it when
    /// the title's cache is missing, so "Clear" removes it too.
    /// </summary>
    internal static string? LegacyPipelineCacheFile()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localData) ? null : Path.Combine(localData, "SharpEmu", "vulkan-pipeline-cache.bin");
    }

    /// <summary>
    /// The shader compiler's analysis cache: LocalApplicationData/SharpEmu/shader-analysis
    /// (%LOCALAPPDATA% on Windows, ~/.local/share on Linux) unless
    /// SHARPEMU_SHADER_ANALYSIS_CACHE_PATH moves it, or turns it off with "0".
    /// </summary>
    internal static string? ShaderAnalysisCacheDirectory(Func<string, string?> variable)
    {
        var configured = variable(ShaderAnalysisCacheVariable);
        if (configured is not null)
        {
            return configured == "0" || string.IsNullOrWhiteSpace(configured) ? null : configured;
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(localData) ? null : Path.Combine(localData, "SharpEmu", "shader-analysis");
    }

    // Only the cache's own files go: the directory may be one the user pointed it at.
    internal static void ClearShaderAnalysisCache(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var pattern in (string[])["*.graph", "*.graph.*.tmp"])
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Delete(file);
            }
        }
    }

    // The analysis cache path a launch would see: the global custom variables win over the
    // launcher's own environment, as they do in LaunchConfiguration.
    private static string? LaunchVariable(string name)
    {
        string? value = null;
        foreach (var entry in GlobalCustomEnvironmentSettings.Load())
        {
            if (CustomEnvironmentVariables.TryParseEntry(entry, out var entryName, out var entryValue) &&
                string.Equals(entryName, name, StringComparison.Ordinal))
            {
                value = entryValue;
            }
        }

        return value ?? Environment.GetEnvironmentVariable(name);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open",
                Arguments = $"\"{path}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            AppendConsoleLine(
                Localization.Instance.Format("Status.CouldNotOpenFolder", ex.Message),
                WarningLineBrush);
        }
    }

    // The first click arms the button and the second, within a few seconds, clears: the cache
    // can take minutes to build again.
    private void OnClearShaderCacheClicked()
    {
        if (_isRunning)
        {
            AppendConsoleLine(Localization.Instance.Get("Setting.ShaderCacheFolder.Running"), WarningLineBrush);
            return;
        }

        if (_clearShaderCacheTimer is null)
        {
            SetClearShaderCacheLabel("Setting.ShaderCacheFolder.Confirm");
            _clearShaderCacheTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _clearShaderCacheTimer.Tick += (_, _) => DisarmClearShaderCache();
            _clearShaderCacheTimer.Start();
            return;
        }

        DisarmClearShaderCache();
        var root = EmulatorShaderCacheRoot();
        var analysis = ShaderAnalysisCacheDirectory(LaunchVariable);
        var legacy = LegacyPipelineCacheFile();
        _ = Task.Run(() =>
        {
            string message;
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                if (analysis is not null) ClearShaderAnalysisCache(analysis);
                if (legacy is not null && File.Exists(legacy)) File.Delete(legacy);

                message = Localization.Instance.Get("Setting.ShaderCacheFolder.Cleared");
            }
            catch (Exception ex)
            {
                message = Localization.Instance.Format("Setting.ShaderCacheFolder.ClearFailed", ex.Message);
            }

            Dispatcher.UIThread.Post(() => AppendConsoleLine(message, DimLineBrush));
        });
    }

    private void DisarmClearShaderCache()
    {
        _clearShaderCacheTimer?.Stop();
        _clearShaderCacheTimer = null;
        SetClearShaderCacheLabel("Setting.ShaderCacheFolder.Clear");
    }

    private void SetClearShaderCacheLabel(string key) =>
        ClearShaderCacheButtonLabel.Bind(TextBlock.TextProperty, LocalizedBinding(key));
}
