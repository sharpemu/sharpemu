// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.Windows;

namespace SharpEmu.GUI;

public partial class MainWindow
{
    // Set by "Play without sandbox" for the next Launch only; the setting itself is untouched.
    private bool _launchWithoutSandboxOnce;

    // True while the toggle is moved by code (loading settings, cancelling the confirmation), not by the user.
    private bool _sandboxToggleSyncing;

    private void SetSandboxToggle(bool value)
    {
        _sandboxToggleSyncing = true;
        try
        {
            SandboxToggle.IsChecked = value;
        }
        finally
        {
            _sandboxToggleSyncing = false;
        }
    }

    private async void OnSandboxToggleChanged()
    {
        if (_sandboxToggleSyncing)
        {
            return;
        }

        if (SandboxToggle.IsChecked == true)
        {
            _settings.RunInSandbox = true;
            ScheduleSettingsSave();
            return;
        }

        // Turning it off needs a yes; Cancel puts the switch back on.
        if (await ConfirmSandboxOffAsync())
        {
            _settings.RunInSandbox = false;
            ScheduleSettingsSave();
        }
        else
        {
            SetSandboxToggle(true);
        }
    }

    // Removes the folder permissions the sandbox added and its Windows profile (same as --sandbox-cleanup). The next
    // sandboxed launch grants again what it needs. Not while a game runs: it would lose access to its own folders.
    private async void OnSandboxCleanupClicked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (_isRunning)
        {
            AppendConsoleLine(Localization.Instance.Get("Options.SandboxCleanup.Running"), WarningLineBrush);
            return;
        }

        SandboxCleanupButton.IsEnabled = false;
        try
        {
            var output = new StringWriter();
            var exitCode = await Task.Run(() => OperatingSystem.IsWindows()
                ? WindowsAppContainerSandbox.Cleanup(SharpEmu.Libs.SharpEmuPaths.DataRoot, output)
                : 0);
            foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AppendConsoleLine(line, DimLineBrush);
            }

            AppendConsoleLine(
                Localization.Instance.Get(exitCode == 0 ? "Options.SandboxCleanup.Done" : "Options.SandboxCleanup.Failed"),
                exitCode == 0 ? SuccessLineBrush : WarningLineBrush);
        }
        finally
        {
            SandboxCleanupButton.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmSandboxOffAsync() =>
        await Sheets.ShowAsync(new SandboxOffConfirmDialog()) is true;

    // The sandboxed launch failed because the game folder is on exFAT/FAT: offer the way out instead of a console line.
    private async void ShowSandboxVolumeDialog(PendingLaunch launch, SandboxVolumeProblem problem)
    {
        var entry = _allGames.FirstOrDefault(game => game.Path.Equals(launch.EbootPath, GameLibraryPath.Comparison));
        var packable = entry is null
            ? null
            : GameVersions.PackableForGame(_allGames, entry)
                .FirstOrDefault(game => game.Path.Equals(launch.EbootPath, GameLibraryPath.Comparison));
        var canPlayWithout = !SandboxLaunchPolicy.IsForcedOn(
            Environment.GetEnvironmentVariable(SandboxLaunchPolicy.Variable));

        var choice = await Sheets.ShowAsync(new SandboxVolumeDialog(problem.Volume.FileSystem, packable is not null, canPlayWithout));
        if (choice is SandboxVolumeChoice.Convert && packable is not null)
        {
            await PackGameAsZarAsync(packable);
        }
        else if (choice is SandboxVolumeChoice.PlayWithoutSandbox && await ConfirmSandboxOffAsync() && !_isRunning)
        {
            _launchWithoutSandboxOnce = true;
            Launch(launch.EbootPath, launch.DisplayName, launch.TitleId);
        }
    }
}
