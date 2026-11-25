// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.VideoOut.Upscaling;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    private readonly LocalizedChoice[] _consoleModelChoices =
    [
        LocalizedChoice.FromKey("PS5", "Options.Console.Model.Ps5"),
        LocalizedChoice.FromKey("PS5Pro", "Options.Console.Model.Ps5Pro"),
    ];

    private readonly LocalizedChoice[] _pssrUpscalerChoices =
    [
        LocalizedChoice.FromKey("Auto", "Options.Pssr.Upscaler.Auto"),
        LocalizedChoice.FromKey("Dlss", "Options.Pssr.Upscaler.Dlss"),
        LocalizedChoice.FromKey("Basic", "Options.Pssr.Upscaler.Basic"),
    ];

    private void InitializePssrControls()
    {
        ConsoleModelBox.ItemsSource = _consoleModelChoices;
        PssrUpscalerBox.ItemsSource = _pssrUpscalerChoices;
        ConsoleModelBox.SelectionChanged += (_, _) =>
        {
            _settings.ConsoleModel = SelectedComboText(ConsoleModelBox, "PS5");
            ScheduleSettingsSave();
        };
        PssrUpscalerBox.SelectionChanged += (_, _) =>
        {
            _settings.PssrUpscaler = SelectedComboText(PssrUpscalerBox, "Auto");
            ScheduleSettingsSave();
        };
        StreamlineDownloadButton.Click += (_, _) => OpenWebPage(DlssRuntimeFiles.StreamlineReleasesUrl);
        DlssFolderButton.Click += (_, _) => OpenFolder(DlssDirectory());
        DlssRefreshButton.Click += (_, _) => UpdateDlssFilesStatus();
    }

    private void ApplyPssrSettingsToControls()
    {
        ConsoleModelBox.SelectedIndex = ChoiceIndex(_settings.ConsoleModel, _consoleModelChoices.Select(choice => choice.Value).ToArray());
        PssrUpscalerBox.SelectedIndex = ChoiceIndex(_settings.PssrUpscaler, _pssrUpscalerChoices.Select(choice => choice.Value).ToArray());
        UpdateDlssFilesStatus();
    }

    private void RefreshPssrChoices()
    {
        RefreshChoices(_consoleModelChoices);
        RefreshChoices(_pssrUpscalerChoices);
        UpdateDlssFilesStatus();
    }

    private string DlssDirectory() =>
        _emulatorExePath is { } executable && Path.GetDirectoryName(executable) is { Length: > 0 } directory
            ? DlssRuntimeFiles.DirectoryFor(directory)
            : DlssRuntimeFiles.DefaultDirectory;

    private void UpdateDlssFilesStatus()
    {
        var directory = DlssDirectory();
        var missing = DlssRuntimeFiles.Missing(directory);
        var localization = Localization.Instance;
        if (missing.Count == 0)
        {
            DlssFilesRow.Description = localization.Format(
                "Options.Pssr.DlssFiles.Found",
                string.Join(", ", DlssRuntimeFiles.Required.Select(file => file.Name)));
            return;
        }

        var lines = missing.Select(file => localization.Format("Options.Pssr.DlssFiles.MissingItem", file.Name, file.Source));
        DlssFilesRow.Description =
            localization.Format("Options.Pssr.DlssFiles.Missing", directory) + Environment.NewLine +
            string.Join(Environment.NewLine, lines) + Environment.NewLine +
            localization.Get("Options.Pssr.DlssFiles.Hint");
    }

    private void OpenWebPage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            AppendConsoleLine(Localization.Instance.Format("Status.CouldNotOpenFolder", exception.Message), WarningLineBrush);
        }
    }
}
