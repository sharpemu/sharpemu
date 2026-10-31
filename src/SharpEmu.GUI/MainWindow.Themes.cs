// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace SharpEmu.GUI;

/// <summary>
/// The Theme picker, the custom theme's background and accent rows, and the glass
/// opacity and contrast sliders. Each colour row offers preset swatches, which a
/// controller can reach, and a hex field for any other colour.
/// </summary>
public partial class MainWindow
{
    private static readonly string[] BackgroundSwatches =
        ["#121212", "#000000", "#0B1020", "#0E0D14", "#0C1A1C", "#0F1A12", "#1C1410", "#1C0F16"];

    private static readonly string[] AccentSwatches =
        ["#F2F2F2", "#2E7CF6", "#7C5CFC", "#30C26B", "#FF9F0A", "#FF375F", "#40C8F4", "#FFD60A"];

    private bool _syncingThemeControls;

    private void InitializeThemeControls()
    {
        BuildSwatches(CustomBackgroundSwatches, BackgroundSwatches, hex => SetCustomThemeColour(hex, accent: false));
        BuildSwatches(CustomAccentSwatches, AccentSwatches, hex => SetCustomThemeColour(hex, accent: true));

        ThemeBox.SelectionChanged += (_, _) =>
        {
            if (_syncingThemeControls)
            {
                return;
            }

            _settings.LauncherTheme = SelectedComboText(ThemeBox, LauncherThemes.DefaultId);
            ApplyLauncherTheme();
            SyncThemeControls();
            ScheduleSettingsSave();
        };

        HookHexField(CustomBackgroundHex, accent: false);
        HookHexField(CustomAccentHex, accent: true);

        GlassOpacitySlider.ValueChanged += (_, _) =>
        {
            if (!_syncingThemeControls)
            {
                _settings.GlassOpacity = (int)Math.Round(GlassOpacitySlider.Value);
                ApplyLauncherTheme();
                ShowAppearanceValues();
                ScheduleSettingsSave();
            }
        };
        ThemeContrastSlider.ValueChanged += (_, _) =>
        {
            if (!_syncingThemeControls)
            {
                _settings.ThemeContrast = (int)Math.Round(ThemeContrastSlider.Value);
                ApplyLauncherTheme();
                ShowAppearanceValues();
                ScheduleSettingsSave();
            }
        };
    }

    private void ApplyLauncherTheme() =>
        LauncherThemes.Apply(new LauncherAppearance(
            _settings.LauncherTheme,
            _settings.CustomThemeBackground,
            _settings.CustomThemeAccent,
            _settings.GlassOpacity,
            _settings.ThemeContrast));

    private void ShowAppearanceValues()
    {
        GlassOpacitySliderValue.Text = $"{_settings.GlassOpacity}%";
        ThemeContrastSliderValue.Text = _settings.ThemeContrast > 0 ? $"+{_settings.ThemeContrast}" : $"{_settings.ThemeContrast}";
    }

    private void SyncThemeControls()
    {
        _syncingThemeControls = true;
        try
        {
            var custom = LauncherThemes.IsCustom(_settings.LauncherTheme);
            ThemeBox.SelectedIndex = custom
                ? LauncherThemes.All.Length
                : Array.IndexOf(LauncherThemes.All, LauncherThemes.Find(_settings.LauncherTheme));
            CustomBackgroundRow.IsVisible = custom;
            CustomAccentRow.IsVisible = custom;
            CustomBackgroundHex.Text = _settings.CustomThemeBackground.ToUpperInvariant();
            CustomAccentHex.Text = _settings.CustomThemeAccent.ToUpperInvariant();
            MarkSelectedSwatch(CustomBackgroundSwatches, _settings.CustomThemeBackground);
            MarkSelectedSwatch(CustomAccentSwatches, _settings.CustomThemeAccent);
            GlassOpacitySlider.Value = _settings.GlassOpacity;
            ThemeContrastSlider.Value = _settings.ThemeContrast;
            ShowAppearanceValues();
        }
        finally
        {
            _syncingThemeControls = false;
        }
    }

    private void SetCustomThemeColour(string hex, bool accent)
    {
        if (!LauncherThemes.TryParseHex(hex, out var color))
        {
            return;
        }

        var normalized = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        if (accent)
        {
            _settings.CustomThemeAccent = normalized;
        }
        else
        {
            _settings.CustomThemeBackground = normalized;
        }

        ApplyLauncherTheme();
        SyncThemeControls();
        ScheduleSettingsSave();
    }

    private void HookHexField(TextBox field, bool accent)
    {
        field.TextChanged += (_, _) =>
        {
            if (!_syncingThemeControls && LauncherThemes.TryParseHex(field.Text, out _))
            {
                SetCustomThemeColour(field.Text!, accent);
            }
        };
        // A half-typed value is put back when focus leaves the field.
        field.LostFocus += (_, _) => SyncThemeControls();
    }

    private static void BuildSwatches(Panel host, string[] colours, Action<string> pick)
    {
        foreach (var hex in colours)
        {
            var button = new Button
            {
                Classes = { "swatch" },
                Tag = hex,
                Content = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(Color.Parse(hex)) },
            };
            ToolTip.SetTip(button, hex);
            Avalonia.Automation.AutomationProperties.SetName(button, hex);
            button.Click += (_, e) =>
            {
                pick(hex);
                e.Handled = true;
            };
            host.Children.Add(button);
        }
    }

    private static void MarkSelectedSwatch(Panel host, string selected)
    {
        foreach (var child in host.Children)
        {
            if (child is Button { Tag: string hex } button)
            {
                button.Classes.Set("selected", string.Equals(hex, selected, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
