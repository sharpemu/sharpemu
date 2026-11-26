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
        LocalizedChoice.FromKey("Fsr", "Options.Pssr.Upscaler.Fsr"),
        LocalizedChoice.FromKey("Basic", "Options.Pssr.Upscaler.Basic"),
    ];

    private void InitializePssrControls()
    {
        ConsoleModelBox.ItemsSource = _consoleModelChoices;
        PssrUpscalerBox.ItemsSource = _pssrUpscalerChoices;
        ConsoleModelBox.SelectionChanged += (_, _) =>
        {
            _settings.ConsoleModel = SelectedComboText(ConsoleModelBox, "PS5");
            UpdatePssrAvailability();
            ScheduleSettingsSave();
        };
        PssrUpscalerBox.SelectionChanged += (_, _) =>
        {
            _settings.PssrUpscaler = SelectedComboText(PssrUpscalerBox, "Auto");
            UpdatePssrAutoChoice();
            ScheduleSettingsSave();
        };
        DlssInstallButton.Click += async (_, _) => await InstallUpscalerAsync(SuperResolutionBackend.Dlss);
        StreamlineDownloadButton.Click += (_, _) => OpenWebPage(DlssRuntimeFiles.StreamlineReleasesUrl);
        DlssFolderButton.Click += (_, _) => OpenFolder(DlssDirectory());
        DlssRefreshButton.Click += (_, _) =>
        {
            _dlssFilesCheckedAt = DateTime.Now;
            UpdateDlssFilesStatus();
        };
        FsrInstallButton.Click += async (_, _) => await InstallUpscalerAsync(SuperResolutionBackend.Fsr);
        FsrDownloadButton.Click += (_, _) => OpenWebPage(FsrRuntimeFiles.SdkReleaseUrl);
        FsrFolderButton.Click += (_, _) => OpenFolder(FsrDirectory());
        FsrRefreshButton.Click += (_, _) =>
        {
            _fsrFilesCheckedAt = DateTime.Now;
            UpdateFsrFilesStatus();
        };
        DlssInstallButton.IsVisible = FsrInstallButton.IsVisible = UpscalerInstaller.IsSupported;
        UpdateUpscalerInstallButtons();
    }

    private DateTime? _dlssFilesCheckedAt;
    private DateTime? _fsrFilesCheckedAt;

    private void ApplyPssrSettingsToControls()
    {
        ConsoleModelBox.SelectedIndex = ChoiceIndex(_settings.ConsoleModel, _consoleModelChoices.Select(choice => choice.Value).ToArray());
        PssrUpscalerBox.SelectedIndex = ChoiceIndex(_settings.PssrUpscaler, _pssrUpscalerChoices.Select(choice => choice.Value).ToArray());
        UpdatePssrAvailability();
        UpdateDlssFilesStatus();
        UpdateFsrFilesStatus();
    }

    private void RefreshPssrChoices()
    {
        RefreshChoices(_consoleModelChoices);
        RefreshChoices(_pssrUpscalerChoices);
        UpdateDlssFilesStatus();
        UpdateFsrFilesStatus();
    }

    private void UpdatePssrAutoChoice()
    {
        var localization = Localization.Instance;
        var gpu = SharpEmu.Logging.HostSystemInfo.GpuName;
        var vendor = SharpEmu.Logging.HostSystemInfo.GpuVendor;
        var backend = SuperResolutionSettings.Choose(SuperResolutionUpscaler.Auto, vendor);
        PssrAutoChoiceRow.Description = vendor == SharpEmu.Logging.HostGpuVendor.Unknown
            ? localization.Get("Options.Pssr.AutoChoice.Unknown")
            : localization.Format(backend == SuperResolutionBackend.Dlss ? "Options.Pssr.AutoChoice.Dlss" : "Options.Pssr.AutoChoice.Fsr", gpu);
        var nvidiaMissing = vendor is SharpEmu.Logging.HostGpuVendor.Amd or SharpEmu.Logging.HostGpuVendor.Intel;
        DlssVendorNotice.IsVisible = nvidiaMissing;
        if (nvidiaMissing)
        {
            DlssVendorNotice.Text = localization.Format("Options.Pssr.Dlss.NvidiaOnly", gpu);
        }
    }

    private void UpdatePssrAvailability()
    {
        var pro = string.Equals(_settings.ConsoleModel, "PS5Pro", StringComparison.OrdinalIgnoreCase);
        PssrProOnlyNotice.IsVisible = !pro;
        PssrGeneralGroup.IsEnabled = pro;
        PssrDlssGroup.IsEnabled = pro;
        PssrFsrGroup.IsEnabled = pro;
        PssrGeneralGroup.Opacity = pro ? 1 : 0.5;
        PssrDlssGroup.Opacity = pro ? 1 : 0.5;
        PssrFsrGroup.Opacity = pro ? 1 : 0.5;
    }

    private string UpscalerExecutableDirectory() =>
        _emulatorExePath is { } executable && Path.GetDirectoryName(executable) is { Length: > 0 } directory
            ? directory
            : AppContext.BaseDirectory;

    private string DlssDirectory() => DlssRuntimeFiles.DirectoryFor(UpscalerExecutableDirectory());

    private string FsrDirectory() => FsrRuntimeFiles.DirectoryFor(UpscalerExecutableDirectory());

    private void UpdateUpscalerInstallButtons()
    {
        DlssInstallButton.IsEnabled = !_isRunning && DlssRuntimeFiles.Missing(DlssDirectory()).Count != 0;
        FsrInstallButton.IsEnabled = !_isRunning && FsrRuntimeFiles.Missing(FsrDirectory()).Count != 0;
    }

    private async Task InstallUpscalerAsync(SuperResolutionBackend backend)
    {
        if (_isRunning || !UpscalerInstaller.IsSupported || Sheets.Top is UpscalerInstallDialog)
        {
            return;
        }

        var dlss = backend == SuperResolutionBackend.Dlss;
        var sdk = dlss ? $"Streamline SDK {DlssRuntimeFiles.StreamlineVersion}" : $"AMD FSR SDK {FsrRuntimeFiles.SdkVersion}";
        var destination = dlss ? DlssDirectory() : FsrDirectory();
        if (await Sheets.ShowAsync(new UpscalerInstallDialog(sdk, backend, destination)) is not true)
        {
            return;
        }

        if (dlss)
        {
            _dlssFilesCheckedAt = DateTime.Now;
            UpdateDlssFilesStatus();
        }
        else
        {
            _fsrFilesCheckedAt = DateTime.Now;
            UpdateFsrFilesStatus();
        }
    }

    private void UpdateDlssFilesStatus()
    {
        var directory = DlssDirectory();
        var missing = DlssRuntimeFiles.Missing(directory);
        var localization = Localization.Instance;
        var found = missing.Count == 0;
        DlssInstallButton.IsEnabled = !_isRunning && !found;
        var brush = found ? SuccessLineBrush : ErrorLineBrush;
        DlssFilesStatusIcon.Text = found ? "check_circle" : "error";
        DlssFilesStatusIcon.Foreground = brush;
        DlssFilesStatusTitle.Foreground = brush;
        DlssFilesStatusTitle.Text = localization.Get(found ? "Options.Pssr.DlssFiles.FoundTitle" : "Options.Pssr.DlssFiles.MissingTitle");
        DlssFilesStatusDetails.Text = found
            ? localization.Format("Options.Pssr.DlssFiles.Found", string.Join(", ", DlssRuntimeFiles.Required.Select(file => file.Name)))
            : localization.Format("Options.Pssr.DlssFiles.Missing", directory) + Environment.NewLine +
              string.Join(Environment.NewLine, missing.Select(file => localization.Format("Options.Pssr.DlssFiles.MissingItem", file.Name, file.Source)));
        UpdatePssrAutoChoice();
        DlssFilesCheckedText.IsVisible = _dlssFilesCheckedAt is not null;
        if (_dlssFilesCheckedAt is { } checkedAt)
        {
            DlssFilesCheckedText.Text = localization.Format("Options.Pssr.DlssFiles.Checked", checkedAt.ToString("T", System.Globalization.CultureInfo.CurrentCulture));
        }
    }

    private void UpdateFsrFilesStatus()
    {
        var directory = FsrDirectory();
        var missing = FsrRuntimeFiles.Missing(directory);
        var localization = Localization.Instance;
        var found = missing.Count == 0;
        FsrInstallButton.IsEnabled = !_isRunning && !found;
        var brush = found ? SuccessLineBrush : ErrorLineBrush;
        FsrFilesStatusIcon.Text = found ? "check_circle" : "error";
        FsrFilesStatusIcon.Foreground = brush;
        FsrFilesStatusTitle.Foreground = brush;
        FsrFilesStatusTitle.Text = localization.Get(found ? "Options.Pssr.FsrFiles.FoundTitle" : "Options.Pssr.FsrFiles.MissingTitle");
        FsrFilesStatusDetails.Text = directory + Environment.NewLine +
            string.Join(Environment.NewLine, found ? FsrRuntimeFiles.Required : missing);
        FsrFilesCheckedText.IsVisible = _fsrFilesCheckedAt is not null;
        if (_fsrFilesCheckedAt is { } checkedAt)
        {
            FsrFilesCheckedText.Text = localization.Format("Options.Pssr.DlssFiles.Checked", checkedAt.ToString("T", System.Globalization.CultureInfo.CurrentCulture));
        }
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
